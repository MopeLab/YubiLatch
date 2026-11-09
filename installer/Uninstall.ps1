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
$serviceRemoved = Remove-YubiLatchService
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
if ($serviceRemoved) { Write-Output 'YubiLatch 已卸载。' }
else { Write-Warning 'YubiLatch 已停用并提交服务删除。请关闭服务管理器及属性窗口，等待服务释放后再安装；若仍未释放，请重启 Windows。' }
