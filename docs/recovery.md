# YubiLatch 故障恢复

## 登录屏幕仍可操作

选择“登录选项”，使用 Windows PIN、账户密码或 Windows Hello。YubiLatch 不注册 Filter，不隐藏或禁用系统 Provider。没有钥匙、服务损坏、DPAPI 数据损坏或账户密码更改时，切换原生入口。

进入桌面后，管理员 64 位 PowerShell 执行安装包 / 安装目录内的：

```powershell
.\installer\Disable-Provider.ps1
```

只删除 GUID 为 `{D53C2D8E-51CC-4F5D-88E6-A0CDF5B39C54}` 的本项目 Provider 和 COM 注册，不依赖服务、不删除绑定。已加载 DLL 可能需重启释放；随后修复或卸载服务。

## 钥匙遗失

1. 用另一把绑定钥匙，或系统方式登录。
2. 在“安全钥匙”撤销遗失钥匙，需要当前账户密码重新认证。
3. 如果账户凭据也泄露，在 Microsoft 正式安全入口改密，然后在 YubiLatch 更新保存的密码。
4. 单独绑定新钥匙。

## pending / 写入中断

服务在编程前保存 pending，避免硬件已写入而秘钥丢失。

- 已写入：连接原钥匙，选择“恢复验证”，输入账户密码并触摸；成功后激活，不重新编程。
- 未写入：确认槽位仍空闲后，撤销 pending 再重建。
- 被其他软件占用：先确认槽位的现有用途，再通过官方工具处理。
- 最后 active 保存失败：pending 仍可再次恢复验证。返回失败不代表钥匙未写入。

## 槽位管理码

新绑定用独立随机 6 字节管理码保护 OTP 长按槽位 2，防止软件改变触摸要求。绑定完成显示 12 位十六进制码，亦可在“安全钥匙 → 管理码”验证密码后查看。

管理码用于 Yubico Authenticator / 官方管理工具中的槽位操作。**撤销绑定、删除数据、`-PurgeData` 或重装系统前先保存所有管理码。** 重新配置受保护的 OTP 槽位需要此码；OTP 的恢复独立于 FIDO2 和 OpenPGP。

## 无法进入桌面：WinRE 离线禁用

准备 BitLocker 恢复密钥，并确认恢复环境中包含 `Windows\System32\Config` 的 Windows 分区盘符。

在 WinRE“疑难解答 → 高级选项 → 命令提示符”中，**仅当 D: 确实是 Windows 分区**时执行：

```text
reg load HKLM\YubiLatchOffline D:\Windows\System32\Config\SOFTWARE
reg delete "HKLM\YubiLatchOffline\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{D53C2D8E-51CC-4F5D-88E6-A0CDF5B39C54}" /f
reg delete "HKLM\YubiLatchOffline\Classes\CLSID\{D53C2D8E-51CC-4F5D-88E6-A0CDF5B39C54}" /f
reg unload HKLM\YubiLatchOffline
```

只删除这两个精确子键。不存在则不需要删除父分支，不能改动 Password、PIN、Hello Provider。重启后使用系统方式登录，再卸载服务。

## 卸载提示 1072 / 服务正在等待删除

关闭“服务”管理器、YubiLatch 属性窗口及相关管理终端，等待服务句柄释放，再重新安装。若仍处于待删除状态，重启 Windows 后重试。

卸载器接受服务已删除或已标记删除的状态，并继续清理旧文件。安装器会等待待删除服务释放，超时后提示上述处理步骤。卸载默认保留加密绑定数据。

## 数据和权限损坏

数据位于 `%ProgramData%\MopeLab\YubiLatch`，使用 LocalSystem 用户级 DPAPI 加密并绑定 SID。目录仅允许 SYSTEM 和管理员访问，数据依赖原 Windows 安装的 DPAPI 主密钥。

数据损坏时不自动恢复旧备份，避免已撤销钥匙复活。先禁用 Provider，保存仍能读取的管理码，再修复服务。丢失 SYSTEM DPAPI 主密钥通常不能恢复数据；管理码也丢失时，受保护 OTP 槽位可能无法重用。
