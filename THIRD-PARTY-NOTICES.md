# 第三方依赖

精确版本与传递依赖见每个项目的 `packages.lock.json`。

- Yubico.YubiKey / Yubico.Core：Yubico 官方 .NET SDK，Apache-2.0，见 [源项目与许可证](https://github.com/Yubico/Yubico.NET.SDK)。Yubico.NativeShims 为 SDK 传递依赖，包含其原生运行库，应遵守原生组件许可证。
- Microsoft Windows App SDK / Windows SDK BuildTools / .NET runtime / Extensions / ProtectedData：见对应 NuGet 包中的 LICENSE、ThirdPartyNotices 与 Microsoft 官方许可条款。
- xUnit、Microsoft.NET.Test.Sdk 与测试 runner：仅供开发测试，不属于安装载荷。

打包自动复制 NuGet 包中的 LICENSE、NOTICE、COPYING、ThirdPartyNotices 与 nuspec 至 `licenses/`，并生成含包版本和 SHA-512 的 `dependencies.json`。
