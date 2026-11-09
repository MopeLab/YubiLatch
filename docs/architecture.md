# 架构与可行性决策

研究日期：2026-11-08。目标平台：Windows 11 x64、个人 Microsoft 账户、USB 连接的 YubiKey 5C NFC。

## 能力边界

1. [Windows Credential Provider](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows) 负责收集及序列化凭据；最终身份验证仍由 Windows/LSA 完成。独立实现 V2 Provider，不包装或过滤系统 Provider。
2. [Microsoft Entra FIDO2 Windows 登录](https://learn.microsoft.com/en-us/entra/identity/authentication/howto-authentication-passwordless-security-key-windows) 适用于 Entra / hybrid joined 设备；[Yubico Login for Windows](https://www.yubico.com/blog/yubico-login-for-windows-now-generally-available/) 面向本地账户。YubiLatch 通过自定义 Provider 处理个人 MSA 的登录凭据。
3. 硬件认证后释放本机加密保存的 MSA 密码。[CredPackAuthenticationBufferW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credpackauthenticationbufferw) 支持 `CRED_PACK_ID_PROVIDER_CREDENTIALS` 及 `ProviderName\UserName`，Microsoft 的 [V2 示例](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CredentialProvider) 使用这一接口提交在线身份凭据。
4. Windows OOBE / 账户首次设置使用系统方式完成；Provider 的目标场景为后续交互式登录和锁屏解锁，包括重启后的登录。支持范围见 [README](../README.md)。
5. YubiKey 通过 USB 使用。挑战响应在本地验证；账户在线验证及离线缓存由 Windows 管理。

## 组件与信任边界

```text
WinUI 3 管理器（普通用户）
  │ 本地管理命名管道；服务模拟客户端，取得真实 SID / session
  ▼
.NET 10 LocalSystem 服务 ── Yubico.YubiKey SDK ── OTP/HMAC 硬件槽位 2
  │ SYSTEM 用户级 DPAPI + SYSTEM/Administrators 专用目录
  │ 二进制登录管道；只允许 SYSTEM，本机；检查客户端为本会话系统 LogonUI
  ▼
C++ V2 Credential Provider（LogonUI / SYSTEM）
  │ 按 MSA 身份提供者格式打包，Negotiate
  ▼
Winlogon / LSA：最终验证 MSA 密码
```

管理器不能请求、读取、导出密码或 HMAC 秘钥。绑定、删除、暂停、密码更新均要求当前账户密码重新认证，并检查 `LogonUserW` 返回 SID 与管道调用者 SID 一致。状态读取只返回本账户的非敏感信息。设备序列号仅用于定位；认证必须通过硬件 HMAC。

Windows 账户验证由独立 LocalSystem 子进程执行。服务通过继承的匿名管道传入凭据，命令行仅包含工作模式标识。验证超时为 25 秒；超时会结束子进程并返回账户验证错误，避免本机认证调用长期占用服务。

## 认证与绑定

登录磁贴在同一账户重新枚举时复用现有凭据对象；就绪状态在 `GetCredentialCount` 和 `SetSelected` 中一致请求自动提交。管道连接在短暂占用时按截止时间重试，并响应取消。失败提示通过磁贴字段回调更新，成功后才通知自动提交。

- 使用 Yubico 官方 [.NET SDK](https://docs.yubico.com/yesdk/users-manual/application-otp/challenge-response.html)。只编程未配置的 OTP 长按槽位 2，20 字节随机秘钥，强制 `UseButton()`，并设置随机 6 字节槽位管理码防止任意软件更新触摸策略。不重置设备，不触碰 FIDO2、PIV、OpenPGP 或短按槽位。已有长按槽位一律拒绝覆盖。管理码可在账户重新认证后单独查看，删除绑定前应备份；不会导出 HMAC 秘钥。
- 每次操作产生 64 字节 CSPRNG 挑战，按照硬件 OTP 协议使用完整 20 字节 HMAC-SHA1 响应，以固定时间比较验证。
- 服务使用 SYSTEM DPAPI 保存共享验证秘钥与账户密码，详见 [安全模型](../SECURITY.md)。
- 先验证密码和恢复确认，再持久化 pending 记录，最后编程空槽位并验证触摸。验证成功后激活绑定；中断留下的 pending 记录可通过管理器恢复验证。
- 每把钥匙使用独立秘钥；撤销绑定只删除本机记录，不自动擦除硬件。备份钥匙单独绑定。
- 登录凭据在硬件验证成功后短暂解密；管道实施长度限制、超时及身份验证，临时字节缓冲区使用后清零。凭据不写入日志。

## 恢复

系统 Password/PIN/Hello 入口始终保留，无 Provider Filter、无 LSA 插件、无自动登录注册表密码。默认安装服务和管理器，Provider 必须在绑定后单独启用；启用前明确确认原生恢复途径可用。服务故障、断开设备、触摸超时、数据损坏时不给凭据，提示切换登录方式。

验证进度见 [验证记录](validation.md)，集成测试场景见 [验收矩阵](acceptance.md)。

