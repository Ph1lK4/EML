param([switch]$UseEnvironmentKey)
# 为此工作副本配置开发者构建凭据；只保存 Windows 当前用户加密的数据。
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$credentialDirectory = Join-Path $projectRoot '.eml-build'
$credentialPath = Join-Path $credentialDirectory 'curseforge.key.dpapi'
$temporaryPath = Join-Path $credentialDirectory ('credential-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$credential = $null
$clearText = $null
$plainBytes = $null
$protectedBytes = $null
$pointer = [IntPtr]::Zero
try {
    if ($env:OS -ne 'Windows_NT') { throw 'Windows is required for developer credential configuration.' }
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'EML.slnx') -PathType Leaf)) { throw 'EML solution was not found.' }
    # 拒绝凭据目录和目标文件中的链接，防止写到工作副本之外。
    foreach ($path in @($credentialDirectory, $credentialPath)) {
        if ((Test-Path -LiteralPath $path) -and
            (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw 'Linked credential paths are not supported.'
        }
    }
    Add-Type -AssemblyName System.Security
    if ($UseEnvironmentKey) {
        $clearText = [Environment]::GetEnvironmentVariable('EML_CURSEFORGE_API_KEY', 'Process')
    } else {
        $credential = Read-Host 'CurseForge developer API Key (hidden input)' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($credential)
        $clearText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    if ([string]::IsNullOrWhiteSpace($clearText) -or $clearText.Length -gt 1280 -or $clearText -match '[^\x21-\x7e]') {
        throw 'CurseForge build credential is missing or invalid.'
    }
    $plainBytes = [Text.Encoding]::UTF8.GetBytes($clearText)
    $protectedBytes = [Security.Cryptography.ProtectedData]::Protect(
        $plainBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $clearText = $null
    # 先完整写入加密临时文件再原子替换，配置失败时保留上一份可用凭据。
    New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null
    [IO.File]::WriteAllText($temporaryPath, [Convert]::ToBase64String($protectedBytes), [Text.Encoding]::ASCII)
    if (Test-Path -LiteralPath $credentialPath) {
        [IO.File]::Replace($temporaryPath, $credentialPath, $null)
    } else {
        [IO.File]::Move($temporaryPath, $credentialPath)
    }
    Write-Output 'Encrypted developer credential configured for this working copy.'
    Write-Output 'Reload the solution in Visual Studio and rebuild once. Ordinary Debug and Release builds will then embed authorization automatically.'
} catch {
    # 不转发可能携带敏感值的底层异常；命令行永远不接受明文 Key 参数。
    throw 'Could not configure the developer credential. Check Windows user permissions and the hidden input; the previous credential was preserved.'
} finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    if ($null -ne $credential) { $credential.Dispose() }
    if ($null -ne $plainBytes) { [Array]::Clear($plainBytes, 0, $plainBytes.Length) }
    if ($null -ne $protectedBytes) { [Array]::Clear($protectedBytes, 0, $protectedBytes.Length) }
    $clearText = $null
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { Remove-Item -LiteralPath $temporaryPath }
}
