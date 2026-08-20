# DeepSeek Harness 桌面客户端（Windows）

基于 **WinUI 3** + **WebView2** 的桌面壳，将 DeepSeek Harness 的
Web UI（`dsh web`）包装成原生 Windows 应用，像安装普通软件一样使用。

它启动一个本地的 `dsh web` 服务进程，并用 WebView2 把 `http://127.0.0.1:<port>` 的
界面嵌入原生窗口；退出应用时自动终止服务进程。

## 功能

- 原生 WinUI 3 窗口，WebView2 渲染 Web UI（独立于系统 Edge 的用户数据目录）
- **捆绑 Node.js + `@deepseek-ai/dsh` 运行时，打开即用**——无需预先安装任何 CLI
- 自动启动 `dsh web` 服务（`--no-open --port 0`，解析输出中的真实 URL）
- **插件管理**：从 DSH Market（dsh.market，收录 3400+ 插件）发现并一键安装插件，自动重启服务
- **崩溃自愈**：插件加载失败导致服务崩溃时，自动屏蔽并卸载报错插件、以安全配置重启，
  弹窗展示插件名与报错日志，提供一键重启 / 恢复该插件
- 工具栏：后退 / 前进 / 重新加载 / 在系统浏览器中打开 / 插件管理 / 设置
- 设置中可手动指定 dsh 可执行文件路径（备用）
- **自包含**发布：目标机器无需安装 .NET 运行时、Windows App SDK 运行时与 Node.js
- Inno Setup 安装器：开始菜单 + 桌面快捷方式 + 卸载程序

## 目录结构

```
desktop/
├── DshDesktop/            # WinUI 3 应用（C# / .NET 8）
│   ├── MainWindow.xaml    #   主窗口：工具栏 + WebView2 + 状态覆盖层
│   ├── Services/          #   DshHostProcess（服务进程）、DshLocator（CLI 定位）、AppSettings、SettingsDialog
│   ├── Assets/            #   应用图标
│   └── runtime/           #   捆绑运行时：node.exe + node_modules/@deepseek-ai/dsh（构建时生成）
├── installer/setup.iss    # Inno Setup 7 安装脚本
├── scripts/
│   ├── build-all.cmd      #   一键构建：prepare-runtime → publish → 拷贝 runtime → 安装器
│   ├── prepare-runtime.cmd#   准备捆绑运行时（npm 安装 dsh 仅生产依赖 + 拷贝 node/VC++ 运行库）
│   ├── build.ps1          #   一键构建（PowerShell 版本）
│   └── generate-icon.ps1  #   生成应用图标
└── artifacts/             # 构建产物（win-x64/ 发布目录 + Setup exe）
```

## 环境要求

| 项目 | 要求 |
|---|---|
| 构建机 | Windows 10/11、.NET SDK 8.0+、Inno Setup 7、Node.js + npm（联网安装 dsh） |
| 运行机 | Windows 10 1809+（x64）、Microsoft Edge WebView2 Runtime（Windows 11 自带） |
| dsh CLI | **已捆绑**，无需单独安装 |

## 构建

```powershell
# 一键构建（准备捆绑运行时 + publish 自包含 + 拷贝 runtime + Inno Setup 安装器）
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
# 或
scripts\build-all.cmd
```

产物：
- `artifacts/win-x64/DshDesktop.exe` — 免安装直接运行（含捆绑运行时）
- `artifacts/DshDesktop-Setup-0.1.0.exe` — 安装器

如仅需开发调试：

```powershell
dotnet build DshDesktop/DshDesktop.csproj
```

## 安装

双击 `artifacts/DshDesktop-Setup-0.1.0.exe`，按向导完成安装。无需管理员权限。
安装时若检测到缺少 WebView2 Runtime 会给出提示。

## 使用

1. **配置 API Key**：在用户主目录（`C:\Users\<你>`）创建 `.env`：

   ```env
   DEEPSEEK_API_KEY=sk-xxxx
   ```

   桌面客户端以用户主目录为工作目录启动服务，会读取该 `.env`。

2. **启动应用**：应用自动使用自带的捆绑 dsh 启动 Web UI，无需任何额外安装。
   若捆绑运行时异常（如被安全软件隔离），可在"设置"中手动指定 dsh 路径
   （`dsh.cmd` / `dsh.exe` / `bin.js`）作为备用。

3. **安装插件**：点击工具栏"插件"按钮，从 DSH Market 插件列表选择安装；安装完成后
   服务自动重启。若某插件导致服务崩溃，应用会自动屏蔽并卸载它、弹窗展示报错日志，
   你可选择"一键重启"或"恢复该插件"。

## 常见问题

- **提示"未检测到 DeepSeek Harness CLI"**：说明捆绑运行时缺失或异常。请重新
  安装本应用，或在"设置"中手动指定 dsh 路径后点工具栏"重新加载"。
- **插件安装/恢复需要联网**：`dsh plugin` 通过 pnpm 从 npm registry 安装，需联网。
- **插件导致崩溃被自动屏蔽**：应用会卸载报错插件并以安全配置重启。可在"插件"
  对话框"已屏蔽"分区里选择"恢复"重新安装尝试。
- **提示缺少 WebView2 Runtime**：到
  <https://developer.microsoft.com/microsoft-edge/webview2/> 安装 Evergreen 版本。
- **端口冲突**：应用以 `--port 0` 启动，由系统自动分配空闲端口，无需关心。
- **界面空白/服务异常**：状态栏显示服务日志摘要；也可点"在系统浏览器中打开"
  用浏览器访问同一地址排查。

## 技术说明

- 目标框架 `net8.0-windows10.0.19041.0`，`WindowsPackageType=None`（unpackaged）
- `WindowsAppSDKSelfContained=true` + `--self-contained true`：运行时全部随应用分发
- 捆绑运行时：`runtime/node.exe`（含 VC++ 运行库）+ `runtime/node_modules/@deepseek-ai/dsh`
  （仅生产依赖，通过 `--allow-scripts` 保留 node-pty/koffi 等原生包）
- 客户端按优先级定位 dsh：**捆绑运行时** → 用户设置路径 → PATH/npm 全局
- WebView2 使用独立用户数据目录（`%LOCALAPPDATA%\DshDesktop\WebView2`），
  与系统 Edge 互不影响
- 服务进程退出时用 `taskkill /T /F` 终止整棵子进程树，避免残留
- 新窗口（`target=_blank`）交给系统默认浏览器打开

## 快速开始（从克隆到构建）

```powershell
git clone <仓库地址>
cd desktop

# 1. 准备捆绑运行时（自动下载安装 @deepseek-ai/dsh 到 runtime/，需 Node.js + npm）
scripts\prepare-runtime.cmd

# 2. 构建发布目录与安装器（publish + 拷贝 runtime + Inno Setup）
scripts\build-publish.cmd
#   或全流程（含重新准备运行时）：
scripts\build-all.cmd
```

产物位于 `artifacts/`：`DshDesktop-Setup-<version>.exe` 安装器与 `win-x64/` 免安装版本。

> 说明：`runtime/` 目录（Node.js + dsh 及其依赖）由 `prepare-runtime.cmd` 生成，
> 属于构建产物，已通过 `.gitignore` 排除，不会提交到仓库。

## 许可证

[MIT](LICENSE)
