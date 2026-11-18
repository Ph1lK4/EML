#requires -Version 7.0
[CmdletBinding()]
param()

# 无凭证的 CI 验证入口；不调用要求开发者授权的正式发布脚本，也不启动游戏。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'EML CI requires Windows for the WPF tests.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ciRoot = Join-Path $projectRoot 'artifacts/ci'
$publishRoot = Join-Path $ciRoot 'publish'
$applicationRoot = Join-Path $publishRoot 'app'
$resultsRoot = Join-Path $ciRoot 'test-results'

# 不覆盖先前产物，防止旧 TRX 或残留 DLL 造成假通过；重跑使用新的干净源码副本。
if (Test-Path -LiteralPath $ciRoot) { throw 'artifacts/ci already exists. Run CI from a fresh checkout.' }
if (Test-Path -LiteralPath (Join-Path $projectRoot '.eml-build/curseforge.key.dpapi')) {
    throw 'Use a clean checkout without local developer credentials for CI validation.'
}
New-Item -ItemType Directory -Path $ciRoot | Out-Null
Import-Module (Join-Path $PSScriptRoot 'ReleasePackage.psm1') -Force
[xml] $properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$runtimeVersion = [string] $properties.Project.PropertyGroup.EMLReleaseRuntimeVersion

# 每条原生命令都检查退出码，不让后续命令或报告上传掩盖构建/测试失败。
function Invoke-CiDotnet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

$safeBuild = @('-p:EMLCurseForgeCredentialFile=', '-p:UseSharedCompilation=false',
    '-p:EmitCompilerGeneratedFiles=false', '-p:EmbedAllSources=false', '-p:EmbedUntrackedSources=false')
$previousEmbed = $env:EML_EMBED_CURSEFORGE_KEY
$previousKey = $env:EML_CURSEFORGE_API_KEY
Push-Location $projectRoot
try {
    # 只更改当前进程环境，防止本机环境授权意外进入 CI；finally 原样还原。
    $env:EML_EMBED_CURSEFORGE_KEY = '0'
    $env:EML_CURSEFORGE_API_KEY = $null
    Invoke-CiDotnet -Arguments (@('restore', 'EML.slnx', '--nologo') + $safeBuild)
    Invoke-CiDotnet -Arguments (@('build', 'EML.slnx', '-c', 'Release', '--no-restore', '--nologo', '--tl:off') + $safeBuild)
    # 两个项目使用独立报告目录，避免相同时间/框架的 TRX 文件名互相覆盖。
    $testCounts = [ordered]@{}
    foreach ($testProject in @('EML.Tests', 'EML.App.Tests')) {
        $projectResults = Join-Path $resultsRoot $testProject
        Invoke-CiDotnet -Arguments (@('test', "tests/$testProject/$testProject.csproj", '-c', 'Release',
            '--no-build', '--no-restore', '--nologo', '--blame-hang-timeout', '2min', '--blame-hang-dump-type', 'none', '--logger', 'trx;LogFileName=results.trx',
            '--results-directory', $projectResults) + $safeBuild)
        [xml] $testReport = Get-Content -LiteralPath (Join-Path $projectResults 'results.trx') -Raw
        $counters = $testReport.TestRun.ResultSummary.Counters
        if ([int]$counters.total -le 0 -or [int]$counters.failed -ne 0 -or [int]$counters.error -ne 0) {
            throw "Missing or unsuccessful tests in $testProject."
        }
        $testCounts[$testProject] = [int]$counters.passed
    }

    # RID 发布需要独立 restore；保留 publish 的默认还原行为，不复用无 RID 的 assets 文件。
    Invoke-CiDotnet -Arguments (@('publish', 'src/EML.App/EML.App.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:EMLReleasePackage=true', '-p:DebugType=None', '-p:DebugSymbols=false',
        '--nologo', '--tl:off', '-o', $applicationRoot, '-p:EMLStructuredRelease=true', "-p:EMLPackageDirectory=$publishRoot") + $safeBuild)

    # 复用正式发布的路径白名单，拒绝账号、实例、日志、源码、符号及链接进入输出。
    $files = @(Get-EmlReleaseFiles -Path $publishRoot -Layout AppDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'EML.exe') -PathType Leaf)) { throw 'Root EML.exe is missing.' }
    if (Test-Path -LiteralPath (Join-Path $applicationRoot 'EML.App.exe')) { throw 'Duplicate legacy entry point in app directory.' }
    foreach ($required in @('eml-layout.json', 'EML.App.dll', 'EML.Core.dll', 'EML.Infrastructure.dll',
            'EML.App.deps.json', 'EML.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
            'PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $applicationRoot $required) -PathType Leaf)) {
            throw "Missing self-contained publish output: $required"
        }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $applicationRoot 'EML.App.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    $options = $runtime.runtimeOptions
    if ($options.ContainsKey('framework') -or $options.ContainsKey('frameworks') -or
        -not $options.ContainsKey('includedFrameworks') -or $options.tfm -ne 'net10.0') {
        throw 'Publish output requires a shared runtime instead of including .NET 10.'
    }
    foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $matches = @($options.includedFrameworks | Where-Object { $_.name -eq $framework -and $_.version -eq $runtimeVersion })
        if ($matches.Count -ne 1) { throw "Missing bundled framework: $framework/$runtimeVersion" }
    }
    $dependencies = Get-Content -LiteralPath (Join-Path $applicationRoot 'EML.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($dependencies.runtimeTarget.name -notmatch '/win-x64$') { throw 'Publish output does not target win-x64.' }
    & (Join-Path $PSScriptRoot 'Test-ReleasePackage.Tests.ps1')
    $runtimeSmoke = & (Join-Path $PSScriptRoot 'Test-ReleaseRuntime.ps1') -Path $publishRoot -DisplayVersion ([string]$properties.Project.PropertyGroup.EMLDisplayVersion)

    # 仅记录冒烟检查结果，不生成正式 release-manifest，也不把未签名产物标为发行包。
    [ordered]@{
        status = 'passed'; runtimeIdentifier = 'win-x64'; selfContained = $true
        layout = 'app-directory'; entryPoint = 'EML.exe'
        runtimeSmoke = $runtimeSmoke
        runtimeVersion = $runtimeVersion; publishedFileCount = $files.Count
        passedTests = $testCounts
        codeSigned = $false; embeddedCurseForgeCredential = $false
        distribution = 'Smoke check only; no release package is produced or uploaded.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ciRoot 'publish-check.json') -Encoding utf8NoBOM
    Write-Output 'CI build, tests and unsigned self-contained publish smoke check passed.'
} finally {
    $env:EML_EMBED_CURSEFORGE_KEY = $previousEmbed
    $env:EML_CURSEFORGE_API_KEY = $previousKey
    Pop-Location
}
