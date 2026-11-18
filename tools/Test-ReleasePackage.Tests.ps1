#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $PackagePath,
    [string] $Version = '1.0.0-rc.2',
    [string] $DisplayVersion = '1.0-RC2',
    [string] $RuntimeVersion = '10.0.13',
    [ValidateSet('Flat', 'AppDirectory')][string] $Layout = 'AppDirectory'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ReleasePackage.psm1') -Force
$checks = 0
$auditArguments = @{ Version = $Version; DisplayVersion = $DisplayVersion; RuntimeVersion = $RuntimeVersion; Layout = $Layout }
$programPrefix = if ($Layout -eq 'AppDirectory') { 'app/' } else { '' }
$documentPrefix = if ($Layout -eq 'AppDirectory') { 'docs/' } else { '' }
$entryPoint = if ($Layout -eq 'AppDirectory') { 'EML.exe' } else { 'EML.App.exe' }

# Apply one explicit release identity to every fixture; no negative test may fail only on a version mismatch.
function Test-Package([string] $Path) {
    Test-EmlReleaseLayout -Path $Path @auditArguments
}

# Assertions report only the test case label, never file contents or credentials.
function Assert-Accepted([string] $CaseName, [scriptblock] $Action) {
    & $Action
    $script:checks++
}
function Assert-Rejected([string] $CaseName, [scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected release policy rejection: $CaseName" }
    $script:checks++
}

foreach ($name in @('EML.App.exe', 'EML.Core.dll', 'Microsoft.CSharp.dll', 'System.Private.CoreLib.dll', 'System.dll', 'netstandard.dll', 'mscorlib.dll', 'msquic.dll', 'coreclr.dll', 'START-HERE.txt', 'microsoft-login.json', 'zh-Hans/PresentationFramework.resources.dll', 'LICENSE', 'SOURCE.txt', 'PRIVACY.md', 'SECURITY.md', 'docs/uninstall.md', 'docs/code-signing.md', 'docs/release-rc1.md')) {
    Assert-Accepted "flat $name" { Test-EmlReleasePath -RelativePath $name -Layout Flat }
}
foreach ($name in @('instances', 'settings', 'runtimes', 'cache', '.eml-runtime', '.eml-build', 'accounts', 'tests', '.git', 'bin', 'obj')) {
    Assert-Rejected "flat $name" { Test-EmlReleasePath -RelativePath $name -Directory -Layout Flat }
    Assert-Rejected "structured root $name" { Test-EmlReleasePath -RelativePath $name -Directory -Layout AppDirectory }
    Assert-Rejected "structured program $name" { Test-EmlReleasePath -RelativePath "app/$name" -Directory -Layout AppDirectory }
}
foreach ($name in @('private.log', 'session.dpapi', 'curseforge.key', 'trace.binlog', 'EML.App.pdb', 'EML.Tests.dll', 'xunit.assert.dll', 'credentials.json', 'settings.json', 'Program.cs', 'private.txt', 'third-party.exe', 'user-data.dll', '../EML.App.dll', 'C:/EML.App.dll', 'zh-Hans/private.txt', 'zh-Hans/subdir/System.resources.dll', 'docs/private.txt', 'docs/nested/uninstall.md')) {
    Assert-Rejected "flat $name" { Test-EmlReleasePath -RelativePath $name -Layout Flat }
    Assert-Rejected "structured $name" { Test-EmlReleasePath -RelativePath "app/$name" -Layout AppDirectory }
}
foreach ($name in @('EML.exe', 'START-HERE.txt', 'UPGRADE.txt', 'release-manifest.json',
    'app/EML.App.dll', 'app/EML.Core.dll', 'app/EML.Infrastructure.dll', 'app/EML.App.runtimeconfig.json',
    'app/EML.App.deps.json', 'app/microsoft-login.json', 'app/eml-layout.json', 'app/coreclr.dll', 'app/hostfxr.dll',
    'app/CommunityToolkit.Mvvm.dll', 'app/createdump.exe', 'app/zh-Hans/PresentationFramework.resources.dll',
    'app/ko/System.Windows.Forms.resources.dll', 'docs/LICENSE', 'docs/SOURCE.txt', 'docs/PRIVACY.md',
    'docs/SECURITY.md', 'docs/LICENSE-DOTNET.txt', 'docs/LICENSE-MVVM.txt', 'docs/THIRD-PARTY-NOTICES.txt',
    'docs/uninstall.md', 'docs/code-signing.md', 'docs/release-rc1.md', 'docs/release-rc2.md')) {
    Assert-Accepted "structured $name" { Test-EmlReleasePath -RelativePath $name -Layout AppDirectory }
}
foreach ($name in @('app', 'docs', 'app/zh-Hans', 'app/ko', 'app/cs')) {
    Assert-Accepted "structured directory $name" { Test-EmlReleasePath -RelativePath $name -Directory -Layout AppDirectory }
}
foreach ($name in @('EML.App.exe', 'EML.App.dll', 'System.dll', 'microsoft-login.json', 'eml-layout.json',
    'LICENSE', 'SOURCE.txt', 'app/EML.exe', 'app/EML.App.exe', 'app/app/EML.App.dll',
    'app/app/System.resources.dll', 'app/docs/LICENSE', 'app/release-manifest.json', 'app/START-HERE.txt',
    'app/LICENSE', 'app/PRIVACY.md', 'app/private.json', 'app/settings/downloads.json', 'app/accounts/microsoft.dpapi',
    'app/instances/world.dat', 'app/zh-Hans/private.txt', 'app/zh-Hans/nested/System.resources.dll',
    'app/zh-Hans/EML.App.dll', 'docs/private.txt', 'docs/secret.key', 'docs/logs/trace.log',
    'docs/EML.App.dll', 'docs/nested/SOURCE.txt', 'app/../EML.App.dll', 'App/EML.App.dll',
    'app//EML.App.dll', 'app/C:/EML.App.dll')) {
    Assert-Rejected "structured $name" { Test-EmlReleasePath -RelativePath $name -Layout AppDirectory }
}
foreach ($name in @('zh-Hans', 'ko', 'app/app', 'app/docs', 'app/zh-Hans/nested', 'docs/zh-Hans',
    'app/instances', 'docs/accounts', 'App', 'app/../docs')) {
    Assert-Rejected "structured directory $name" { Test-EmlReleasePath -RelativePath $name -Directory -Layout AppDirectory }
}

if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $source = [IO.Path]::GetFullPath($PackagePath)
    Test-Package $source | Out-Null
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
    Assert-Rejected 'injected private log' { Test-Package $fixture }
    Remove-Item -LiteralPath $fakeLog
    $configurationRelative = "${programPrefix}microsoft-login.json"
    $configuration = Join-Path $fixture $configurationRelative
    [IO.File]::WriteAllText($configuration, '{"clientId":"11111111-1111-1111-1111-111111111111","accessToken":"synthetic-not-real"}')
    Assert-Rejected 'injected credential in public config' { Test-Package $fixture }
    Copy-Item -LiteralPath (Join-Path $source $configurationRelative) -Destination $configuration -Force

    # 每个故障从有效包开始，检查失败后恢复测试副本，避免前一个故障掩盖下一个。
    $runtimeRelative = "${programPrefix}EML.App.runtimeconfig.json"
    $runtimeFile = Join-Path $fixture $runtimeRelative
    $originalRuntime = [IO.File]::ReadAllText($runtimeFile)
    try {
        $runtime = $originalRuntime | ConvertFrom-Json -AsHashtable
        $runtime.runtimeOptions.Remove('includedFrameworks') | Out-Null
        $runtime.runtimeOptions.framework = @{ name = 'Microsoft.WindowsDesktop.App'; version = $RuntimeVersion }
        [IO.File]::WriteAllText($runtimeFile, ($runtime | ConvertTo-Json -Depth 10))
        Assert-Rejected 'framework-dependent package' { Test-Package $fixture }
        $runtime = $originalRuntime | ConvertFrom-Json -AsHashtable
        $runtime.runtimeOptions.includedFrameworks[0].version = '10.0.12'
        [IO.File]::WriteAllText($runtimeFile, ($runtime | ConvertTo-Json -Depth 10))
        Assert-Rejected 'wrong bundled runtime' { Test-Package $fixture }
    } finally { Copy-Item -LiteralPath (Join-Path $source $runtimeRelative) -Destination $runtimeFile -Force }

    $native = Join-Path $fixture "${programPrefix}hostfxr.dll"
    $heldNative = Join-Path $temporary 'held-hostfxr.dll'
    Move-Item -LiteralPath $native -Destination $heldNative
    try { Assert-Rejected 'missing native host' { Test-Package $fixture } }
    finally { Move-Item -LiteralPath $heldNative -Destination $native }
    foreach ($requiredNotice in @("${documentPrefix}LICENSE", "${documentPrefix}SOURCE.txt", "${documentPrefix}PRIVACY.md", "${documentPrefix}SECURITY.md", 'docs/uninstall.md', $entryPoint, "${programPrefix}EML.App.dll")) {
        $noticePath = Join-Path $fixture $requiredNotice
        $heldNotice = Join-Path $temporary ('held-' + [IO.Path]::GetFileName($requiredNotice))
        Move-Item -LiteralPath $noticePath -Destination $heldNotice
        try { Assert-Rejected "missing $requiredNotice" { Test-Package $fixture } }
        finally { Move-Item -LiteralPath $heldNotice -Destination $noticePath }
    }
    $notice = Join-Path $fixture "${documentPrefix}THIRD-PARTY-NOTICES.txt"
    $noticeText = [IO.File]::ReadAllText($notice)
    try {
        [IO.File]::AppendAllText($notice, 'Synthetic tamper')
        Assert-Rejected 'payload hash mismatch' { Test-Package $fixture }
    } finally { [IO.File]::WriteAllText($notice, $noticeText) }

    $manifestFile = Join-Path $fixture 'release-manifest.json'
    $heldManifest = Join-Path $temporary 'held-manifest.json'
    Move-Item -LiteralPath $manifestFile -Destination $heldManifest
    try { Assert-Rejected 'missing distributed manifest' { & (Join-Path $PSScriptRoot 'Test-ReleasePackage.ps1') -Path $fixture @auditArguments } }
    finally { Move-Item -LiteralPath $heldManifest -Destination $manifestFile }
    $manifestText = [IO.File]::ReadAllText($manifestFile)
    try {
        $manifest = $manifestText | ConvertFrom-Json -AsHashtable
        $manifest.files += $manifest.files[0]
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'duplicate manifest entry' { Test-Package $fixture }
        $manifest = $manifestText | ConvertFrom-Json -AsHashtable
        $manifest.files = @($manifest.files | Select-Object -Skip 1)
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'incomplete manifest' { Test-Package $fixture }
        $manifest = $manifestText | ConvertFrom-Json -AsHashtable
        $manifest.sourceCommit = 'not-a-source-revision'
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'invalid source revision' { Test-Package $fixture }
        $manifest.sourceCommit = '0' * 40
        [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
        Assert-Rejected 'source notice mismatch' { Test-Package $fixture }
        if ($Layout -eq 'AppDirectory') {
            foreach ($field in @('layout', 'entryPoint', 'applicationDirectory', 'dataDirectory')) {
                $manifest = $manifestText | ConvertFrom-Json -AsHashtable
                $manifest[$field] = 'unexpected-value'
                [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
                Assert-Rejected "incorrect manifest $field" { Test-Package $fixture }
                $manifest.Remove($field)
                [IO.File]::WriteAllText($manifestFile, ($manifest | ConvertTo-Json -Depth 10))
                Assert-Rejected "missing manifest $field" { Test-Package $fixture }
            }
        }
    } finally { [IO.File]::WriteAllText($manifestFile, $manifestText) }
    if ($Layout -eq 'AppDirectory') {
        $markerPath = Join-Path $fixture 'app/eml-layout.json'
        $markerBytes = [IO.File]::ReadAllBytes($markerPath)
        # Hide only the disposable fixture's manifest so marker rejections cannot be hash failures.
        Move-Item -LiteralPath $manifestFile -Destination $heldManifest
        try {
            Assert-Accepted 'structured fixture before manifest' { Test-Package $fixture | Out-Null }
            foreach ($invalidMarker in @('{}', '[]', '{',
                '{"schemaVersion":1,"layout":"app-directory","dataDirectory":"../.."}',
                '{"schemaVersion":1,"layout":"flat","dataDirectory":".."}',
                '{"schemaVersion":"1","layout":"app-directory","dataDirectory":".."}',
                '{"schemaVersion":1,"layout":"app-directory","dataDirectory":"..","extra":true}',
                '{"schemaVersion":1,"layout":"app-directory","dataDirectory":"..","layout":"app-directory"}',
                '{"schemaVersion":1,"layout":"app-directory","DataDirectory":".."}',
                (' ' * 1025))) {
                [IO.File]::WriteAllText($markerPath, $invalidMarker)
                Assert-Rejected 'invalid structured marker without hash validation' { Test-Package $fixture }
            }
            $heldMarker = Join-Path $temporary 'held-marker.json'
            Move-Item -LiteralPath $markerPath -Destination $heldMarker
            try { Assert-Rejected 'missing structured marker' { Test-Package $fixture } }
            finally { Move-Item -LiteralPath $heldMarker -Destination $markerPath }
        } finally {
            [IO.File]::WriteAllBytes($markerPath, $markerBytes)
            Move-Item -LiteralPath $heldManifest -Destination $manifestFile
        }
    }
    Assert-Accepted 'restored fixture' { Test-Package $fixture | Out-Null }

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
    if ($Layout -eq 'AppDirectory') {
        # A merge upgrade must not silently remove even old RC1 program files.
        $oldEntry = Join-Path $fixture 'EML.App.exe'
        [IO.File]::WriteAllText($oldEntry, 'Synthetic previous RC1 executable, not a runnable program.')
        $preserved[$oldEntry] = (Get-FileHash -LiteralPath $oldEntry -Algorithm SHA256).Hash
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
