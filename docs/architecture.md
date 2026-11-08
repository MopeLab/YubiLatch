# 架构与可行性决策

研究日期：2026-11-08。目标平台：Windows 11 x64、个人 Microsoft 账户、USB 连接的 YubiKey 5C NFC。

## 能力边界

1. [Windows Credential Provider](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows) 负责收集及序列化凭据；最终身份验证仍由 Windows/LSA 完成。独立实现 V2 Provider，不包装或过滤系统 Provider。
2. [Microsoft Entra FIDO2 Windows 登录](https://learn.microsoft.com/en-us/entra/identity/authentication/howto-authentication-passwordless-security-key-windows) 的文档适用于 Entra / hybrid joined 设备，不能据此宣称个人 MSA 的 Passkey 可直接登录 Windows。[Yubico Login for Windows](https://www.yubico.com/blog/yubico-login-for-windows-now-generally-available/) 明确不支持 Microsoft 账户。
3. 本项目选择硬件认证后释放本机加密保存的 MSA 密码。[CredPackAuthenticationBufferW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credpackauthenticationbufferw) 支持 `CRED_PACK_ID_PROVIDER_CREDENTIALS` 及 `ProviderName\UserName`，Microsoft 的 [V2 示例](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CredentialProvider) 使用这一接口提交在线身份凭据。API 支持不等于已验证所有 MSA 策略；真实登录必须在独立 Windows 11 测试机验证。
4. 必须先用系统方式完成 Windows OOBE / 账户首次设置；YubiLatch 支持设计上的“重启后的首次交互式登录”，不实现 OOBE、BitLocker 预启动、RDP、UAC、密码修改、Web 登录或密码已被彻底移除的 MSA。
5. NFC 型号通过 USB 使用；不承诺安全桌面 NFC 读卡器路径。无云端验证服务；Windows 自身在线/离线缓存的账户行为仍由 Windows 决定。

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

## 认证与绑定

- 使用 Yubico 官方 [.NET SDK](https://docs.yubico.com/yesdk/users-manual/application-otp/challenge-response.html)。只编程未配置的 OTP 长按槽位 2，20 字节随机秘钥，强制 `UseButton()`，不重置设备，不触碰 FIDO2、PIV、OpenPGP 或短按槽位。已有长按槽位一律拒绝覆盖。
- 每次操作产生 64 字节 CSPRNG 挑战，使用完整 20 字节 HMAC-SHA1 响应、固定时间比较。SHA-1 碰撞攻击不等同于 HMAC 伪造；选择此算法源于硬件 OTP 协议限制。
- 服务保留对称验证秘钥的 SYSTEM DPAPI 密文。这是共享秘钥认证，不是 FIDO2 公钥认证；具有 SYSTEM 权限的攻击者可解密验证秘钥及密码。
- 先验证密码和恢复确认，再持久化 pending 记录，最后编程空槽位并实际验证触摸。验证成功才激活绑定。断电中断留下的 pending 记录可通过管理器恢复验证，绝不悄悄重写槽位。
- 每把钥匙使用独立秘钥；撤销绑定只删除本机记录，不自动擦除硬件。备份钥匙单独绑定。
- 登录凭据只在硬件验证成功后短暂解密；管道有边界、超时及身份验证，临时字节缓冲区使用后清零。托管 UI / JSON 密码字符串无法保证即时擦除，因此不记录请求、异常正文或转储，不能宣称内存中永不出现明文。

## 恢复及发布规则

系统 Password/PIN/Hello 入口始终保留，无 Provider Filter、无 LSA 插件、无自动登录注册表密码。默认安装服务和管理器，Provider 必须在绑定后单独启用；启用前明确确认原生恢复途径可用。服务故障、断开设备、触摸超时、数据损坏时不给凭据，提示切换登录方式。

本轮只执行构建和隔离单元测试；不安装/启动服务，不注册 DLL，不编程/认证已插入的钥匙，不触发 Windows 登录。尚未进行真实登录验证的版本必须标为预发布，不能以测试替身或编译成功代替端到端通过。

