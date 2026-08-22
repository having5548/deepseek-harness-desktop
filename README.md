> 🌐 **For English readers:** the English version is here → [**README_en.md**](README_en.md)

---

# 🚀 DeepSeek Harness 桌面客户端（Windows）

> 把 DeepSeek Harness 装进一个原生 Windows 窗口 —— **安装即用，像用普通软件一样简单**。

![版本](https://img.shields.io/badge/版本-0.5.0-2b6cb0)
![平台](https://img.shields.io/badge/平台-Windows%2010%2F11-0078d4)
![框架](https://img.shields.io/badge/.NET-8.0-512bd4)
![运行时](https://img.shields.io/badge/运行时-自带%20Node.js%20%2B%20dsh-4ea04e)
![许可证](https://img.shields.io/badge/许可证-MIT-green)

基于 **WinUI 3** + **WebView2** 打造的原生桌面壳，把 DeepSeek Harness 的 Web UI（`dsh web`）
包装成 Windows 应用。它会在本地悄悄拉起一个 `dsh web` 服务进程，再用 WebView2 把
`http://127.0.0.1:<port>` 的界面嵌进原生窗口；退出应用时自动收尾，不留一丝残留进程。

---

## ✨ 亮点速览

| | |
|---|---|
| 🪄 **打开即用** | 捆绑 Node.js + `@deepseek-ai/dsh` 运行时，装完就能跑，**零 CLI 前置** |
| 🖥️ **原生体验** | 现代 WinUI 3 窗口 + WebView2 渲染，拥有独立于系统 Edge 的用户数据目录 |
| 🔌 **插件市场** | 从 DSH Market（收录 **3400+** 插件）发现并一键安装，装完服务自动重启 |
| 🩹 **崩溃自愈** | 插件搞崩服务？自动屏蔽 + 安全配置重启，弹窗展示日志，一键恢复 |
| 🧳 **自包含发布** | 目标机器无需 .NET / Windows App SDK / Node.js 运行时 |
| 🎛️ **贴心工具栏** | 后退 / 前进 / 刷新 / 浏览器打开 / 插件管理 / 设置，一应俱全 |

### 更多细节

- 自动以 `--no-open --port 0` 启动 `dsh web`，由系统分配空闲端口，**永不冲突**
- 插件加载失败时：自动卸载报错插件 → 安全配置重启 → 弹窗告知插件名与报错日志，
  支持「一键重启」或「恢复该插件」
- 设置中可手动指定 dsh 可执行文件路径作为备用方案
- 退出应用时用 `taskkill /T /F` 终止整棵子进程树，**绝不残留**
- Inno Setup 安装器：开始菜单 + 桌面快捷方式 + 卸载程序，**无需管理员权限**

---

## 📥 安装

双击 `artifacts/DshDesktop-Setup-0.5.0.exe`，按向导一路「下一步」即可，无需管理员权限。
安装时若检测到缺少 WebView2 Runtime 会给出提示。

> 免安装版：`artifacts/win-x64/DshDesktop.exe`，解压即用。

---

## 🚀 快速开始

1. **配置 API Key**：在用户主目录（`C:\Users\<你>`）创建 `.env`：

   ```env
   DEEPSEEK_API_KEY=sk-xxxx
   ```

2. **启动应用**：双击桌面图标即可。应用自动使用自带的捆绑 dsh 启动 Web UI，
   无需任何额外安装。若捆绑运行时异常（如被安全软件隔离），可在「设置」中手动
   指定 dsh 路径（`dsh.cmd` / `dsh.exe` / `bin.js`）作为备用。

3. **安装插件**：点击工具栏「插件」按钮，从 DSH Market 列表选择安装，完成后服务
   自动重启。若某插件导致服务崩溃，应用会自动屏蔽并卸载它、弹窗展示报错日志，
   你可以选择「一键重启」或「恢复该插件」。

---

## 🛠️ 从源码构建

### 构建机环境要求

| 项目 | 要求 | 说明 |
|---|---|---|
| 操作系统 | Windows 10 / 11 | |
| .NET SDK | **8.0+** | 需 `dotnet` 命令可用 |
| Node.js + npm | **18+** | 需 `node` / `npm` 命令可用（联网安装 dsh） |
| Inno Setup | **7** | 需 `iscc` 命令可用（见下方说明） |

> ⚠️ **重要：构建脚本通过 PATH 定位工具**
>
> 所有构建脚本（`build.ps1` / `build-all.cmd` / `build-publish.cmd` / `prepare-runtime.cmd`）
> 都不再写死工具安装路径，而是**直接从系统 PATH 解析** `dotnet`、`node`、`npm`、`iscc`。
>
> 构建前请先确认这些命令在终端里可以直接敲出来：
>
> ```powershell
> dotnet --version   # 能输出版本号即可
> node --version
> npm --version
> ```
>
> 💡 **关于 Inno Setup**：`iscc` 默认**不在** PATH 里。构建脚本会优先从 PATH 解析，
> 找不到时会自动探测常见安装目录（`C:\Program Files\Inno Setup 7\...` 等）。
> 想一劳永逸，也可以手动把 `ISCC.exe` 所在目录加入 PATH。
>
> 若某条命令缺失，脚本会给出明确的 `xxx_NOT_FOUND` 提示，不会静默失败。

### 一键构建（推荐）

```powershell
# 全流程：准备捆绑运行时 + publish 自包含 + 拷贝 runtime + Inno Setup 安装器
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
# 或
scripts\build-all.cmd
```

产物：
- `artifacts/win-x64/DshDesktop.exe` — 免安装直接运行（含捆绑运行时）
- `artifacts/DshDesktop-Setup-0.5.0.exe` — 安装器

仅需开发调试：

```powershell
dotnet build DshDesktop/DshDesktop.csproj
```

---

## 📂 目录结构

```
desktop/
├── DshDesktop/                # WinUI 3 应用（C# / .NET 8）
│   ├── MainWindow.xaml        #   主窗口：工具栏 + WebView2 + 状态覆盖层
│   ├── Services/              #   DshHostProcess（服务进程）、DshLocator（CLI 定位）、AppSettings、SettingsDialog
│   ├── Assets/                #   应用图标
│   └── runtime/               #   捆绑运行时：node.exe + node_modules/@deepseek-ai/dsh（构建时生成）
├── installer/setup.iss        # Inno Setup 7 安装脚本
├── scripts/
│   ├── build-all.cmd          #   一键全流程构建（prepare-runtime → publish → runtime → 安装器）
│   ├── build-publish.cmd      #   仅 publish + 拷贝 runtime + 安装器（假设运行时已就绪）
│   ├── build.ps1              #   一键构建（PowerShell 版，自动从 PATH 定位工具）
│   ├── prepare-runtime.cmd    #   准备捆绑运行时（npm 安装 dsh 生产依赖 + 拷贝 node/VC++ 运行库）
│   ├── verify-artifacts.cmd   #   验证产物（捆绑 node/dsh 版本 + 安装器时间戳）
│   └── generate-icon.ps1      #   生成应用图标
└── artifacts/                 # 构建产物（win-x64/ 发布目录 + Setup exe）
```

### 分步构建

```powershell
# 1. 准备捆绑运行时（自动下载安装 @deepseek-ai/dsh 到 runtime/，需 Node.js + npm 联网）
scripts\prepare-runtime.cmd

# 2. 构建发布目录与安装器（publish + 拷贝 runtime + Inno Setup）
scripts\build-publish.cmd

# 3. （可选）验证产物：打印捆绑 node / dsh 版本，检查安装器与主程序时间戳
scripts\verify-artifacts.cmd
```

> 📝 说明：`runtime/` 目录（Node.js + dsh 及其依赖）由 `prepare-runtime.cmd` 生成，
> 属于构建产物，已通过 `.gitignore` 排除，不会提交到仓库。

---

## ❓ 常见问题

- **提示「未检测到 DeepSeek Harness CLI」**：说明捆绑运行时缺失或异常。请重新安装
  本应用，或在「设置」中手动指定 dsh 路径后点工具栏「重新加载」。
- **插件安装 / 恢复需要联网**：`dsh plugin` 通过 pnpm 从 npm registry 安装，需联网。
- **插件导致崩溃被自动屏蔽**：应用会卸载报错插件并以安全配置重启。可在「插件」
  对话框「已屏蔽」分区里选择「恢复」重新安装尝试。
- **提示缺少 WebView2 Runtime**：到
  <https://developer.microsoft.com/microsoft-edge/webview2/> 安装 Evergreen 版本。
- **界面空白 / 服务异常**：状态栏会显示服务日志摘要；也可点「在系统浏览器中打开」
  用浏览器访问同一地址排查。

---

## 🔬 技术说明

- 目标框架 `net8.0-windows10.0.19041.0`，`WindowsPackageType=None`（unpackaged）
- `WindowsAppSDKSelfContained=true` + `--self-contained true`：运行时全部随应用分发
- 捆绑运行时：`runtime/node.exe`（含 VC++ 运行库）+ `runtime/node_modules/@deepseek-ai/dsh`
  （仅生产依赖，通过 `--allow-scripts` 保留 node-pty/koffi 等原生包）
- 客户端按优先级定位 dsh：**捆绑运行时** → 用户设置路径 → PATH / npm 全局
- WebView2 使用独立用户数据目录（`%LOCALAPPDATA%\DshDesktop\WebView2`），
  与系统 Edge 互不影响
- 新窗口（`target=_blank`）交给系统默认浏览器打开

---

## 📄 许可证

[MIT](LICENSE)

