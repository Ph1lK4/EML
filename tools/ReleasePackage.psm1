#Requires -Version 7.0
Set-StrictMode -Version Latest

# Validate a package-relative path before inspecting its contents. This allow-list is
# intentionally independent of whatever personal files happen to exist on the build PC.
function Test-EmlReleasePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RelativePath,
        [switch] $Directory,
        [ValidateSet('Flat', 'AppDirectory')][string] $Layout = 'Flat'
    )
    $path = $RelativePath.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($path) -or $path -match '(^/|[:\x00-\x1f]|//|/$)' -or
        @($path.Split('/') | Where-Object { $_ -in @('.', '..', '') }).Count -gt 0) {
        throw "Invalid release path: $RelativePath"
    }
    $parts = $path.Split('/')
    foreach ($part in $parts) {
        if ($part -imatch '^\.eml-' -or $part -iin @('instances', 'settings', 'runtimes', 'cache', 'accounts', 'logs', 'log', 'tests', 'testresults', 'bin', 'obj', '.git', '.vs')) {
            throw "Personal data or development directory is forbidden in a release: $path"
        }
    }
    $culturePattern = '^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$'
    $documents = @('LICENSE', 'SOURCE.txt', 'PRIVACY.md', 'SECURITY.md',
        'LICENSE-DOTNET.txt', 'LICENSE-MVVM.txt', 'THIRD-PARTY-NOTICES.txt',
        'uninstall.md', 'code-signing.md', 'release-rc1.md', 'release-rc2.md')
    if ($Directory) {
        if ($path -ceq 'docs') { return }
        if ($Layout -eq 'AppDirectory') {
            if ($path -ceq 'app') { return }
            if ($parts.Count -eq 2 -and $parts[0] -ceq 'app' -and
                $parts[1] -cnotin @('app', 'docs') -and $parts[1] -cmatch $culturePattern) { return }
        } elseif ($parts.Count -eq 1 -and $parts[0] -cmatch $culturePattern) { return }
        throw "Unexpected release directory: $path"
    }
    $name = $parts[-1]
    if ($name -imatch '(testhost|\.tests?\.|xunit|nunit|coverlet|testplatform|codecoverage)' -or
        $name -imatch '\.(pdb|log|binlog|dpapi|key|pfx|pem|cs|xaml|csproj|slnx|user|zip|jar|mrpack)$') {
        throw "Private or development file is forbidden in a release: $path"
    }
    if ($Layout -eq 'AppDirectory') {
        if ($parts.Count -eq 1 -and $name -cin @('EML.exe', 'START-HERE.txt', 'UPGRADE.txt', 'release-manifest.json')) { return }
        if ($parts.Count -eq 2 -and $parts[0] -ceq 'docs' -and $name -cin $documents) { return }
        if ($parts.Count -lt 2 -or $parts[0] -cne 'app') { throw "File is outside the structured release allow-list: $path" }
        # Remove only the exact program prefix, after rejecting all private path segments.
        $parts = @($parts | Select-Object -Skip 1)
        if ($parts.Count -eq 1 -and $name -ceq 'eml-layout.json') { return }
        if ($name -cin @('EML.App.exe', 'START-HERE.txt', 'UPGRADE.txt', 'release-manifest.json') -or $name -cin $documents) {
            throw "File is in the wrong release directory: $path"
        }
    }
    if ($parts.Count -eq 2) {
        if ($Layout -eq 'Flat' -and $path -cin @('docs/uninstall.md', 'docs/code-signing.md', 'docs/release-rc1.md')) { return }
        if ($parts[0] -cin @('app', 'docs') -or $parts[0] -cnotmatch $culturePattern -or
            $name -cnotmatch '^(?:System\..+|Microsoft\..+|Presentation.+|WindowsBase|WindowsFormsIntegration|ReachFramework|UIAutomation.+|Accessibility)\.resources\.dll$') {
            throw "Only runtime satellite assemblies are allowed below the program directory: $path"
        }
        return
    }
    if ($parts.Count -ne 1) { throw "Unexpected nested release file: $path" }
    $fixedFiles = @(
        'EML.App.exe', 'EML.App.dll', 'EML.Core.dll', 'EML.Infrastructure.dll',
        'CommunityToolkit.Mvvm.dll', 'EML.App.deps.json', 'EML.App.runtimeconfig.json',
        'microsoft-login.json', 'release-manifest.json', 'START-HERE.txt', 'UPGRADE.txt',
        'THIRD-PARTY-NOTICES.txt', 'LICENSE-DOTNET.txt', 'LICENSE-MVVM.txt',
        'LICENSE', 'SOURCE.txt', 'PRIVACY.md', 'SECURITY.md',
        'createdump.exe', 'System.dll', 'mscorlib.dll', 'netstandard.dll'
    )
    if ($name -cin $fixedFiles) { return }
    if ($name -cmatch '^(?:System\..+|Microsoft\..+|Presentation.+|WindowsBase|WindowsFormsIntegration|ReachFramework|UIAutomation.+|Accessibility)\.dll$') { return }
    $nativeRuntimeFiles = @(
        'coreclr.dll', 'clrjit.dll', 'clrgc.dll', 'clrgcexp.dll', 'clretwrc.dll', 'clrcompression.dll',
        'mscorrc.dll', 'msquic.dll', 'mscordaccore_amd64_amd64_10.0.1326.50504.dll',
        'hostfxr.dll', 'hostpolicy.dll', 'mscordaccore.dll', 'mscordbi.dll', 'dbgshim.dll',
        'D3DCompiler_47_cor3.dll', 'DirectWriteForwarder.dll', 'PenImc_cor3.dll',
        'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'msvcp140_cor3.dll', 'wpfgfx_cor3.dll'
    )
    if ($name -cin $nativeRuntimeFiles) { return }
    throw "File is not on the release allow-list: $path"
}

