#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param([switch]$PurgeData)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
Assert-NoReparse $script:InstallRoot
Assert-NoReparse $script:VaultRoot
if (-not $PSCmdlet.ShouldProcess($script:InstallRoot, '卸载 YubiLatch；默认保留加密绑定，-PurgeData 才删除')) { return }
# Unregister FIRST. A file deletion failure must never leave a broken provider registered.
Remove-ProviderRegistration
$service = Get-Service -Name YubiLatch -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name YubiLatch -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
    Invoke-Sc @('delete','YubiLatch')
}
Remove-Item -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\YubiLatch.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YubiLatch' -Recurse -Force -ErrorAction SilentlyContinue
if ($PurgeData -and (Test-Path -LiteralPath $script:VaultRoot)) {
    $checked = Assert-ChildPath $script:VaultRoot (Join-Path $env:ProgramData 'MopeLab')
    Assert-NoReparseTree $checked
    Remove-Item -LiteralPath $checked -Recurse -Force
}
if (Test-Path -LiteralPath $script:InstallRoot) {
    $checked = Assert-ChildPath $script:InstallRoot (Join-Path $env:ProgramFiles 'MopeLab')
    Assert-NoReparseTree $checked
    try { Remove-Item -LiteralPath $checked -Recurse -Force }
    catch { Write-Warning '登录 DLL 或管理器仍被使用。Provider 已禁用；请重启后重新运行卸载脚本以清理剩余文件。' }
}
Write-Output 'YubiLatch 已停用并卸载服务。未修改任何 YubiKey 配置或 Windows 原有登录方式。'
