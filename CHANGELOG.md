# 更新日志 / Changelog

> 本文件记录各版本更新内容，中英双语。
> This changelog is bilingual (English + 简体中文).

---

## [0.7.1] — 2026-09-12

### 🇬🇧 English

🐛 **Fixes — dsh updates used to leave a broken install**

- **Fixed the version skew that broke updates.** The app used to install the `latest` npm tag. For dsh, `latest` (0.1.5-rc.1) can be *older* than `next` (0.1.5-rc.2), while dsh's own dependency ranges (`^0.1.5-rc.1`) resolve to the newest matching prerelease — so npm produced a tree where the CLI stayed on rc.1 while every plugin package (`dsh-base`, `dsh-web-app`, `dsh-web-frontend`, …) jumped to rc.2. The app now resolves the **exact** version (the newer of `latest`/`next`) and installs that, so CLI and plugins always match.
- **Updates are now staged, verified and swapped atomically.** Instead of running `npm install` over the live directory — which leaves stale and file-locked leftovers behind and produced a half-updated tree — the app installs into a staging folder, verifies the result, then swaps it into place, rolling back automatically on failure. This is the automated equivalent of the "delete everything and reinstall" workaround users previously had to do by hand.
- **The dsh process tree is now fully stopped before updating**, so npm no longer collides with locked files.
- **Startup self-check & self-heal.** If the managed dsh installation is found to be inconsistent, the app reinstalls it automatically instead of failing to start.
- **Fixed the Huawei Cloud npm mirror URL** — `registry.huaweicloud.com` does not resolve; the correct host is `repo.huaweicloud.com`. One of the four mirrors was therefore always unreachable.
- **The crash-recovery path can no longer crash the app**: exceptions raised while auto-disabling a failing plugin are now caught and written to the startup log.
- **New setting: "Refresh plugin tree after upgrading dsh" (on by default).** dsh's plugin tree lives in `~/.dsh/profiles/web` and is managed by its own pnpm lockfile; upgrading the CLI never re-resolves it, so a third-party plugin could keep depending on an older `@deepseek-ai/dsh-*`. When enabled, the app runs `pnpm update` in the profile directory after a successful upgrade (and reports the result).

### 🇨🇳 中文

🐛 **修复 —— 之前 dsh 更新会留下损坏的安装**

- **修复导致更新损坏的"版本偏斜"问题。** 应用过去安装 npm 的 `latest` 标签。但对 dsh 而言
  `latest`（0.1.5-rc.1）可能比 `next`（0.1.5-rc.2）**更旧**，而 dsh 声明的依赖范围
  `^0.1.5-rc.1` 会被 npm 解析到该范围内最新的预发布版 —— 于是 npm 装出的树里
  CLI 停在 rc.1，而全部插件包（`dsh-base`、`dsh-web-app`、`dsh-web-frontend` …）升到了 rc.2。
  现在应用会解析出**确切版本**（`latest` 与 `next` 中较新者）再安装，CLI 与插件包永远一致。
- **更新改为"暂存安装 → 校验 → 整体替换"。** 过去直接在正在使用的目录上跑 `npm install`，
  陈旧文件与被占用（文件锁）的文件会残留，装出半新半旧的树；现在先装到暂存目录、校验通过后
  再整体换上去，失败自动回滚 —— 等于把用户此前"删光整个目录再重装"的手工操作自动化了。
- **更新前会把 dsh 进程树彻底停掉**，npm 不再和文件锁冲突。
- **启动自检 + 自愈。** 若发现自动安装目录里的 dsh 不一致，应用会自动重装修复，而不是启动失败。
- **修正华为云 npm 镜像地址** —— `registry.huaweicloud.com` 无法解析，正确的是
  `repo.huaweicloud.com`；此前四个镜像源里总有一个是死的。
- **崩溃恢复路径自身不会再搞崩应用**：自动屏蔽出错插件时的异常现在会被捕获并写入启动日志。
- **新增设置项「升级 dsh 后刷新插件树」（默认开启）。** dsh 的插件树位于 `~/.dsh/profiles/web`，
  由它自己的 pnpm 锁文件管理；升级 CLI 并不会重解析它，因此第三方插件可能仍依赖较旧的
  `@deepseek-ai/dsh-*`。开启后，升级成功会在该 profile 目录执行一次 `pnpm update` 并反馈结果。

---

## [0.7.0] — 2026-09-05

### 🇬🇧 English

✨ **What's New**
- **No longer bundles dsh** — the installer now ships only the Node.js runtime. On first launch the app automatically downloads and installs the latest `@deepseek-ai/dsh` (auto-selecting the fastest of npm official / npmmirror / Tencent / Huawei mirrors), streams the progress live into the startup log console, and binds the install directory — no more ~110 MB of bundled dsh in every release, and dsh is always up to date.
- **dsh auto-install on demand** — if dsh is missing at startup, the app installs it automatically; if it already exists it is reused as-is (no unnecessary auto-upgrade network call). A manual "Check for updates" button stays available for upgrading.
- **Dedicated install location** — dsh is installed into a `DeepSeek Harness` folder on the app's drive (auto-created), isolated from the app itself so it survives app updates/reinstalls.
- **Authenticated Web UI URL** — the full `dsh web` URL including its `?token=...` is now captured, fixing "dsh web authentication required" that appeared with dsh 0.1.2.