# Walk only ordinary directories. A link is rejected before recursion, so a package
# cannot silently pull content from a user's account or another part of the machine.
function Get-EmlReleaseFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [ValidateSet('Flat', 'AppDirectory')][string] $Layout = 'Flat'
    )
    $root = [IO.Path]::GetFullPath($Path)
    $rootItem = Get-Item -Force -LiteralPath $root -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Release root must be an ordinary directory.'
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -Force -LiteralPath $pending.Pop() -ErrorAction Stop) {
            $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Links are forbidden in a release: $relative"
            }
            Test-EmlReleasePath -RelativePath $relative -Directory:$item.PSIsContainer -Layout $Layout
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $item }
        }
    }
}

# Read bounded JSON without evaluating code or printing configuration contents.
function Read-EmlReleaseJson {
    param([Parameter(Mandatory)][string] $Path, [long] $MaximumBytes = 8388608)
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.Length -le 0 -or $file.Length -gt $MaximumBytes) {
        throw "Release JSON has an invalid size: $($file.Name)"
    }
    try { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { throw "Release JSON is malformed: $($file.Name)" }
}

# Inspect the PE header directly; do not load or execute the candidate executable.
function Assert-EmlAmd64Executable {
    param([Parameter(Mandatory)][string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'Release apphost is not a PE executable.' }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw 'Release apphost has an invalid PE header.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw 'Release apphost must target Windows AMD64 (win-x64).'
        }
    } finally { $reader.Dispose() }
}

# The marker is an exact layout declaration, not a configurable path redirect.
# Inspect the JSON properties directly so duplicate or differently cased keys cannot be hidden.
function Assert-EmlLayoutMarker {
    param([Parameter(Mandatory)][string] $Path)
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $file.Length -le 0 -or $file.Length -gt 1024) { throw 'Release layout marker has an invalid size or file type.' }
    try { $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path)) }
    catch { throw 'Release layout marker is malformed.' }
    try {
        $element = $document.RootElement
        if ($element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Release layout marker must be an object.' }
        $properties = @($element.EnumerateObject())
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $properties) {
            if (-not $names.Add($property.Name) -or $property.Name -cnotin @('schemaVersion', 'layout', 'dataDirectory')) {
                throw 'Release layout marker contains unknown or duplicate fields.'
            }
        }
        $schemaVersion = 0
        if ($properties.Count -ne 3 -or
            $element.GetProperty('schemaVersion').ValueKind -ne [Text.Json.JsonValueKind]::Number -or
            -not $element.GetProperty('schemaVersion').TryGetInt32([ref]$schemaVersion) -or $schemaVersion -ne 1 -or
            $element.GetProperty('layout').ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $element.GetProperty('layout').GetString() -cne 'app-directory' -or
            $element.GetProperty('dataDirectory').ValueKind -ne [Text.Json.JsonValueKind]::String -or
            $element.GetProperty('dataDirectory').GetString() -cne '..') {
            throw 'Release layout marker does not match the app-directory layout.'
        }
    } finally { $document.Dispose() }
}

