#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][switch]$ConfirmNativeSignInWorks, [Parameter(Mandatory)][switch]$AcceptUnvalidatedPreview)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
if (-not $ConfirmNativeSignInWorks -or -not $AcceptUnvalidatedPreview) { throw '需要明确确认原生恢复途径可用，并接受预发布版本尚未完成真实登录验收。' }
$dll = Join-Path $script:InstallRoot 'provider\YubiLatch.CredentialProvider.dll'
Assert-NoReparse $dll
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw '请先安装 YubiLatch。' }
if ((Get-Service -Name YubiLatch).Status -ne 'Running') { throw '服务未运行。' }
$status = Get-OwnAccountStatus
if (-not $status.Enabled -or @($status.Keys | Where-Object { $_.State -eq 1 }).Count -lt 1) { throw '当前账户至少需要一个已验证且启用的钥匙绑定。' }
if (-not $PSCmdlet.ShouldProcess($script:ProviderKey, '注册附加登录方式；不会禁用系统 Provider')) { return }
if ((Test-Path -LiteralPath $script:ProviderKey) -or (Test-Path -LiteralPath $script:ComKey)) { throw 'Provider 或 COM 注册已存在，请先禁用再启用。' }
try {
    $inproc = New-Item -Path ($script:ComKey + '\InprocServer32') -Force
    $inproc.SetValue('', $dll)
    $inproc.SetValue('ThreadingModel','Apartment')
    $registration = New-Item -Path $script:ProviderKey -Force
    $registration.SetValue('', 'YubiLatch')
    Write-Output '已启用。先在独立测试机验收；登录屏幕中选择 YubiLatch。所有 Windows 原有登录方式保留。'
} catch {
    Remove-Item -LiteralPath $script:ProviderKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $script:ComKey -Recurse -Force -ErrorAction SilentlyContinue
    throw
}
