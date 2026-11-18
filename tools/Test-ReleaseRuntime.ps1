#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Path, [string] $DisplayVersion = '1.0-RC2')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Only run our built launcher in a disposable copy; never initialize the package being audited.
$source = [IO.Path]::GetFullPath($Path)
if (!(Test-Path -LiteralPath (Join-Path $source 'EML.exe') -PathType Leaf)) { throw 'Structured release entry point is missing.' }
Add-Type -AssemblyName System.Drawing
$executableIcon = [Drawing.Icon]::ExtractAssociatedIcon((Join-Path $source 'EML.exe'))
$referenceIcon = [Drawing.Icon]::new((Join-Path $PSScriptRoot '../src/EML.App/Assets/Branding/eml.ico'), $executableIcon.Width, $executableIcon.Height)
$actualPixels = $executableIcon.ToBitmap()
$expectedPixels = $referenceIcon.ToBitmap()
try {
    for ($y = 0; $y -lt $actualPixels.Height; $y++) {
        for ($x = 0; $x -lt $actualPixels.Width; $x++) {
            if ($actualPixels.GetPixel($x, $y).ToArgb() -ne $expectedPixels.GetPixel($x, $y).ToArgb()) { throw 'Native EXE does not contain the expected EML logo.' }
        }
    }
} finally { $actualPixels.Dispose(); $expectedPixels.Dispose(); $executableIcon.Dispose(); $referenceIcon.Dispose() }
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('EML 运行验证 ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $source -Force) { Copy-Item -LiteralPath $item.FullName -Destination $temporary -Recurse }
$preserved = @{}
foreach ($relative in @('instances/合成实例/saves/合成世界/level.dat', 'settings/retention.txt',
    'runtimes/retention.txt', 'cache/retention.txt', '.eml-runtime/retention.txt', '.eml-imports/retention.txt')) {
    $file = Join-Path $temporary $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file)) -Force | Out-Null
    [IO.File]::WriteAllText($file, 'Synthetic package test data; not a real world or account.')
    $preserved[$file] = (Get-FileHash -LiteralPath $file).Hash
}

$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $temporary 'EML.exe'))
$info.UseShellExecute = $false
$info.WorkingDirectory = [Environment]::SystemDirectory
# Poison shared-runtime paths, but do not change machine environment or uninstall anything.
$info.Environment['DOTNET_ROOT'] = Join-Path $temporary 'nonexistent-dotnet'
$info.Environment['DOTNET_ROOT_X64'] = $info.Environment['DOTNET_ROOT']
$info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$process = $null
try {
    $process = [Diagnostics.Process]::Start($info)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) { throw "Launcher exited before opening its window ($($process.ExitCode))." }
    } while ((!$process.MainWindowHandle -or $process.MainWindowTitle -notlike "*$DisplayVersion*") -and $timer.Elapsed.TotalSeconds -lt 25)
    if (!$process.MainWindowHandle -or $process.MainWindowTitle -notlike "*$DisplayVersion*") { throw 'Expected release window did not open.' }
    $moduleNames = @('hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'PresentationNative_cor3.dll')
    $modules = @($process.Modules | Where-Object ModuleName -In $moduleNames)
    if ($modules.Count -ne $moduleNames.Count) { throw 'Expected bundled runtime modules were not loaded.' }
    foreach ($module in $modules) {
        if (!$module.FileName.StartsWith((Join-Path $temporary 'app\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Launcher loaded a runtime from outside its app directory.'
        }
    }
    if (!$process.CloseMainWindow() -or !$process.WaitForExit(20000)) { throw 'Release window did not close normally.' }
    if ($process.ExitCode -ne 0) { throw "Release process exited unsuccessfully ($($process.ExitCode))." }
    foreach ($file in $preserved.Keys) {
        if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $preserved[$file]) { throw 'Startup changed synthetic player data.' }
    }
    foreach ($directory in @('instances', 'settings', 'cache', 'runtimes', '.eml-runtime', '.eml-imports')) {
        if (Test-Path -LiteralPath (Join-Path $temporary "app/$directory")) { throw 'Player data was created inside app.' }
    }
    [pscustomobject]@{ Status = 'passed'; WindowOpened = $true; NativeIconMatchesSource = $true; BundledRuntimeModules = $moduleNames;
        DifferentWorkingDirectory = $true; ChineseAndSpacePath = $true; NormalExit = $true;
        SyntheticPlayerDataPreserved = $preserved.Count; CleanMachineAcceptance = 'pending'; Fixture = $temporary }
} finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { $null = $process.CloseMainWindow() }
        $process.Dispose()
    }
}