# Audit the complete fresh publish directory. Return only public build metadata;
# never read credential stores or disclose tokens from a rejected file.
function Test-EmlReleaseLayout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [string] $Version = '1.0.0-rc.2',
        [string] $DisplayVersion = '1.0-RC2',
        [string] $RuntimeVersion = '10.0.13',
        [ValidateSet('Flat', 'AppDirectory')][string] $Layout = 'AppDirectory'
    )
    $root = [IO.Path]::GetFullPath($Path)
    $files = @(Get-EmlReleaseFiles -Path $root -Layout $Layout)
    $programPrefix = if ($Layout -eq 'AppDirectory') { 'app/' } else { '' }
    $documentPrefix = if ($Layout -eq 'AppDirectory') { 'docs/' } else { '' }
    $entryPoint = if ($Layout -eq 'AppDirectory') { 'EML.exe' } else { 'EML.App.exe' }
    $required = @($entryPoint, 'START-HERE.txt', 'UPGRADE.txt', 'docs/uninstall.md', 'docs/code-signing.md', 'docs/release-rc1.md')
    $required += @(
        'EML.App.dll', 'EML.Core.dll', 'EML.Infrastructure.dll',
        'CommunityToolkit.Mvvm.dll', 'EML.App.deps.json', 'EML.App.runtimeconfig.json',
        'microsoft-login.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
        'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll'
    ) | ForEach-Object { $programPrefix + $_ }
    $required += @('LICENSE-DOTNET.txt', 'LICENSE-MVVM.txt', 'THIRD-PARTY-NOTICES.txt',
        'LICENSE', 'SOURCE.txt', 'PRIVACY.md', 'SECURITY.md') | ForEach-Object { $documentPrefix + $_ }
    if ($Layout -eq 'AppDirectory') { $required += @('app/eml-layout.json', 'docs/release-rc2.md') }
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) {
            throw "Required self-contained release file is missing: $name"
        }
    }
    if ($Layout -eq 'AppDirectory') { Assert-EmlLayoutMarker -Path (Join-Path $root 'app/eml-layout.json') }
    Assert-EmlAmd64Executable -Path (Join-Path $root $entryPoint)
    foreach ($name in @($entryPoint, "${programPrefix}EML.App.dll", "${programPrefix}EML.Core.dll", "${programPrefix}EML.Infrastructure.dll")) {
        $information = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root $name))
        if ($information.FileVersion -ne '1.0.0.0' -or $information.ProductVersion -ne $Version) {
            throw "Release version metadata is inconsistent: $name"
        }
    }
    $runtime = Read-EmlReleaseJson -Path (Join-Path $root "${programPrefix}EML.App.runtimeconfig.json")
    if (-not $runtime.ContainsKey('runtimeOptions')) { throw 'Release runtime options are missing.' }
    $options = $runtime.runtimeOptions
    if ($options.ContainsKey('framework') -or $options.ContainsKey('frameworks') -or
        -not $options.ContainsKey('includedFrameworks') -or $options.tfm -ne 'net10.0') {
        throw 'Release must include .NET 10 frameworks instead of requiring a shared installation.'
    }
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $frameworks = @($options.includedFrameworks | Where-Object { $_.name -eq $name -and $_.version -eq $RuntimeVersion })
        if ($frameworks.Count -ne 1) { throw "Bundled framework is missing or ambiguous: $name" }
    }
    $dependencies = Read-EmlReleaseJson -Path (Join-Path $root "${programPrefix}EML.App.deps.json")
    if (-not $dependencies.ContainsKey('runtimeTarget') -or $dependencies.runtimeTarget.name -notmatch '/win-x64$') {
        throw 'Release dependency manifest does not target win-x64.'
    }
    $login = Read-EmlReleaseJson -Path (Join-Path $root "${programPrefix}microsoft-login.json") -MaximumBytes 4096
    if ($login.Count -ne 1 -or -not $login.ContainsKey('clientId')) {
        throw 'Microsoft login configuration must contain only the public clientId.'
    }
    $clientId = [Guid]::Empty
    if ($login.clientId -isnot [string] -or -not [Guid]::TryParse($login.clientId, [ref]$clientId) -or $clientId -eq [Guid]::Empty) {
        throw 'Microsoft login configuration requires a valid public clientId.'
    }
    # 清单生成前允许首次审计；清单一旦存在，必须覆盖全部载荷且逐项匹配。
    $manifestPath = Join-Path $root 'release-manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Read-EmlReleaseJson -Path $manifestPath
        if ($manifest.schemaVersion -ne 1 -or $manifest.version -ne $Version -or
            $manifest.displayVersion -ne $DisplayVersion -or $manifest.runtimeIdentifier -ne 'win-x64' -or
            $manifest.selfContained -ne $true -or $manifest.runtimeVersion -ne $RuntimeVersion) {
            throw 'Release manifest identity is inconsistent.'
        }
        if ($manifest.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $manifest.sourceRepository -cne 'https://github.com/Ph1lK4/EML') {
            throw 'Release manifest requires the exact public source revision.'
        }
        if ($Layout -eq 'AppDirectory') {
            if ($manifest.layout -cne 'app-directory' -or $manifest.entryPoint -cne 'EML.exe' -or
                $manifest.applicationDirectory -cne 'app' -or $manifest.dataDirectory -cne '.') {
                throw 'Release manifest layout is inconsistent.'
            }
        } elseif ($manifest.ContainsKey('layout') -or $manifest.ContainsKey('entryPoint') -or
            $manifest.ContainsKey('applicationDirectory') -or $manifest.ContainsKey('dataDirectory')) {
            if ($manifest.layout -cne 'flat' -or $manifest.entryPoint -cne 'EML.App.exe' -or
                $manifest.applicationDirectory -cne '.' -or $manifest.dataDirectory -cne '.') {
                throw 'Release manifest layout is inconsistent.'
            }
        }
        $sourceNotice = [IO.File]::ReadAllText((Join-Path $root "${documentPrefix}SOURCE.txt"))
        if (-not $sourceNotice.Contains("Source commit: $($manifest.sourceCommit)") -or
            -not $sourceNotice.Contains("$($manifest.sourceRepository)/archive/$($manifest.sourceCommit).zip") -or
            -not $sourceNotice.Contains('GPL-3.0-or-later')) {
            throw 'Source notice does not match the release manifest.'
        }
        $payloadFiles = @{}
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            if ($relative -ne 'release-manifest.json') { $payloadFiles[$relative] = $file }
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $manifest.files) {
            Test-EmlReleasePath -RelativePath $entry.path -Layout $Layout
            if (-not $seen.Add($entry.path) -or -not $payloadFiles.ContainsKey($entry.path)) {
                throw 'Release manifest has unexpected or duplicate entries.'
            }
            $file = $payloadFiles[$entry.path]
            if ($entry.size -ne $file.Length -or $entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
                $entry.sha256 -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                throw "Release manifest integrity check failed: $($entry.path)"
            }
        }
        if ($seen.Count -ne $payloadFiles.Count) { throw 'Release manifest does not cover every payload file.' }
    }
    [pscustomobject]@{
        Version = $Version
        DisplayVersion = $DisplayVersion
        RuntimeIdentifier = 'win-x64'
        SelfContained = $true
        Layout = $Layout
        EntryPoint = $entryPoint
        FileCount = $files.Count
        TotalBytes = ($files | Measure-Object -Property Length -Sum).Sum
        Frameworks = @($options.includedFrameworks | ForEach-Object { [pscustomobject]@{ Name = $_.name; Version = $_.version } })
        PrivacyCheck = 'Passed path and configuration allow-list; embedded developer authorization remains part of the product.'
    }
}

Export-ModuleMember -Function Test-EmlReleasePath, Get-EmlReleaseFiles, Test-EmlReleaseLayout
