#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $CredentialFile,
    [switch] $UseEnvironmentKey,
    [switch] $SkipTests
)

# Only compiler outputs from a new directory enter a release. Never package a live launcher folder.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'ReleasePackage.psm1') -Force
[xml] $properties = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$version = [string] $properties.Project.PropertyGroup.Version
$displayVersion = [string] $properties.Project.PropertyGroup.EMLDisplayVersion
$runtimeVersion = [string] $properties.Project.PropertyGroup.EMLReleaseRuntimeVersion
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$' -or $displayVersion -notmatch '^[A-Za-z0-9.-]+$') {
    throw 'Invalid release identity in Directory.Build.props.'
}
if (-not $IsWindows) { throw 'Build the Windows desktop release on Windows.' }
# A public package must identify the exact committed source, not an unrecorded working-tree draft.
$sourceCommit = & git -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'A Git checkout with a committed source revision is required.' }
$sourceChanges = & git -C $projectRoot status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0 -or $sourceChanges) { throw 'Commit the reviewed source before creating a release package.' }
$sourceRepository = 'https://github.com/Ph1lK4/EML'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/releases' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "EML-$displayVersion-win-x64"
$zipPath = Join-Path $output ($packageName + '.zip')
$hashPath = $zipPath + '.sha256'
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $hashPath)) {
    throw 'This release already exists. Choose a new output directory; existing packages are never overwritten.'
}

