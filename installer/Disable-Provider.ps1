#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param()
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
if ($PSCmdlet.ShouldProcess('YubiLatch', '紧急禁用附加登录方式，保留绑定与全部系统登录方式')) {
    Remove-ProviderRegistration
    Write-Output 'YubiLatch Provider 注册已移除。已加载的 DLL 可能到重启后才卸载。原有登录方式不受影响。'
}
