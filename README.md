# DSH-Launcher

**An unofficial one-click launcher for DeepSeek Harness (dsh).**

> ⚠️ **非官方声明 · Unofficial notice**：DSH-Launcher 是**第三方、社区维护的非官方启动器**，**与 DeepSeek 及 deepseek-ai 组织没有任何关联，未获得其背书或赞助，也不是 DeepSeek 的官方产品**。
> *"DeepSeek Harness" is a registered trademark of DeepSeek.* 本项目的命名遵循 [DeepSeek Harness 品牌指南](https://github.com/deepseek-ai/deepseek-harness/blob/master/BRAND_GUIDELINES.md) 的建议，使用缩写 "DSH"。
>
> **官方项目 / Official**：<https://github.com/deepseek-ai/deepseek-harness> · <https://deepseek.com/harness>


## <span>一切的一切来源于某人的一句</span><br><br><span style="font-size:24px;color:red">“为什么不能直接安装啊”</span><br><span style="font-size:24px;color:red">“nodejs是什么啊”</span><br><span style="font-size:24px;color:red">“cmd是什么啊”</span><br><span style="font-size:24px;color:red">“为什么他下载一直卡着不动啊”</span><br><span style="font-size:24px;color:red">“为什么每次都要输指令才能进去啊”</span><br><span style="font-size:24px;color:red">“为什么还要只能用浏览器打开阿”</span><br><span style="font-size:24px;color:red">“什么是key啊”</span><br><span style="font-size:24px;color:red">“为什么还必须要花钱啊”</span><br><span style="font-size:24px;color:red">“我用豆包写代码都不用这么麻烦的”</span>

</font></font>
DSH-Launcher 是一个 Windows 64 位便携式启动器。单文件 `DSH-Launcher.exe`，首次运行检测到未安装的前置后会根据当前的网络情况自动选择并下载搭建 Node.js、pnpm 与 deepseek-harness 源码，构建后在本机 `http://127.0.0.1:3080` 起服务，并用内嵌的 WebView2 打开网页界面。
完成安装后直接点击该启动器则可以像其他agent软件一样直接打开deepseek-harness

~~当你刚学计算机的朋友兴致勃勃地让你帮忙安装时，你可以直接把这个东西甩给他。~~
## 使用

1. 下载 `DSH-Launcher.exe`（或按下面「从源码编译」自己构建）。
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

脚本会自动按顺序编译两次：先用 `DSH-uninstall.cs` 编出 `uninstall.exe`，再把它连同图标、WebView2 DLL 一起作为内嵌资源编进 `DSH-Launcher.exe`。产物输出到仓库根目录 `DSH-Launcher.exe`，中间产物在 `.build\`。

## 下载源与配置

运行时下载的 Node.js / pnpm / deepseek-harness 来源、镜像、哈希校验、超时等全部由 `sources.json` 控制，构建时内嵌进 `DSH-Launcher.exe`。改源只需编辑 `sources.json` 后重新编译。

## 第三方组件与许可

本启动器本身以 MIT 许可发布（见 `LICENSE`）。它下载或再分发的第三方组件许可如下（完整清单见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)）：

| 组件 | 许可 | 说明 |
|---|---|---|
| deepseek-harness | MIT（© 2026 DeepSeek） | 运行时下载并构建运行 |
| Node.js | MIT（含捆绑组件声明） | 运行时下载 |
| pnpm | MIT | 运行时下载 |
| Microsoft.Web.WebView2 SDK | BSD-3-Clause 风格（© Microsoft） | `assets\` 内随仓库再分发 |
| Microsoft Edge WebView2 Runtime | Microsoft WebView2 Runtime 许可条款（专有） | 由官方 bootstrapper 安装，**不随本仓库分发** |

## 商标与法律说明

- 本软件为**非官方**第三方作品，与 DeepSeek、deepseek-ai 无关联、未获背书。
- 运行时自动下载并执行 deepseek-harness（MIT 许可）等第三方代码，下载源与版本见 `sources.json`（已固定版本 + SHA256 校验）。
- 本项目不使用 DeepSeek 官方 logo/品牌素材，也不暗示官方合作或授权。

> 本文档为事实性/许可说明，不构成正式法律意见。

