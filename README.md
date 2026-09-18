> 🌐 **For English readers:** the English version is here → [**README_en.md**](README_en.md)

---

# 🚀 DeepSeek Harness 桌面客户端（Rust / Tauri 版）

> 把 DeepSeek Harness 装进一个原生窗口 —— **装完即用，像用普通软件一样简单**。
> v1.0.0 起由 C#/WinUI 3 完全重写为 **Rust + Tauri 2**，跨 Windows / macOS / Linux。

![版本](https://img.shields.io/badge/版本-1.0.0-2b6cb0)
![框架](https://img.shields.io/badge/Rust-Tauri%202-dea584)
![平台](https://img.shields.io/badge/平台-Windows%20%7C%20macOS%20%7C%20Linux-0078d4)
![运行时](https://img.shields.io/badge/运行时-自带%20Node.js%2C%20dsh%20首次启动自动安装-4ea04e)
![许可证](https://img.shields.io/badge/许可证-MIT-green)

原生窗口内嵌系统 WebView，把 DeepSeek Harness 的 Web UI（`dsh web`）包装成桌面应用。
应用在本地拉起 `dsh web` 服务进程，解析鉴权 URL 后导航到 `http://127.0.0.1:<port>/?token=...`；
退出应用时终止整棵子进程树，**绝不残留**。

---

## ✨ 亮点速览

| | |
|---|---|
| 🪄 **零 CLI 前置** | 安装包自带 Node.js 运行时；首次启动自动联网安装最新版 dsh（多源测速选最快），**无需手动装任何东西** |
| 🧩 **安装即自动绑定** | dsh 自动装到独立目录，装完即被定位并复用，升级/重装应用不受影响 |
| 🖥️ **原生体验** | Tauri 原生窗口 + 系统菜单栏（导航 / 工具），WebView 渲染服务界面 |
| 🔌 **插件市场** | 多来源（DSH Market / npm / npmmirror）发现并一键安装插件，装完服务自动重启 |
| 🩹 **崩溃自愈** | 插件搞崩服务？自动屏蔽 + 卸载 + 安全配置重启，弹窗展示日志，可在插件管理中恢复 |
| 🪶 **轻量** | Rust 原生二进制，不再捆绑 ~200MB 的 .NET 运行时 |

### 功能与 C# 版（v0.7.2）完全对齐

- 首次启动自动安装 dsh：对 **npm 官方 / npmmirror / 腾讯云 / 华为云** 4 个源并行测速，
  取 `latest` / `next` 中**较新的确切版本号**安装（杜绝"latest 标签导致版本偏斜"），进度实时显示在日志窗口
- **暂存安装 → 校验 → 整体替换** 的升级方式，失败自动回滚，绝不留"半新半旧"的坏安装
- 启动自检自愈：版本偏斜自动重装修复；`$DSH_HOME/profiles/node_modules` 模块回退缓存
  缺失/悬空时自动清除重建（"更新后必须删光重装"的根治方案）
- 服务以 `--profile web --no-open --port 0` 启动，系统分配空闲端口永不冲突；完整捕获鉴权 token
- 插件加载失败导致崩溃：自动屏蔽并卸载报错插件 → 安全重启 → 弹窗告知，可在「插件管理 → 已屏蔽」恢复
- 45 秒启动超时提示；手动检查更新 / 升级 dsh（可选升级后自动刷新插件树）
- 一键终止整棵进程树（Windows `taskkill /T /F`，Unix 进程组信号）
- 外部链接（`target=_blank`）交给系统浏览器打开；WebView 用户数据目录独立
- 设置：手动指定 dsh 路径（dsh.cmd / dsh.exe / bin.js / shell shim 自动解析为 node 直跑）、升级后刷新插件树开关

---

## 📥 安装

| 平台 | 安装包 | 说明 |
|---|---|---|
| **Windows 10/11 x64** | `artifacts/DshDesktop-Setup-1.0.0-rust.exe` | Inno Setup 向导，无需管理员权限；检测 WebView2 Runtime |
| **Ubuntu 22.04+ / Debian 12+ / UOS 1070 / deepin 23 x64** | `artifacts/*.deb` | `sudo apt install ./dsh-desktop_1.0.0_amd64.deb` |
| **macOS (Apple Silicon)** | `artifacts/*.dmg` | 未签名，首次打开需右键 → 打开 |

> 免安装版（Windows）：`src-tauri/target/release/DshDesktop.exe`（需与 `resources/` 目录放在一起）。

### 🚀 快速开始

1. **配置 API Key**：在用户主目录创建 `.env`：

   ```env
   DEEPSEEK_API_KEY=sk-xxxx
   ```

2. **启动应用**：首次启动自动下载安装 `@deepseek-ai/dsh`（自动选择最快镜像源），
   安装进度实时显示在「启动日志」窗口；装完立即自动打开 Web UI。

   > 📍 dsh 安装位置：Windows 在**应用所在盘根**的 `DeepSeek Harness` 文件夹
   > （与 C# 版一致，旧安装直接复用）；Linux/macOS 在 `~/.local/share/DeepSeek Harness`。
   > 设置中可手动指定 dsh 路径作为备用方案。

3. **安装插件**：菜单「工具 → 插件管理」，从多来源列表一键安装；崩溃自动屏蔽，可恢复。

### 使用入口（原生菜单）

- **导航**：后退 `Alt+←` / 前进 `Alt+→` / 重新加载 `Ctrl+R` / 重新连接服务 `Ctrl+Shift+H` / 在系统浏览器中打开 `Ctrl+Shift+O`
- **工具**：检查更新 / 插件管理 `Ctrl+Shift+P` / 设置 `Ctrl+,` / 启动日志 `Ctrl+L`

---

## 🛠️ 从源码构建

### 构建机环境要求

| 项目 | 要求 | 说明 |
|---|---|---|
| Rust | **1.77+** stable（Windows 需 MSVC 工具链） | `rustup` 安装 |
| Node.js + npm | **18+** | 用于生成捆绑运行时 |
| Tauri CLI | 2.x | `npm install -g @tauri-apps/cli` |
| Inno Setup | 7（仅 Windows 打包） | `iscc` 在 PATH 或默认安装目录 |
| Linux 额外依赖 | `libwebkit2gtk-4.1-dev libgtk-3-dev build-essential` | Ubuntu 22.04+ / Debian 12+ |

### Windows 一键构建

```cmd
scripts\build-all.cmd
```

流程：`prepare-runtime.ps1`（捆绑 node/npm/pnpm 到 `src-tauri/resources/runtime`）
→ `tauri build --no-bundle` → Inno Setup 打包 → 产物：

- `src-tauri/target/release/DshDesktop.exe` — 免安装直接运行
- `artifacts/DshDesktop-Setup-1.0.0-rust.exe` — 安装器

### Linux 构建 .deb（Ubuntu / Debian / UOS）

```bash
bash scripts/build-deb.sh
# 产物：src-tauri/target/release/bundle/deb/*.deb
```

### macOS 构建 .dmg

```bash
bash scripts/build-mac.sh
# 产物：src-tauri/target/release/bundle/dmg/*.dmg（未签名）
```

> 💡 Linux / macOS 也可以直接用 GitHub Actions：推送后在
> `.github/workflows/build.yml` 的三平台矩阵中自动构建并上传产物。

仅需开发调试：

```bash
cd src-tauri && cargo tauri dev    # 或 cargo build / cargo test
```

---

## 📂 目录结构

```
deepseek-harness-desktop-rust/
├── src-tauri/                  # Rust / Tauri 2 应用
│   ├── src/
│   │   ├── main.rs             #   入口：原生菜单、窗口管理、单实例
│   │   ├── state.rs            #   启动/安装/升级/崩溃自愈状态机（原 MainWindow 逻辑）
│   │   ├── commands.rs         #   Tauri 命令层（前端 invoke 接口）
│   │   ├── host.rs             #   dsh web 子进程管理（URL 解析、崩溃检测、进程树清理）
│   │   ├── locator.rs          #   dsh 定位（自动安装目录 → 手动路径 → PATH/npm 全局）
│   │   ├── installer.rs        #   暂存安装/校验/整体替换/模块回退缓存自愈
│   │   ├── registry.rs         #   npm 多源测速（官方 + 国内镜像）
│   │   ├── plugins.rs          #   插件市场多来源抓取/去重/安装/屏蔽恢复
│   │   ├── settings.rs         #   用户设置（settings.json，与 C# 版同目录可继承）
│   │   ├── paths.rs            #   跨平台路径常量
│   │   └── version.rs          #   简化 semver 比较
│   ├── ui → ../ui              #   前端页面（frontendDist）
│   ├── capabilities/           #   Tauri 权限配置
│   ├── icons/                  #   应用图标（由 AppIcon.png 生成）
│   ├── resources/runtime/      #   捆绑的 node/npm/pnpm 运行时（构建脚本生成，gitignore）
│   └── tauri.conf.json
├── ui/                         # 前端：index（状态页）/ log / plugins / settings
├── installer/setup.iss         # Inno Setup 7 安装脚本
├── scripts/
│   ├── build-all.cmd           #   Windows 一键全流程
│   ├── prepare-runtime.ps1     #   捆绑运行时（Windows）
│   ├── prepare-runtime.sh      #   捆绑运行时（Linux/macOS）
│   ├── build-deb.sh            #   Linux .deb 构建
│   └── build-mac.sh            #   macOS .dmg 构建
├── .github/workflows/build.yml #   三平台 CI 矩阵构建
└── legacy/                     # 旧 C#/WinUI 3 版本源码存档（v0.7.2）
```

---

## ❓ 常见问题

- **首次启动提示「正在自动安装 dsh」后失败**：需要联网；若所有 npm 源都不可达或安装目录无写权限会失败。
  检查网络后点菜单「导航 → 重新连接服务」重试，或在「设置」中手动指定 dsh 路径。
- **提示「dsh web authentication required」**：本版完整捕获鉴权 token；若仍出现，说明安装的 dsh 过旧，
  可在「工具 → 检查更新」升级。
- **更新 dsh 后启动报错**：v1.0.0 已内置版本偏斜检测 + 模块回退缓存自愈，启动时会自动重装修复，无需手动删除目录。
- **升级后插件树要不要刷新**：设置中「升级 dsh 后刷新插件树」默认开启（等价于在 profile 目录执行 `pnpm update`）。
- **插件导致崩溃被自动屏蔽**：可在「插件管理 → 已屏蔽」分区「恢复」。
- **Linux 提示缺 webkit**：Tauri 2 需要 `libwebkit2gtk-4.1`（Ubuntu 22.04+ / Debian 12+ / deepin 23 / UOS 1070 自带）。
- **界面空白 / 服务异常**：打开「工具 → 启动日志」查看服务输出；也可用菜单「在系统浏览器中打开」排查。

---

## 🔬 技术说明

- Tauri 2 + 系统 WebView（Windows WebView2 / macOS WKWebView / Linux WebKitGTK）
- 主窗口直接导航到 dsh web 地址：dsh 的鉴权 cookie 为 `SameSite=Strict`，只有顶级导航才能携带，
  因此**不能用 iframe 嵌入** —— 这也是本版采用"原生菜单 + 直接导航"架构的原因
- 自包含分发：捆绑 node/npm/pnpm 运行时（pnpm 固定 11.x，避免 12.x 的体积暴涨）；
  Rust 静态链接 rustls，Linux 包不依赖系统 OpenSSL
- dsh 安装根：Windows `<盘根>\DeepSeek Harness`（兼容 C# 版旧安装）；
  Unix `~/.local/share/DeepSeek Harness`（可用 `DSH_INSTALL_ROOT` 覆盖）
- 设置与插件缓存沿用 C# 版目录（`%APPDATA%\DshDesktop` / `~/.config/DshDesktop`），旧数据无缝继承
- 单实例锁：二次启动自动聚焦已有窗口
- dsh 安装/升级：4 源并行测速 → 确切版本号 → 暂存安装 → 校验 → 原子替换 → 回退缓存重置

---

## 📄 许可证

[MIT](LICENSE)
