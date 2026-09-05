> 🇨🇳 **For Chinese readers:** 中文版请见 → [**README.md**](README.md)

---

# 🚀 DeepSeek Harness Desktop (Windows)

> DeepSeek Harness in a native Windows window — **install and go, as easy as any normal app**.

![Version](https://img.shields.io/badge/version-0.7.0-2b6cb0)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4)
![Framework](https://img.shields.io/badge/.NET-8.0-512bd4)
![Runtime](https://img.shields.io/badge/runtime-Bundled%20Node.js%2C%20dsh%20auto-installed%20on%20first%20launch-4ea04e)
![License](https://img.shields.io/badge/license-MIT-green)

A native desktop shell built with **WinUI 3** + **WebView2** that wraps DeepSeek Harness's
Web UI (`dsh web`) into a Windows app. It quietly spins up a local `dsh web` service process,
embeds the `http://127.0.0.1:<port>/?token=...` interface into a native window via WebView2, and
cleans everything up when you quit — no leftover processes.

---

## ✨ Highlights

| | |
|---|---|
| 🪄 **Zero CLI setup** | The installer ships Node.js; on first launch the app downloads the latest dsh automatically (fastest mirror auto-selected) — **nothing to install by hand** |
| 🧩 **Auto-installed & bound** | dsh is installed into its own folder (`DeepSeek Harness` on the app's drive), located & reused automatically; survives app upgrades/reinstalls |
| 🖥️ **Native experience** | Modern WinUI 3 window + WebView2 rendering, with its own user-data dir separate from system Edge |
| 🔌 **Plugin marketplace** | Discover and install plugins from multiple sources (DSH Market / npm / npmmirror); service auto-restarts after install |
| 🩹 **Crash self-healing** | Plugin crashed the service? Auto-block + restart with safe config, log popup, one-click recovery |
| 🎛️ **Handy toolbar** | Back / Forward / Reload / Open in browser / Check for updates / Plugins / Settings, all in one place |

### More details

- On first launch, if dsh is missing: pings **npm official / npmmirror / Tencent / Huawei** mirrors,
  picks the lowest-latency one, installs `latest`, and streams progress into the **black startup log console**
- If dsh already exists it is reused as-is (no re-download); use the toolbar "Check for updates" to upgrade manually
- Auto-starts `dsh web` with `--no-open --port 0`; the OS assigns a free port, so **no port conflicts ever**
- Captures the full authenticated URL from dsh web (including `?token=...`), so the WebView never trips auth errors
- When a plugin fails to load: auto-uninstall the culprit → restart with safe config → popup showing
  the plugin name & error log, offering "Restart" or "Restore plugin"
- Unified title bar: content extends into the title bar; navigation & actions sit in one draggable modern bar
- You can manually specify a fallback dsh executable path in Settings
- Kills the whole process tree with `taskkill /T /F` on exit — **no leftovers**
- Inno Setup installer: Start Menu + desktop shortcut + uninstaller, **no admin required**

---

## 📥 Installation

Double-click `artifacts/DshDesktop-Setup-0.7.0.exe` and follow the wizard — no admin rights needed.
The installer will warn you if the WebView2 Runtime is missing.

> Portable version: `artifacts/win-x64/DshDesktop.exe` — unzip and run.

---

## 🚀 Quick Start

1. **Configure your API Key**: create a `.env` file in your home directory (`C:\Users\<you>`):

   ```env
   DEEPSEEK_API_KEY=sk-xxxx
   ```

2. **Launch the app**: double-click the desktop icon. The first launch needs internet — the app
   downloads and installs the latest `@deepseek-ai/dsh` (fastest mirror chosen automatically) and
   streams the progress into the black startup log; once done it starts the Web UI immediately.
   Subsequent launches reuse the installed dsh.

   > 📍 dsh is installed into a `DeepSeek Harness` folder on the app's drive (auto-created),
   > e.g. app under `H:\...` → dsh goes to `H:\DeepSeek Harness`.
   > If auto-install fails (offline / no write permission at drive root), the app blocks startup and
   > asks you to retry; you can also specify a dsh path manually in Settings (`dsh.cmd` / `dsh.exe` / `bin.js`).

3. **Install plugins**: click the "Plugins" button in the toolbar, pick from the DSH Market / npm list
   and install; the service restarts automatically. If a plugin crashes the service, the app auto-blocks
   and uninstalls it, shows the error log in a popup, and lets you choose "Restart" or "Restore plugin".

---

## 🛠️ Building from Source

### Build machine requirements

| Item | Requirement | Notes |
|---|---|---|
| OS | Windows 10 / 11 | |
| .NET SDK | **8.0+** | `dotnet` command must be available |
| Node.js + npm | **18+** | `node` / `npm` commands must be available (to build the bundled node/npm runtime) |
| Inno Setup | **7** | `iscc` command must be available (see note below) |

> ⚠️ **Important: build scripts resolve tools via PATH**
>
> All build scripts (`build.ps1` / `build-all.cmd` / `build-publish.cmd` / `prepare-runtime.cmd`)
> no longer hard-code tool install paths — they **resolve `dotnet`, `node`, `npm`, `iscc` directly
> from the system PATH**.
>
> Before building, make sure these commands work directly in your terminal:
>
> ```powershell
> dotnet --version   # any version output is fine
> node --version
> npm --version
> ```
>
> 💡 **About Inno Setup**: `iscc` is **not** on PATH by default. The build scripts look it up on PATH
> first, and if not found, automatically probe common install directories
> (e.g., `C:\Program Files\Inno Setup 7\...`). To make it permanent, you can add the folder
> containing `ISCC.exe` to PATH manually.
>
> If a command is missing, the script prints a clear `xxx_NOT_FOUND` error instead of failing silently.

### One-click build (recommended)

```powershell
# Full pipeline: prepare bundled node/npm runtime + self-contained publish + copy runtime + Inno Setup installer
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
# or
scripts\build-all.cmd
```

Artifacts:
- `artifacts/win-x64/DshDesktop.exe` — portable, run directly (includes bundled node/npm runtime)
- `artifacts/DshDesktop-Setup-0.7.0.exe` — installer

Just for dev/debug:

```powershell
dotnet build DshDesktop/DshDesktop.csproj
```

---

## 📂 Project Structure

```
desktop/
├── DshDesktop/                # WinUI 3 app (C# / .NET 8)
│   ├── MainWindow.xaml        #   Main window: unified title bar + WebView2 + startup log/status
│   ├── Services/
│   │   ├── DshHostProcess.cs  #   dsh web child process (parses auth URL, crash detection, cleanup)
│   │   ├── DshLocator.cs      #   dsh lookup (auto-install dir → manual path → PATH/npm global)
│   │   ├── DshUpdater.cs      #   dsh install/upgrade (multi-source ping + npm + progress/cancel/timeout)
│   │   ├── DshPaths.cs        #   Path constants (bundled node/npm & dsh install root)
│   │   ├── PluginManager.cs   #   Plugin management (dsh plugin command)
│   │   ├── PluginDialog.cs    #   Multi-source plugin marketplace dialog
│   │   ├── AppSettings.cs     #   User settings (settings.json)
│   │   └── SettingsDialog.cs  #   Settings dialog (manual dsh path)
│   ├── Assets/                #   App icon
│   └── runtime/               #   Bundled node/npm/pnpm runtime (generated at build time, no dsh)
├── installer/setup.iss        # Inno Setup 7 install script
├── scripts/
│   ├── build-all.cmd          #   One-click full build (prepare-runtime → publish → runtime → installer)
│   ├── build-publish.cmd      #   Publish + copy runtime + installer only (assumes runtime is ready)
│   ├── build.ps1              #   One-click build (PowerShell, resolves tools from PATH)
│   ├── prepare-runtime.cmd    #   Prepare bundled runtime (node + npm dist + pnpm, no dsh)
│   ├── verify-artifacts.cmd   #   Verify artifacts (bundled node/npm + installer timestamps)
│   └── generate-icon.ps1      #   Generate app icon
└── artifacts/                 # Build output (win-x64/ publish dir + Setup exe)
```

### Step-by-step build

```powershell
# 1. Prepare bundled runtime (copy node + bundle npm/pnpm; needs Node.js online; does NOT install dsh)
scripts\prepare-runtime.cmd

# 2. Build publish dir + installer (publish + copy runtime + Inno Setup)
scripts\build-publish.cmd

# 3. (Optional) Verify artifacts: print bundled node version, check installer & main exe timestamps
scripts\verify-artifacts.cmd
```

> 📝 Note: the `runtime/` directory (Node.js + npm/pnpm) is generated by `prepare-runtime.cmd`.
> It's a build artifact, excluded via `.gitignore`, and not committed to the repo. **dsh is NOT here** —
> it is auto-installed by the app on the end user's machine on first launch into its own directory.

---

## ❓ FAQ

- **"Auto-installing dsh…" fails on first launch**: internet is required. If every npm source
  (official + China mirrors) is unreachable, or the install dir (`DeepSeek Harness` at the app drive
  root) isn't writable, install fails. Check the network / move the app to a writable drive, then click
  "Reload" in the toolbar to retry, or specify a dsh path manually in Settings.
- **"dsh web authentication required"**: upgrade to v0.7.0 (older versions didn't capture dsh 0.1.2's
  auth token). If it persists, an outdated dsh is installed — upgrade it via "Check for updates".
- **Plugin install/restore needs internet**: `dsh plugin` installs via pnpm from the npm registry.
- **A plugin crashed and got auto-blocked**: the app uninstalled the culprit and restarted with a safe
  config. Go to the "Blocked" section of the Plugins dialog and choose "Restore" to retry.
- **WebView2 Runtime missing**: install the Evergreen version from
  <https://developer.microsoft.com/microsoft-edge/webview2/>.
- **Blank UI / service issues**: the status bar shows a service log summary; you can also click
  "Open in browser" to inspect the same address in your browser.

---

## 🔬 Technical Notes

- Target framework `net8.0-windows10.0.19041.0`, `WindowsPackageType=None` (unpackaged)
- `WindowsAppSDKSelfContained=true` + `--self-contained true`: all runtimes ship with the app
- Bundled runtime: `runtime/node.exe` (with VC++ libs) + `runtime/node_modules/npm` + pnpm
  (**no dsh**)
- dsh install root: `Path.GetPathRoot(app dir) + "DeepSeek Harness"`; npm installs to its
  `node_modules/@deepseek-ai/dsh`; at startup the app looks here first and auto-installs `latest` if missing
- dsh resolution order: **auto-install directory** → user-configured path → PATH / npm global
- dsh install/upgrade: pings 4 npm sources (official / npmmirror / Tencent / Huawei) in parallel, picks
  the lowest latency, installs with the bundled npm, with live progress, cancel, and a 15-min timeout
- WebView2 uses its own user-data dir (`%LOCALAPPDATA%\DshDesktop\WebView2`), isolated from system Edge
- New windows (`target=_blank`) open in the system default browser

---

## 📄 License

[MIT](LICENSE)
