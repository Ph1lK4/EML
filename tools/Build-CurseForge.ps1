param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$UseEnvironmentKey,
    [switch]$Publish
)
# 维护者构建入口：交互输入不回显，密钥仅在本次构建进程中存在，不写入配置或参数。
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$oldKey = [Environment]::GetEnvironmentVariable('EML_CURSEFORGE_API_KEY', 'Process')
$oldEmbed = [Environment]::GetEnvironmentVariable('EML_EMBED_CURSEFORGE_KEY', 'Process')
$credential = $null
$clearText = $null
$pointer = [IntPtr]::Zero
try {
    if ($UseEnvironmentKey) {
        $clearText = $oldKey
    } else {
        $credential = Read-Host 'CurseForge developer API Key (hidden input)' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($credential)
        $clearText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    if ([string]::IsNullOrWhiteSpace($clearText) -or $clearText.Length -gt 1280 -or $clearText -match '[^\x21-\x7e]') {
        throw 'CurseForge build credential is missing or invalid. No credential has been saved.'
    }
    [Environment]::SetEnvironmentVariable('EML_CURSEFORGE_API_KEY', $clearText, 'Process')
    [Environment]::SetEnvironmentVariable('EML_EMBED_CURSEFORGE_KEY', '1', 'Process')
    $clearText = $null

    # 强制重新编译且停用共享编译进程，避免旧密钥或无密钥的增量产物被误复用。
    # 禁止生成器落盘和调试源嵌入；不要额外开启诊断日志、binlog 或 CI 环境转储。
    & dotnet build (Join-Path $projectRoot 'EML.slnx') -c $Configuration --nologo --tl:off -t:Rebuild '-p:UseSharedCompilation=false' '-p:EmitCompilerGeneratedFiles=false' '-p:EmbedAllSources=false' '-p:EmbedUntrackedSources=false' '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw 'EML build failed. See the non-secret build diagnostics above.' }

    if ($Publish) {
        $publishRoot = Join-Path $projectRoot 'artifacts\EML-CurseForge'
        & dotnet publish (Join-Path $projectRoot 'src\EML.App\EML.App.csproj') -c $Configuration --no-build --no-restore --nologo --tl:off -o $publishRoot '-p:EmbedAllSources=false' '-p:EmbedUntrackedSources=false' '-p:DebugType=None' '-p:DebugSymbols=false'
        if ($LASTEXITCODE -ne 0) { throw 'EML publish failed.' }
        Write-Output ('Published launcher: ' + $publishRoot)
    } else {
        Write-Output ('Built launcher: ' + (Join-Path $projectRoot "src\EML.App\bin\$Configuration\net10.0-windows\EML.App.exe"))
    }
    Write-Output 'CurseForge authorization was embedded. No plaintext key configuration was created.'
} finally {
    [Environment]::SetEnvironmentVariable('EML_CURSEFORGE_API_KEY', $oldKey, 'Process')
    [Environment]::SetEnvironmentVariable('EML_EMBED_CURSEFORGE_KEY', $oldEmbed, 'Process')
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    if ($null -ne $credential) { $credential.Dispose() }
    $clearText = $null
    $oldKey = $null
}
