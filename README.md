# YubiLatch

保留个人 Microsoft 账户，通过 USB YubiKey 的真实硬件挑战响应与触摸，向 Windows 提交受系统加密保护的登录凭据。

当前版本：**0.1.0 预发布**。验证进度见 [验证记录](docs/validation.md)。

## 适用范围

- Windows 11 22H2+ **x64**，个人 Microsoft 账户，仍有可用于 Windows 登录的账户密码。
- YubiKey 5C NFC 通过 **USB** 连接，OTP 功能可用且长按槽位 2 空闲。
- Windows OOBE / Microsoft 账户初次配置先使用系统方式完成。组件设计覆盖其后的重启登录和锁屏解锁，具体情况见 [验收矩阵](docs/acceptance.md)。
- 不支持纯无密码 MSA、ARM64、RDP、UAC、BitLocker 预启动、首次 OOBE 或 NFC 登录路径。

认证流程：**YubiKey 触摸与 HMAC 挑战响应 → 本地服务验证 → 解密 SYSTEM DPAPI 保护的账户密码 → Credential Provider 提交 → Windows 验证**。

系统原有密码、PIN、Windows Hello 入口保留。认证与凭据保护的实现见 [安全模型](SECURITY.md) 和 [架构设计](docs/architecture.md)。

## 构建

需要 .NET SDK 10.0.403、Visual Studio 2022/2026 的“使用 C++ 的桌面开发”与 Windows 11 SDK、CMake 3.25+、PowerShell 7。依赖由 NuGet 精确锁定；首次还原包含较大的原生 SDK 包。

```powershell
git clone https://github.com/MopeLab/YubiLatch.git
cd YubiLatch
pwsh -NoProfile -File .\scripts\Build.ps1 -Package -LockedRestore
```

构建会编译组件、运行核心安全测试、检查安装脚本语法，生成 `artifacts/YubiLatch-0.1.0-preview-win-x64-时间戳.zip` 和同名目录。测试宿主需要本机进程通信权限。

VS 解决方案为 `YubiLatch.slnx`；原生 DLL 使用 `native/CMakeLists.txt` 构建。Windows App SDK 与 .NET 运行时随发布目录部署，目标机不要求预装 .NET。原生 DLL 静态链接 VC 运行库。

## 安装与首次绑定

安装前确认至少一种 Windows 原有登录方式可用；启用了 BitLocker 的设备请保存恢复密钥。虚拟机需要 USB 直通才能使用钥匙。

1. 解压整个发布包，不要单独复制 EXE。管理员 **64 位 PowerShell** 执行：

   ```powershell
   .\installer\Install.ps1 -PackageRoot . -AllowUnsignedDevelopmentBuild
   ```

   未签名开发包需要 `-AllowUnsignedDevelopmentBuild` 参数。安装器校验文件 SHA-256、设置目录权限，创建 LocalSystem 服务及开始菜单入口；失败时回滚新文件与服务，保留已有加密数据。登录 Provider 在绑定后单独启用。

2. 在开始菜单打开 **YubiLatch**，普通用户运行即可。服务只允许管理调用者自己的账户。
3. 确认本地服务已连接。插入 USB YubiKey，选择“绑定安全钥匙”，填写名称、当前 Microsoft 账户邮箱及**账户密码**。
4. 选择空闲的长按槽位 2，并确认绑定。已占用的槽位无法用于新绑定。
5. 指示灯闪烁时触摸。服务先验证账户 SID，再编程随机 HMAC 秘钥、强制触摸并验证真实响应。只有验证成功才激活绑定。
6. **安全保存显示的 12 位十六进制槽位管理码**，建议存入密码管理器。它保护 OTP 配置，不能登录 Windows。可在“安全钥匙 → 管理码”重新验证账户密码后查看。删除绑定、清除数据或重装系统前必须保存；遗失可能无法重新配置该 OTP 槽位。
7. 可单独绑定第二把备用钥匙，保存其管理码，并分开存放。
8. 确认原生恢复方式可独立使用后，在同一账户提升的管理员 PowerShell 中执行：

   ```powershell
   .\installer\Enable-Provider.ps1 -ConfirmNativeSignInWorks -AcceptUnvalidatedPreview
   ```

   要求服务运行，本账户至少一把钥匙已验证且启用。若通过另一个管理员账户提升，检查的是那个管理员的绑定。

管理器“启用此账户”控制服务内账户状态；`Enable-Provider.ps1` 控制机器级 Provider 注册，两者不同。只有有有效绑定的 MSA 用户枚举 YubiLatch 登录磁贴。

## 日常使用

在 Windows 登录屏幕选择账户及 **YubiLatch** 登录方式。只连接一把已绑定钥匙，灯闪烁时触摸，即可自动提交凭据，无需再输入密码/PIN。Windows 管理默认登录方式的选择。

等待期间可切换原生方式。约 55 秒未发现钥匙、触摸超时、拔出、同时连接多把绑定钥匙、服务异常或验证失败时，不释放凭据。可重试或改用 PIN/密码/Hello。取消时已发出的硬件请求可能等到 SDK 自身超时，但不会提交已取消的结果。Windows 拒绝密码后不会自动循环重试。

更改 Microsoft 账户密码后，用原生方式进入桌面，在“登录设置 → 更新已保存的密码”输入新密码。服务验证 Windows 返回 SID 后更新密文。本管理器不修改云端账户密码。

## 恢复与卸载

- **遗失钥匙**：使用备用钥匙或系统方式登录，再撤销遗失钥匙的本机绑定。
- **绑定中断**：使用“恢复验证”，输入账户密码后触摸。pending 不用于登录；若槽位还空闲，可撤销 pending 后重建。已写入则不要覆盖，见 [恢复说明](docs/recovery.md)。
- **紧急停用**：管理员运行 `installer/Disable-Provider.ps1`，不需要服务或钥匙。
- **卸载**：管理员运行 `installer/Uninstall.ps1`。先移除 Provider 注册，再停止/删除服务，最后清理文件；默认保留加密绑定。
- **清除数据**：先保存所有槽位管理码，再在管理器删除全部本机数据，或卸载时加 `-PurgeData`。硬件及槽位保护保留。
- DLL 被占用时，先禁用、重启，再重新运行卸载脚本清理。升级先卸载并保留数据、重启，再安装新版本；不热替换登录 DLL。
- 无法进入桌面时，按 [WinRE 离线恢复](docs/recovery.md) 删除本项目的两个注册子键，不能清空系统 Provider 分支。

## 项目结构

| 目录 | 职责 |
| --- | --- |
| `src/YubiLatch.Core` | 挑战验证、受限协议、数据与状态模型 |
| `src/YubiLatch.Service` | Yubico SDK、SYSTEM DPAPI、账户验证、命名管道 |
| `native` | C++ V2 Credential Provider，无系统 Provider Filter |
| `src/YubiLatch.App` | 简体中文 WinUI 3 管理器 |
| `installer` | 安装、单独启用、紧急禁用、卸载 |
| `tests` | 无真实钥匙/登录组件依赖的安全测试 |
| `docs` | 研究、架构、恢复、验收与验证记录 |

本地化入口为 `Strings/zh-CN.json` 与 `Localization.cs`；当前 XAML 及服务消息为简体中文，后续可迁移成完整资源 ID 与语言包。协议操作名稳定，不依赖显示语言。

远程 CI 见 [Actions](https://github.com/MopeLab/YubiLatch/actions)。
