# DSH-mini

DeepSeek Harness 的便携式自启动器（Windows，64 位）。单文件 `DSH-mini.exe`，首次运行会自动下载并搭建好 Node.js、pnpm 和 deepseek-harness 源码，构建后在本机 `http://127.0.0.1:3080` 起服务，并用内嵌的 WebView2 打开网页界面。

> 当前为**最初测试版本**（tag `v0.1.0-test`），尚未正式发布。

## 使用

1. 下载 `DSH-mini.exe`（或按下面“从源码编译”自己构建）。
2. 双击运行。首次启动会自动下载依赖并构建，之后每次启动直接复用，无需再次联网构建。
3. 运行时它会在自身目录旁生成：
   - `deepseek-harness\` —— 下载的 harness 源码与构建产物
   - `.launcher\` —— 便携式 Node.js/pnpm 运行时、包缓存、构建日志
   - `卸载.exe` —— 卸载器（每次启动时自动补回，用于一键删除以上目录）

## 从源码编译

前置条件：64 位 Windows + .NET Framework 4.x（自带 `csc.exe`）。

```bat
build.cmd
```

或：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

脚本会自动按顺序编译两次：先用 `DSH-uninstall.cs` 编出 `uninstall.exe`，再把它连同图标、WebView2 DLL 一起作为内嵌资源编进 `DSH-mini.exe`。产物输出到仓库根目录 `DSH-mini.exe`，中间产物在 `.build\`。

## 下载源与配置

运行时下载的 Node.js / pnpm / deepseek-harness 来源、镜像、哈希校验、超时等全部由 `sources.json` 控制，构建时内嵌进 `DSH-mini.exe`。改源只需编辑 `sources.json` 后重新编译。

## 资源与许可说明

- `assets\Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.WinForms.dll`、`WebView2Loader.dll` 来自微软官方 NuGet 包 [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2)（MIT，允许再分发）。本项目按“单 .cs + 脚本编译”的极简结构，直接随仓库附带这些 DLL，不额外引入 NuGet 拉取步骤。
- `assets\` 下其余图片/图标（`dafeiyu.*`、`xiezai.*`、`error.png`）为本项目资源。

## 说明

- 本启动器仅负责“下载 + 构建 + 起服务 + 打开页面”，它不包含 deepseek-harness 的代码本身；后者在首次运行时从 `sources.json` 配置的源下载。
- 卸载请运行 `卸载.exe`；它只删除本启动器自己下载的内容，不触碰系统级 Node/npm 缓存。
