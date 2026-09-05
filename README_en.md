# 🚀 DeepSeek Harness Desktop (Windows)

> DeepSeek Harness in a native Windows window — **install and go, as easy as any normal app**.

![Version](https://img.shields.io/badge/version-0.6.0-2b6cb0)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4)
![Framework](https://img.shields.io/badge/.NET-8.0-512bd4)
![Runtime](https://img.shields.io/badge/runtime-Bundled%20Node.js%20%2B%20dsh-4ea04e)
![License](https://img.shields.io/badge/license-MIT-green)

A native desktop shell built with **WinUI 3** + **WebView2** that wraps DeepSeek Harness's
Web UI (`dsh web`) into a Windows app. It quietly spins up a local `dsh web` service process,
embeds the `http://127.0.0.1:<port>` interface into a native window via WebView2, and cleans
everything up when you quit — no leftover processes.

---

## ✨ Highlights

| | |
|---|---|
| 🪄 **Out of the box** | Bundles Node.js + `@deepseek-ai/dsh` runtime — install, run, **zero CLI setup** |
| 🖥️ **Native experience** | Modern WinUI 3 window + WebView2 rendering, with its own user-data dir separate from system Edge |
| 🔌 **Plugin marketplace** | Discover and install from DSH Market (**3400+ plugins**), service auto-restarts after install |
| 🩹 **Crash self-healing** | Plugin crashed the service? Auto-block + restart with safe config, log popup, one-click recovery |
| 🧳 **Self-contained release** | No .NET / Windows App SDK / Node.js runtime needed on the target machine |
| 🎛️ **Handy toolbar** | Back / Forward / Reload / Open in browser / Plugins / Settings, all in one place |

### More details

- Auto-starts `dsh web` with `--no-open --port 0`; the OS assigns a free port, so **no port conflicts ever**
- When a plugin fails to load: auto-uninstall the culprit → restart with safe config → popup showing
  the plugin name & error log, offering "Restart" or "Restore plugin"
- You can manually specify a fallback dsh executable path in Settings
- Kills the whole process tree with `taskkill /T /F` on exit — **no leftovers**
- Inno Setup installer: Start Menu + desktop shortcut + uninstaller, **no admin required**

---

## 📥 Installation

Double-click `artifacts/DshDesktop-Setup-0.6.0.exe` and follow the wizard — no admin rights needed.
The installer will warn you if the WebView2 Runtime is missing.

> Portable version: `artifacts/win-x64/DshDesktop.exe` — unzip and run.

---

## 🚀 Quick Start

1. **Configure your API Key**: create a `.env` file in your home directory (`C:\Users\<you>`):

   ```env
   DEEPSEEK_API_KEY=sk-xxxx
   ```

2. **Launch the app**: double-click the desktop icon. The app automatically starts the Web UI with its
   bundled dsh — no extra installs. If the bundled runtime misbehaves (e.g., quarantined by
   antivirus), specify a dsh path (`dsh.cmd` / `dsh.exe` / `bin.js`) manually in Settings as a fallback.

3. **Install plugins**: click the "Plugins" button in the toolbar, pick from the DSH Market list and
   install; the service restarts automatically. If a plugin crashes the service, the app auto-blocks
   and uninstalls it, shows the error log in a popup, and lets you choose "Restart" or "Restore plugin".

---

## 🛠️ Building from Source

### Build machine requirements

| Item | Requirement | Notes |
|---|---|---|
| OS | Windows 10 / 11 | |
| .NET SDK | **8.0+** | `dotnet` command must be available |
| Node.js + npm | **18+** | `node` / `npm` commands must be available (internet needed to install dsh) |
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
# Full pipeline: prepare bundled runtime + self-contained publish + copy runtime + Inno Setup installer
powershell -ExecutionPolicy Bypass -File scripts/build.ps1
# or
scripts\build-all.cmd
```

Artifacts:
- `artifacts/win-x64/DshDesktop.exe` — portable, run directly (includes bundled runtime)
- `artifacts/DshDesktop-Setup-0.6.0.exe` — installer

Just for dev/debug:

```powershell
dotnet build DshDesktop/DshDesktop.csproj
```

---

## 📂 Project Structure

```
desktop/
├── DshDesktop/                # WinUI 3 app (C# / .NET 8)
│   ├── MainWindow.xaml        #   Main window: toolbar + WebView2 + status overlay
│   ├── Services/              #   DshHostProcess, DshLocator, AppSettings, SettingsDialog
│   ├── Assets/                #   App icon
│   └── runtime/               #   Bundled runtime: node.exe + node_modules/@deepseek-ai/dsh (generated at build time)
├── installer/setup.iss        # Inno Setup 7 install script
├── scripts/
│   ├── build-all.cmd          #   One-click full build (prepare-runtime → publish → runtime → installer)
│   ├── build-publish.cmd      #   Publish + copy runtime + installer only (assumes runtime is ready)
│   ├── build.ps1              #   One-click build (PowerShell, resolves tools from PATH)
│   ├── prepare-runtime.cmd    #   Prepare bundled runtime (npm install dsh prod deps + copy node/VC++ libs)
│   ├── verify-artifacts.cmd   #   Verify artifacts (bundled node/dsh versions + installer timestamps)
│   └── generate-icon.ps1      #   Generate app icon
└── artifacts/                 # Build output (win-x64/ publish dir + Setup exe)
```

### Step-by-step build

```powershell
# 1. Prepare bundled runtime (auto-installs @deepseek-ai/dsh into runtime/, needs Node.js + npm online)
scripts\prepare-runtime.cmd

# 2. Build publish dir + installer (publish + copy runtime + Inno Setup)
scripts\build-publish.cmd

# 3. (Optional) Verify artifacts: print bundled node / dsh versions, check installer & main exe timestamps
scripts\verify-artifacts.cmd
```

> 📝 Note: the `runtime/` directory (Node.js + dsh and its dependencies) is generated by
> `prepare-runtime.cmd`. It's a build artifact, excluded via `.gitignore`, and not committed to the repo.

---

## ❓ FAQ

- **"DeepSeek Harness CLI not detected"**: the bundled runtime is missing or broken. Reinstall the app,
  or specify a dsh path manually in Settings and click "Reload" in the toolbar.
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
- Bundled runtime: `runtime/node.exe` (with VC++ libs) + `runtime/node_modules/@deepseek-ai/dsh`
  (prod deps only, `--allow-scripts` keeps native packages like node-pty/koffi)
- dsh resolution order: **bundled runtime** → user-configured path → PATH / npm global
- WebView2 uses its own user-data dir (`%LOCALAPPDATA%\DshDesktop\WebView2`), isolated from system Edge
- New windows (`target=_blank`) open in the system default browser

---

## 📄 License

[MIT](LICENSE)