🐛 **Fixes & Improvements**
- Startup flow unified; clearer messaging when dsh must be installed or a manual path is broken.
- Settings dialog simplified (removed the now-obsolete "check for updates on startup" switch).
- Build scripts no longer bundle dsh; stale dsh links/shims are cleaned automatically.

### 🇨🇳 中文

✨ **新功能**
- **不再捆绑 dsh** —— 安装包只携带 Node.js 运行时。首次启动时应用自动联网安装最新版
  `@deepseek-ai/dsh`（自动在 npm 官方 / npmmirror / 腾讯云 / 华为云镜像中选最快者），把安装
  进度实时显示在启动日志控制台里，装完自动绑定安装目录。发布包不再背着约 110MB 的 dsh，
  而且 dsh 永远是最新版。
- **按需自动安装 dsh** —— 启动时若本机没有 dsh 就自动安装；已有则直接复用（不再每次联网
  自动升级）。工具栏仍保留「检查更新」按钮供手动升级。
- **独立安装目录** —— dsh 自动安装到应用所在盘的 `DeepSeek Harness` 文件夹（自动创建），
  与应用本体隔离，应用升级/重装也不影响。
- **带鉴权的 Web URL** —— 现在能完整捕获 dsh web 输出的 URL（含 `?token=...`），修复了
  dsh 0.1.2 起出现的 "dsh web authentication required"。

🐛 **修复与改进**
- 统一启动流程：dsh 缺失需自动安装、或手动指定路径失效时给出更清晰提示。
- 简化设置对话框（移除已失效的「启动时自动检查更新」开关）。
- 构建脚本不再捆绑 dsh，并自动清理历史残留的 dsh 链接 / shim。

---

## [0.6.0] — 2026-09-05

### 🇬🇧 English

✨ **What's New**
- **Unified title bar** — window content now extends into the title bar; navigation & app actions live in a single modern top bar that you can drag anywhere while the buttons stay clickable.
- **Startup log console** — a black, terminal-style panel under the launch banner streams the dsh/Node service logs live while starting, so the moment something goes wrong you can see exactly why.
- **Clearer startup failures** — if the service exits right after launch, the app now shows an error screen with the exit code and keeps the log visible, instead of silently flipping the status text.

🐛 **Fixes & Improvements**
- Startup-state feedback improved: no more "starting…" that quietly turns into "service exited" without explanation.
- Version bumped to **0.6.0**.

### 🇨🇳 中文

✨ **新功能**
- **一体化标题栏** —— 内容延伸到标题栏，导航与应用操作合并为一条现代顶栏；整条可拖动，按钮仍可正常点击。
- **启动日志控制台** —— 启动横幅下方新增黑底终端风格日志面板，实时显示 dsh/Node 服务启动输出，一有异常立刻可见原因。
- **启动失败提示更清晰** —— 服务启动后随即退出时，立即展示带退出码的错误界面并保留日志，不再只是悄悄改变右上角状态文字。

🐛 **修复与改进**
- 优化启动状态反馈：不再出现「正在启动…」却无声变成「服务已退出」的情况。
- 版本号升至 **0.6.0**。

---

## [0.5.0] — 2026-08-22

### 🇬🇧 English

✨ **What's New**
- **Multiple plugin sources** — plugins now come from three trusted sources (DSH Market, official npm registry, npmmirror mirror); a failed source no longer breaks the whole list.
- **Switch / stack sources** — choose "single source" or "multi-source merge" right in the plugin dialog; your choice is remembered.
- **Smart deduplication** — the same plugin found in several sources is matched by its GitHub repository link + author and shown only once, merging the best info.
- **Source labels** — every plugin shows which source(s) it came from.
- **One-click GitHub** — a GitHub button next to each install button jumps straight to the plugin's repository.
- **Regex search** — search the plugin list by regular expression against package names.
- **Offline cache** — the merged plugin list is persisted to a single local JSON file and refreshed on every update; if all sources fail, the last cached list is still shown.

### 🇨🇳 中文

✨ **新功能**
- **多插件来源** —— 插件来源扩展为三个可信源（DSH Market / npm 官方 / npmmirror 镜像），单一来源故障不再导致整个列表不可用。
- **来源切换 / 多源叠加** —— 在插件对话框中可选「单来源」或「多来源叠加」，选择会被记住。
- **智能去重** —— 同一插件在多个来源出现时，按 GitHub 仓库链接 + 作者自动匹配，只显示一个并合并最优信息。
- **来源标识** —— 每个插件都标注来自哪个（些）来源。
- **一键 GitHub** —— 每个插件的安装按钮旁新增 GitHub 按钮，一键跳转到插件仓库。
- **正则搜索** —— 按插件名使用正则表达式过滤搜索。
- **离线缓存** —— 整合后的插件列表持久化为单一本地 JSON 文件，每次刷新自动更新；全部来源不可用时仍可显示上次的缓存列表。

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
