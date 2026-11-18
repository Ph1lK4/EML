#Requires -Version 7.0
[CmdletBinding()]
param([string] $PackagePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePackage.psm1') -Force
$checks = 0

# Assertions report only the test case label, never file contents or credentials.
function Assert-Accepted([string] $Name, [scriptblock] $Action) {
    & $Action
    $script:checks++
}
function Assert-Rejected([string] $Name, [scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected release policy rejection: $Name" }
    $script:checks++
}

foreach ($name in @('EML.App.exe', 'EML.Core.dll', 'Microsoft.CSharp.dll', 'System.Private.CoreLib.dll', 'System.dll', 'netstandard.dll', 'mscorlib.dll', 'msquic.dll', 'coreclr.dll', 'START-HERE.txt', 'microsoft-login.json', 'zh-Hans/PresentationFramework.resources.dll', 'LICENSE', 'SOURCE.txt', 'PRIVACY.md', 'SECURITY.md', 'docs/uninstall.md', 'docs/code-signing.md', 'docs/release-rc1.md')) {
    Assert-Accepted $name { Test-EmlReleasePath -RelativePath $name }
}
foreach ($name in @('instances', 'settings', 'runtimes', 'cache', '.eml-runtime', '.eml-build', 'accounts', 'tests', '.git', 'bin', 'obj')) {
    Assert-Rejected $name { Test-EmlReleasePath -RelativePath $name -Directory }
}
foreach ($name in @('private.log', 'session.dpapi', 'curseforge.key', 'trace.binlog', 'EML.App.pdb', 'EML.Tests.dll', 'xunit.assert.dll', 'credentials.json', 'settings.json', 'Program.cs', 'private.txt', 'third-party.exe', 'user-data.dll', '../EML.App.dll', 'C:/EML.App.dll', 'zh-Hans/private.txt', 'zh-Hans/subdir/System.resources.dll', 'docs/private.txt', 'docs/nested/uninstall.md')) {
    Assert-Rejected $name { Test-EmlReleasePath -RelativePath $name }
}

if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $source = [IO.Path]::GetFullPath($PackagePath)
    Test-EmlReleaseLayout -Path $source | Out-Null
    $checks++
    # These are disposable test fixtures copied from the release. The source package
    # and the user's installations are never changed or cleaned up by this test.
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('eml-release-policy-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $fixture = Join-Path $temporary 'fixture'
    New-Item -ItemType Directory -Path $fixture | Out-Null
    foreach ($item in Get-ChildItem -Force -LiteralPath $source) { Copy-Item -LiteralPath $item.FullName -Destination $fixture -Recurse }
    $fakeLog = Join-Path $fixture 'private.log'
    [IO.File]::WriteAllText($fakeLog, 'Synthetic test data; not a real user log.')
    Assert-Rejected 'injected private log' { Test-EmlReleaseLayout -Path $fixture }
    Remove-Item -LiteralPath $fakeLog
    $configuration = Join-Path $fixture 'microsoft-login.json'
    [IO.File]::WriteAllText($configuration, '{"clientId":"11111111-1111-1111-1111-111111111111","accessToken":"synthetic-not-real"}')
    Assert-Rejected 'injected credential in public config' { Test-EmlReleaseLayout -Path $fixture }
    Copy-Item -LiteralPath (Join-Path $source 'microsoft-login.json') -Destination $configuration -Force

    # 每个故障从有效包开始，检查失败后恢复测试副本，避免前一个故障掩盖下一个。
    $runtimeFile = Join-Path $fixture 'EML.App.runtimeconfig.json'
    $originalRuntime = [IO.File]::ReadAllText($runtimeFile)
    try {
        $runtime = $originalRuntime | ConvertFrom-Json -AsHashtable
        $runtime.runtimeOptions.Remove('includedFrameworks') | Out-Null
        $runtime.runtimeOptions.framework = @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.13' }
        [IO.File]::WriteAllText($runtimeFile, ($runtime | ConvertTo-Json -Depth 10))
        Assert-Rejected 'framework-dependent package' { Test-EmlReleaseLayout -Path $fixture }
        $runtime = $originalRuntime | ConvertFrom-Json -AsHashtable
        $runtime.runtimeOptions.includedFrameworks[0].version = '10.0.12'
        [IO.File]::WriteAllText($runtimeFile, ($runtime | ConvertTo-Json -Depth 10))
        Assert-Rejected 'wrong bundled runtime' { Test-EmlReleaseLayout -Path $fixture }
    } finally { Copy-Item -LiteralPath (Join-Path $source 'EML.App.runtimeconfig.json') -Destination $runtimeFile -Force }

    $native = Join-Path $fixture 'hostfxr.dll'
    $heldNative = Join-Path $temporary 'held-hostfxr.dll'
    Move-Item -LiteralPath $native -Destination $heldNative
    try { Assert-Rejected 'missing native host' { Test-EmlReleaseLayout -Path $fixture } }
    finally { Move-Item -LiteralPath $heldNative -Destination $native }
    foreach ($requiredNotice in @('LICENSE', 'SOURCE.txt', 'PRIVACY.md', 'SECURITY.md', 'docs/uninstall.md')) {
        $noticePath = Join-Path $fixture $requiredNotice
        $heldNotice = Join-Path $temporary ('held-' + [IO.Path]::GetFileName($requiredNotice))
        Move-Item -LiteralPath $noticePath -Destination $heldNotice
        try { Assert-Rejected "missing $requiredNotice" { Test-EmlReleaseLayout -Path $fixture } }
        finally { Move-Item -LiteralPath $heldNotice -Destination $noticePath }
    }
    $notice = Join-Path $fixture 'THIRD-PARTY-NOTICES.txt'
    $noticeText = [IO.File]::ReadAllText($notice)
    try {
        [IO.File]::AppendAllText($notice, 'Synthetic tamper')
        Assert-Rejected 'payload hash mismatch' { Test-EmlReleaseLayout -Path $fixture }
    } finally { [IO.File]::WriteAllText($notice, $noticeText) }

    $manifestFile = Join-Path $fixture 'release-manifest.json'
    $heldManifest = Join-Path $temporary 'held-manifest.json'
    Move-Item -LiteralPath $manifestFile -Destination $heldManifest
    try { Assert-Rejected 'missing distributed manifest' { & (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') -Path $fixture } }
    finally { Move-Item -LiteralPath $heldManifest -Destination $manifestFile }
    $manifestText = [IO.File]::ReadAllText($manifestFile)
    try {
        $manifest = $manifestText | ConvertFrom-Json -AsHashtable
        $manifest.files += $manifest.files[0]
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'duplicate manifest entry' { Test-EmlReleaseLayout -Path $fixture }
        $manifest = $manifestText | ConvertFrom-Json -AsHashtable
        $manifest.files = @($manifest.files | Select-Object -Skip 1)
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'incomplete manifest' { Test-EmlReleaseLayout -Path $fixture }
        $manifest = $manifestText | ConvertFrom-Json -AsHashtable
        $manifest.sourceCommit = 'not-a-source-revision'
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'invalid source revision' { Test-EmlReleaseLayout -Path $fixture }
        $manifest.sourceCommit = '0' * 40
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'source notice mismatch' { Test-EmlReleaseLayout -Path $fixture }
    } finally { [IO.File]::WriteAllText($manifestFile, $manifestText) }
    Assert-Accepted 'restored fixture' { Test-EmlReleaseLayout -Path $fixture | Out-Null }

    # 按文档演练原路径合并升级，只使用合成玩家数据，不读取真实实例或账号。
    $preserved = @{}
    foreach ($relative in @('instances/测试实例/saves/世界/level.dat',
        'instances/测试实例/.eml-runtime/launch/settings.json', 'instances/测试实例/.eml-runtime/migration/backup.dat',
        'settings/downloads.json', 'runtimes/java/synthetic-java.txt', 'cache/original-pack.zip',
        '.eml-runtime/sentinel.txt', '.eml-imports/sentinel.txt')) {
        $file = Join-Path $fixture $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($file)) | Out-Null
        [IO.File]::WriteAllText($file, "Synthetic user data: $relative")
        $preserved[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    foreach ($item in Get-ChildItem -Force -LiteralPath $source) { Copy-Item -LiteralPath $item.FullName -Destination $fixture -Recurse -Force }
    foreach ($file in $preserved.Keys) {
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $preserved[$file]) {
            throw 'Merge upgrade modified synthetic user data.'
        }
        $checks++
    }
    Write-Output "Synthetic fixture retained for inspection: $temporary"
}
Write-Output "Release package checks passed: $checks"