# Use the existing encrypted developer input without copying or printing its contents.
$credentialArguments = @()
if ($UseEnvironmentKey) {
    if ($env:EML_EMBED_CURSEFORGE_KEY -ne '1' -or [string]::IsNullOrWhiteSpace($env:EML_CURSEFORGE_API_KEY)) {
        throw 'Environment authorization is not configured. No credential value will be printed.'
    }
} else {
    if ($env:EML_EMBED_CURSEFORGE_KEY -eq '1') { throw 'Use -UseEnvironmentKey explicitly for environment authorization.' }
    if (-not $CredentialFile) { $CredentialFile = Join-Path $projectRoot '.eml-build/curseforge.key.dpapi' }
    $CredentialFile = [IO.Path]::GetFullPath($CredentialFile)
    if (-not (Test-Path -LiteralPath $CredentialFile -PathType Leaf)) {
        throw 'Encrypted CurseForge build input is missing. Configure it with tools/Configure-CurseForge.ps1 first.'
    }
    if ($CredentialFile.Contains(';')) { throw 'The credential file path cannot contain an MSBuild list separator.' }
    $credentialArguments = @("-p:EMLCurseForgeCredentialFile=$CredentialFile")
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$buildRoot = Join-Path $output ('.release-build-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $buildRoot $packageName
New-Item -ItemType Directory -Path $payload | Out-Null
$application = Join-Path $payload 'app'
$documentation = Join-Path $payload 'docs'
New-Item -ItemType Directory -Path $documentation | Out-Null
$safeBuild = @('--nologo', '--tl:off', '-p:UseSharedCompilation=false', '-p:EmitCompilerGeneratedFiles=false',
    '-p:EmbedAllSources=false', '-p:EmbedUntrackedSources=false', '-p:DebugType=None', '-p:DebugSymbols=false') + $credentialArguments
$publishArguments = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:EMLReleasePackage=true') + $safeBuild
$project = Join-Path $projectRoot 'src/EML.App/EML.App.csproj'

Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        & dotnet test (Join-Path $projectRoot 'EML.slnx') -c Release --blame-hang-timeout 2min --blame-hang-dump-type none --logger "trx;LogFilePrefix=$displayVersion" --results-directory (Join-Path $buildRoot 'test-results') @safeBuild
        if ($LASTEXITCODE -ne 0) { throw 'Release tests failed; no package was created.' }
    }
    # Rebuild and publish use identical RID/runtime/credential options, avoiding stale authorization.
    & dotnet build $project @publishArguments -t:Rebuild
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed; no package was created.' }
    & dotnet publish $project @publishArguments --no-build --no-restore -o $application -p:EMLStructuredRelease=true "-p:EMLPackageDirectory=$payload"
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed; no package was created.' }

    foreach ($name in @('START-HERE.txt', 'UPGRADE.txt')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "release-assets/$name") -Destination (Join-Path $payload $name)
    }
    # Keep EML's own license and user-facing policies alongside third-party notices.
    foreach ($name in @('LICENSE', 'PRIVACY.md', 'SECURITY.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $documentation $name)
    }
    foreach ($name in @('uninstall.md', 'code-signing.md', 'release-rc1.md', 'release-rc2.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "docs/$name") -Destination (Join-Path $payload "docs/$name")
    }
    # 仓库根策略文件与 docs 合并打包后，仅修正这些已知本地文档链接，不改正文或外链。
    foreach ($file in Get-ChildItem -LiteralPath $documentation -File -Filter '*.md') {
        $text = [IO.File]::ReadAllText($file.FullName).Replace('(../PRIVACY.md)', '(PRIVACY.md)').Replace('(../SECURITY.md)', '(SECURITY.md)').Replace('(../LICENSE)', '(LICENSE)').Replace('(docs/uninstall.md)', '(uninstall.md)')
        [IO.File]::WriteAllText($file.FullName, $text, [Text.UTF8Encoding]::new($false))
    }
    @(
        'EML Launcher - GPL-3.0-or-later',
        'Copyright (C) 2026 K4ssia and contributors.',
        'This program is distributed WITHOUT ANY WARRANTY. See LICENSE for the full terms.',
        "Version: $version", "Source commit: $sourceCommit", "Repository: $sourceRepository",
        "Exact source: $sourceRepository/tree/$sourceCommit",
        "Source archive: $sourceRepository/archive/$sourceCommit.zip", '',
        'Build from the source archive on Windows with the SDK selected by global.json and PowerShell 7:',
        '  dotnet restore EML.slnx',
        '  dotnet build EML.slnx -c Release',
        '  dotnet run --project src/EML.App/EML.App.csproj -c Release',
        '  dotnet test EML.slnx -c Release',
        'The source includes tools/Build-Release.ps1 and the credential-free tools/Test-CI.ps1.',
        'CurseForge developer authorization is not in the public source; source builds can use their own authorization.',
        'Public Microsoft application configuration is included. No user account credentials are shipped.',
        'Third-party components retain their own licenses; see LICENSE-DOTNET.txt, LICENSE-MVVM.txt and THIRD-PARTY-NOTICES.txt.'
    ) -join "`r`n" | Set-Content -LiteralPath (Join-Path $documentation 'SOURCE.txt') -Encoding utf8NoBOM

    # Preserve upstream notices from restored packages and the SDK selected by global.json.
    $assets = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'src/EML.App/obj/project.assets.json') | ConvertFrom-Json -AsHashtable
    $packageFolders = @($assets.packageFolders.Keys)
    function Find-PackageText([string] $packageId, [string] $packageVersion, [string[]] $names) {
        foreach ($folder in $packageFolders) {
            $directory = Join-Path $folder ($packageId.ToLowerInvariant() + '/' + $packageVersion)
            foreach ($name in $names) {
                $candidate = Join-Path $directory $name
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
            }
        }
        throw "Required upstream notice was not found for $packageId/$packageVersion."
    }
    $runtimeLicense = Find-PackageText 'Microsoft.NETCore.App.Runtime.win-x64' $runtimeVersion @('LICENSE.TXT', 'LICENSE.txt')
    $runtimeNotices = Find-PackageText 'Microsoft.NETCore.App.Runtime.win-x64' $runtimeVersion @('THIRD-PARTY-NOTICES.TXT', 'ThirdPartyNotices.txt')
    $desktopLicense = Find-PackageText 'Microsoft.WindowsDesktop.App.Runtime.win-x64' $runtimeVersion @('LICENSE.TXT', 'LICENSE.txt', 'LICENSE')
    # The desktop runtime NuGet package supplies LICENSE but no notices file;
    # keep the official WindowsDesktop SDK notices as well as the runtime notices.
    $sdkPath = (& dotnet msbuild $project -nologo -getProperty:MSBuildSDKsPath | Select-Object -Last 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkPath)) { throw 'Cannot locate the selected WindowsDesktop SDK.' }
    $desktopNotices = Join-Path $sdkPath.Trim() 'Microsoft.NET.Sdk.WindowsDesktop/THIRD-PARTY-NOTICES.TXT'
    if (-not (Test-Path -LiteralPath $desktopNotices -PathType Leaf)) { throw 'Official WindowsDesktop notices are missing.' }
    $mvvmLibrary = @($assets.libraries.Keys | Where-Object { $_ -like 'CommunityToolkit.Mvvm/*' })
    if ($mvvmLibrary.Count -ne 1) { throw 'Expected exactly one CommunityToolkit.Mvvm package.' }
    $mvvmVersion = $mvvmLibrary[0].Split('/')[1]
    $mvvmLicense = Find-PackageText 'CommunityToolkit.Mvvm' $mvvmVersion @('License.md', 'LICENSE.md', 'LICENSE.txt', 'LICENSE')
    $mvvmNotices = Find-PackageText 'CommunityToolkit.Mvvm' $mvvmVersion @('ThirdPartyNotices.txt')
    @('Microsoft.NETCore.App:', (Get-Content -Raw -LiteralPath $runtimeLicense), 'Microsoft.WindowsDesktop.App:',
        (Get-Content -Raw -LiteralPath $desktopLicense)) -join "`r`n`r`n" |
        Set-Content -LiteralPath (Join-Path $documentation 'LICENSE-DOTNET.txt') -Encoding utf8NoBOM
    @('Microsoft.NETCore.App:', (Get-Content -Raw -LiteralPath $runtimeNotices), 'Microsoft.NET.Sdk.WindowsDesktop:',
        (Get-Content -Raw -LiteralPath $desktopNotices), 'CommunityToolkit.Mvvm:',
        (Get-Content -Raw -LiteralPath $mvvmNotices)) -join "`r`n`r`n" |
        Set-Content -LiteralPath (Join-Path $documentation 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
    Copy-Item -LiteralPath $mvvmLicense -Destination (Join-Path $documentation 'LICENSE-MVVM.txt')

    $audit = Test-EmLReleaseLayout -Path $payload -Version $version -DisplayVersion $displayVersion -RuntimeVersion $runtimeVersion -Layout AppDirectory
    $entries = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1; version = $version; displayVersion = $displayVersion; runtimeIdentifier = 'win-x64'
        layout = 'app-directory'; entryPoint = 'EML.exe'; applicationDirectory = 'app'; dataDirectory = '.'
        selfContained = $true; runtimeVersion = $runtimeVersion; createdUtc = [DateTime]::UtcNow.ToString('O')
        sourceCommit = $sourceCommit; sourceRepository = $sourceRepository
        automatedTestsRun = -not $SkipTests; cleanMachineAcceptance = 'pending'; codeSigned = $false
        authorization = 'CurseForge credential intentionally embedded; extractable from a desktop application.'
        files = $entries
    }
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'release-manifest.json') -Encoding utf8NoBOM
    $audit = Test-EmLReleaseLayout -Path $payload -Version $version -DisplayVersion $displayVersion -RuntimeVersion $runtimeVersion -Layout AppDirectory
    & (Join-Path $PSScriptRoot 'Test-ReleasePackage.Tests.ps1') -PackagePath $payload -Version $version -DisplayVersion $displayVersion -Layout AppDirectory
    & (Join-Path $PSScriptRoot 'Test-ReleaseRuntime.ps1') -Path $payload -DisplayVersion $displayVersion

    # Building must not change the committed input while its hash is being stamped into the package.
    $finalCommit = & git -C $projectRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $finalCommit -ne $sourceCommit) { throw 'Source revision changed during the release build.' }
    $finalChanges = & git -C $projectRoot status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $finalChanges) { throw 'Source files changed during the release build.' }

    $temporaryZip = Join-Path $buildRoot ($packageName + '.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $true)
    # Verify all payload bytes in the ZIP, including its manifest, before assigning the final filename.
    $zip = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
    try {
        $expected = @{}
        foreach ($entry in $entries) { $expected["$packageName/$($entry.path)"] = $entry.sha256 }
        $expected["$packageName/release-manifest.json"] = (Get-FileHash -LiteralPath (Join-Path $payload 'release-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName)) { throw 'Unexpected or duplicate ZIP entry.' }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($hash -ne $expected[$entry.FullName]) { throw 'ZIP content hash verification failed.' }
        }
        if ($seen.Count -ne $expected.Count) { throw 'ZIP is missing release files.' }
    } finally { $zip.Dispose() }
    [IO.File]::Move($temporaryZip, $zipPath, $false)
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($hashPath, "$zipHash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ Package = $zipPath; SHA256 = $zipHash; PublishedFiles = $audit; BuildDirectory = $buildRoot; CleanMachineAcceptance = 'pending' }
} finally {
    Pop-Location
}
