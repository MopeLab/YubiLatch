# 验证记录

日期：2026-11-09。

## 已执行

- .NET SDK 10.0.403、Yubico.YubiKey 1.18.1、Windows App SDK 1.8.260921001。
- Release 构建：Core、Windows Service、WinUI 3 管理器，零警告、零错误。
- MSVC C++20 x64 Release 构建 V2 Provider DLL；启用 `/W4 /WX /sdl /guard:cf`、ASLR、NX、CET。
- 18 项核心安全测试通过：随机挑战、单次验证、重放与错秘钥拒绝、畸形响应、长度边界、截断帧、SID 错配、无效记录、槽位保护及秘钥清零。
- 9 项服务测试通过：管理 IPC、硬件挑战流程、脱敏诊断和账户验证子进程超时。
- 独立管道测试通过：单实例忙后重连、连接超时及等待期间取消。测试使用私有管道，不加载 Provider。
- PowerShell 安装/构建脚本语法解析通过。
- WinUI 管理器启动与导航检查通过：服务断开、Provider 未注册状态显示正常，概览、钥匙管理、恢复帮助页面可用。
- self-contained 发布流程成功；文件 SHA-256、运行库、许可证及 x64 PE 头检查通过。
- Windows 11 x64、个人 Microsoft 账户及 USB YubiKey 5C NFC：服务安装、绑定恢复验证、管理码读取通过。
- 用户手动锁屏后选择 YubiLatch，触摸钥匙成功解锁；日志确认硬件验证成功、凭据已提交，Windows 返回状态及子状态均为 `0x00000000`。

## 待验证

重启、冷启动、注销、离线缓存、睡眠唤醒、多账户及多钥匙场景、安装/卸载故障回滚、WinRE 恢复、安全桌面竞态及签名发布。具体场景见 [验收矩阵](acceptance.md)。
