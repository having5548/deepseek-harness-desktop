> 🌐 **中文用户请看** → [**README.md**](README.md)

---

# 🚀 DeepSeek Harness Desktop (Rust / Tauri)

> Put DeepSeek Harness in a native window — **install and use it like any ordinary app**.
> Since v1.0.2 the app is fully rewritten from C#/WinUI 3 to **Rust + Tauri 2**, cross-platform on Windows / macOS / Linux.

![Version](https://img.shields.io/badge/version-1.0.2-2b6cb0)
![Framework](https://img.shields.io/badge/Rust-Tauri%202-dea584)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-0078d4)
![Runtime](https://img.shields.io/badge/runtime-bundled%20Node.js%2C%20dsh%20auto--install%20on%20first%20launch-4ea04e)
![License](https://img.shields.io/badge/license-MIT-green)

A native window embedding the system WebView that wraps the DeepSeek Harness web UI (`dsh web`).
The app spawns a local `dsh web` service process, parses the authenticated URL, and navigates to
`http://127.0.0.1:<port>/?token=...`. On exit the whole child process tree is terminated — **nothing is left behind**.

---

## ✨ Highlights

| | |
|---|---|
| 🪄 **Zero CLI prerequisite** | The installer bundles a Node.js runtime; on first launch the latest dsh is installed automatically (fastest-mirror detection) — **nothing to set up manually** |
| 🧩 **Auto-binding** | dsh is installed into a dedicated folder, located and reused automatically across app upgrades/reinstalls |
| 🖥️ **Native experience** | Tauri native window + system menu bar (Navigate / Tools), WebView renders the service UI |
| 🔌 **Plugin marketplace** | Discover and one-click install plugins from multiple sources (DSH Market / npm / npmmirror); service restarts automatically |
| 🩹 **Crash self-healing** | A broken plugin crashes the service? It gets blocked + uninstalled automatically, the service restarts safely, and you can restore it from the plugin manager |
| 🪶 **Lightweight** | Native Rust binary — no ~200MB .NET runtime bundled anymore |

### Feature parity with the C# version (v0.7.2)

- First-launch auto install: pings **npm official / npmmirror / Tencent / Huawei** mirrors in parallel and
  installs the **exact version** resolved from `latest`/`next` (eliminating the "latest-tag version skew" failure mode)
- **Staged install → verify → atomic swap** upgrades with automatic rollback — never leaves a half-old/half-new install
- Startup self-checks: version-skew auto-repair; the `$DSH_HOME/profiles/node_modules` module fallback cache
  is validated and cleared automatically (the real fix behind "must delete everything and reinstall after update")
- Service runs with `--profile web --no-open --port 0` (OS-assigned free port); the full auth token URL is captured
- Plugin crash flow: offending plugins are blocked and uninstalled → safe restart → dialog with the log; restorable
- 45s startup timeout notice; manual update check / dsh upgrade (optionally refreshing the plugin tree afterwards)
- Kills the entire process tree on exit (`taskkill /T /F` on Windows, process-group signal on Unix)
- External links (`target=_blank`) open in the system browser; isolated WebView user-data directory
- Settings: manual dsh path (dsh.cmd / dsh.exe / bin.js / shell shims are parsed to run via node directly),
  refresh-plugin-tree-after-upgrade toggle

---

## 📥 Install

| Platform | Package | Notes |
|---|---|---|
| **Windows 10/11 x64** | `artifacts/DshDesktop-Setup-1.0.2-rust.exe` | Inno Setup wizard, no admin required; checks WebView2 Runtime |
| **Ubuntu 22.04+ / Debian 12+ / UOS 1070 / deepin 23 x64** | `artifacts/*.deb` | `sudo apt install ./dsh-desktop_1.0.2_amd64.deb` |
| **macOS (Apple Silicon)** | `artifacts/*.dmg` | Unsigned — right-click → Open on first launch |

### 🚀 Quick start

1. **Configure the API key** — create `.env` in your home directory:

   ```env
   DEEPSEEK_API_KEY=sk-xxxx
   ```

2. **Launch the app**. On first launch it downloads and installs `@deepseek-ai/dsh`
   (fastest mirror auto-selected); progress is shown live in the "Startup log" window.

   > 📍 dsh location: Windows — `DeepSeek Harness` folder on the **drive root of the app**
   > (same as the C# version, existing installs are reused); Linux/macOS — `~/.local/share/DeepSeek Harness`.

3. **Install plugins** via "Tools → Plugin manager". Crashed plugins are blocked automatically and can be restored.

### Entry points (native menu)

- **Navigate**: Back `Alt+←` / Forward `Alt+→` / Reload `Ctrl+R` / Reconnect service `Ctrl+Shift+H` / Open in system browser `Ctrl+Shift+O`
- **Tools**: Check for updates / Plugin manager `Ctrl+Shift+P` / Settings `Ctrl+,` / Startup log `Ctrl+L`

---

## 🛠️ Build from source

### Requirements

| Item | Requirement | Notes |
|---|---|---|
| Rust | **1.77+** stable (MSVC toolchain on Windows) | via `rustup` |
| Node.js + npm | **18+** | to generate the bundled runtime |
| Tauri CLI | 2.x | `npm install -g @tauri-apps/cli` |
| Inno Setup | 7 (Windows packaging only) | `iscc` on PATH or default install dir |
| Linux extras | `libwebkit2gtk-4.1-dev libgtk-3-dev build-essential` | Ubuntu 22.04+ / Debian 12+ |

### Windows one-click build

```cmd
scripts\build-all.cmd
```

Pipeline: `prepare-runtime.ps1` (bundle node/npm/pnpm into `src-tauri/resources/runtime`)
→ `tauri build --no-bundle` → Inno Setup → artifacts:

- `src-tauri/target/release/DshDesktop.exe` — portable build
- `artifacts/DshDesktop-Setup-1.0.2-rust.exe` — installer

### Linux .deb build

```bash
bash scripts/build-deb.sh
# output: src-tauri/target/release/bundle/deb/*.deb
```

### macOS .dmg build

```bash
bash scripts/build-mac.sh
# output: src-tauri/target/release/bundle/dmg/*.dmg (unsigned)
```

> 💡 Linux/macOS can also be built by GitHub Actions — see the three-platform matrix in
> `.github/workflows/build.yml`.

Development:

```bash
cd src-tauri && cargo tauri dev    # or cargo build / cargo test
```

---

## 📂 Layout

```
deepseek-harness-desktop-rust/
├── src-tauri/                  # Rust / Tauri 2 app
│   ├── src/                    #   main / state / commands / host / locator /
│   │                           #   installer / registry / plugins / settings / paths / version
│   ├── ui → ../ui              #   frontend pages (frontendDist)
│   ├── capabilities/           #   Tauri permission config
│   ├── icons/                  #   generated from AppIcon.png
│   ├── resources/runtime/      #   bundled node/npm/pnpm (generated, gitignored)
│   └── tauri.conf.json
├── ui/                         # pages: index (status) / log / plugins / settings
├── installer/setup.iss         # Inno Setup 7 script
├── scripts/                    # build-all.cmd / prepare-runtime.{ps1,sh} / build-deb.sh / build-mac.sh
├── .github/workflows/build.yml # 3-platform CI matrix
└── legacy/                     # archived C#/WinUI 3 sources (v0.7.2)
```

---

## 🔬 Technical notes

- Tauri 2 + system WebView (WebView2 / WKWebView / WebKitGTK)
- The main webview navigates **directly** to the dsh web URL: dsh's auth cookie is `SameSite=Strict`
  and is only sent in a top-level navigation context, so an iframe embedding would never authenticate —
  hence the "native menu + direct navigation" architecture
- Self-contained distribution: bundled node/npm/pnpm runtime (pnpm pinned to 11.x to avoid the
  12.x size explosion); rustls is statically linked so the Linux build needs no system OpenSSL
- dsh install root: Windows — `<drive root>\DeepSeek Harness` (compatible with C#-era installs);
  Unix — `~/.local/share/DeepSeek Harness` (override with `DSH_INSTALL_ROOT`)
- Settings/plugin-cache reuse the C#-era directories (`%APPDATA%\DshDesktop` / `~/.config/DshDesktop`)
- Single-instance lock: a second launch focuses the existing window
- dsh install/upgrade: 4-mirror latency race → exact version → staged install → verify → atomic swap → fallback-cache reset

---

## 📄 License

[MIT](LICENSE)
