# 更新日志 / Changelog

> 本文件记录各版本更新内容，中英双语。
> This changelog is bilingual (English + 简体中文).

---

## [0.4.0] — 2026-08-21

### 🇬🇧 English

✨ **What's New**
- **Multi-source update checks** — the app pings several npm registries (official + China mirrors: npmmirror / Tencent / Huawei) in parallel and automatically picks the lowest-latency one, so checking and upgrading work even where GitHub/npm is slow or blocked.
- **Manual "Check for Updates" button** — check any time from the toolbar, not just on startup; you always get a clear result (update available / already up to date / network error).
- **Upgrade progress + cancel** — upgrading shows a live progress dialog (streaming npm output) with a Cancel button that stops the whole process tree; a built-in 15-minute timeout prevents infinite hangs.
- **Bundled dsh upgraded to `0.1.1-rc.2`** — the latest harness, including the earlier workspace-delete `signal timeout` fix.

🐛 **Fixes & Improvements**
- Startup auto-check no longer silently fails when the official registry is unreachable — it falls back to mirrors.
- Restarting after a stuck upgrade now re-checks properly.
- Version bumped to **0.4.0**.

### 🇨🇳 中文

✨ **新功能**
- **多源自动择优** —— 并行 ping 多个 npm 源（官方 + npmmirror / 腾讯云 / 华为云等国内镜像），自动选用延迟最低的，国内网络也能稳定检查与升级。
- **手动「检查更新」按钮** —— 不再只能启动时检测，随时点工具栏即可，结果一目了然（有新版 / 已是最新 / 网络异常）。
- **升级进度 + 可取消** —— 升级弹窗实时显示 npm 输出，带「取消」按钮（终止进程树）；内置 15 分钟超时兜底，杜绝卡死。
- **捆绑 dsh 升级到 `0.1.1-rc.2`** —— 最新版 harness，包含此前删除工作区 `signal timeout` 的修复。

🐛 **修复与改进**
- 官方源不可达时，启动自动检查不再静默失败，自动回退镜像。
- 修复升级卡死后重启不再触发检查的问题。
- 版本号升至 **0.4.0**。

---

## [0.3.0] — 2026-08-20

### 🇬🇧 English

✨ **What's New**
- **In-app dsh auto-update** — on startup the app checks for a newer `@deepseek-ai/dsh`, prompts with a dialog, then installs and restarts automatically.
- **Bundled npm** — self-upgrade works on machines with no Node.js / npm installed.
- **Settings refresh** — shows the current dsh version and a toggle for the startup update check.

🐛 **Fixes & Improvements**
- Bundled dsh upgraded `0.1.0-rc.7` → `0.1.0-rc.8` (fixes `signal timeout` on workspace delete).
- Version bumped to **0.3.0**.

### 🇨🇳 中文

✨ **新功能**
- **应用内 dsh 自动更新** —— 启动时检测新版本，弹窗询问后自动安装并重启服务。
- **捆绑 npm** —— 未安装 Node.js / npm 的机器也能自升级。
- **设置页升级** —— 显示当前 dsh 版本，可开关启动时自动检查。

🐛 **修复与改进**
- 捆绑 dsh 升级 `0.1.0-rc.7` → `0.1.0-rc.8`（修复删除工作区时的 `signal timeout`）。
- 版本号升至 **0.3.0**。

---

## [0.2.0] — 2026-08-20

### 🇬🇧 English

- Added **English README** (`README_en.md`) with a language switcher on the Chinese README.
- Made build scripts **portable** — tools (`dotnet`, `node`, `npm`, `iscc`) resolved from PATH instead of hard-coded paths.
- Version bumped to **0.2.0**.

### 🇨🇳 中文

- 新增**英文 README**（`README_en.md`），并在中文 README 顶部加入语言引导。
- 构建脚本改为**可移植**——工具从 PATH 解析，不再写死安装路径。
- 版本号升至 **0.2.0**。

---

## [0.1.0] — 2026-08-20

### 🇬🇧 English

✨ **Initial Release**
- **Native Windows shell** built with WinUI 3 + WebView2, wrapping the DeepSeek Harness Web UI into a normal desktop app.
- **Out of the box** — bundles Node.js + `@deepseek-ai/dsh`, no CLI required.
- **Auto service management** — starts/stops `dsh web` automatically, no leftover processes.
- **Plugin marketplace** — install plugins from DSH Market (3400+) with one click.
- **Crash self-healing** — auto-blocks failing plugins and restarts with a safe config.
- **Self-contained release** — no .NET / Windows App SDK / Node.js runtime needed on the target machine.
- **Inno Setup installer** — Start Menu + desktop shortcut + uninstaller.

### 🇨🇳 中文

✨ **首个正式版**
- **原生 Windows 客户端**——基于 WinUI 3 + WebView2，把 DeepSeek Harness Web UI 包装成普通桌面应用。
- **打开即用**——捆绑 Node.js + `@deepseek-ai/dsh`，无需安装任何 CLI。
- **自动服务管理**——自动启停 `dsh web`，退出无残留进程。
- **插件市场**——从 DSH Market（3400+ 插件）一键安装。
- **崩溃自愈**——自动屏蔽报错插件并以安全配置重启。
- **自包含发布**——目标机器无需安装 .NET / Windows App SDK / Node.js 运行时。
- **Inno Setup 安装器**——开始菜单 + 桌面快捷方式 + 卸载程序。

---

## 📌 版本说明

- 版本号遵循语义化版本（SemVer）；`0.x` 阶段以 `rc` 后缀标记预发布。
- 产物：`artifacts/DshDesktop-Setup-<version>.exe`（安装器）与 `artifacts/win-x64/DshDesktop.exe`（免安装版）。
