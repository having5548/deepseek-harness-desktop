# DeepSeek Harness Desktop one-click build script
# Usage: powershell -ExecutionPolicy Bypass -File scripts/build.ps1
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
# PSScriptRoot = scripts\ dir; its parent is the repo root
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "DshDesktop\DshDesktop.csproj"
$publishDir = Join-Path $root "artifacts\$Runtime"
$iss = Join-Path $root "installer\setup.iss"

# Resolve tools from PATH (no hardcoded install paths)
function Resolve-Tool([string]$name) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    return $null
}

$dotnet = Resolve-Tool "dotnet"
if (-not $dotnet) { throw "dotnet not found on PATH. Install .NET SDK and add it to PATH." }

$cmd = Resolve-Tool "cmd"
if (-not $cmd) { $cmd = "cmd" }

# ISCC is usually not on PATH: allow -IsccPath override, else PATH, else probe common install dirs
if (-not $IsccPath) {
    $iscc = Resolve-Tool "iscc"
    if (-not $iscc) {
        foreach ($p in @(
            "C:\Program Files\Inno Setup 7\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe")) {
            if (Test-Path $p) { $iscc = $p; break }
        }
    }
    $IsccPath = $iscc
}
if (-not $IsccPath) { throw "ISCC.exe not found. Install Inno Setup and add it to PATH, or pass -IsccPath." }

Write-Host "dotnet: $dotnet"
Write-Host "iscc:   $IsccPath"

# 1. Generate icon if missing
$icon = Join-Path $root "DshDesktop\Assets\AppIcon.ico"
if (-not (Test-Path $icon)) {
    Write-Host "[1/4] Generating app icon..."
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root "scripts\generate-icon.ps1")
}

# 2. Prepare bundled runtime (node + npm + pnpm) for out-of-box auto-install of dsh
Write-Host "[2/4] Preparing bundled runtime (node + npm + pnpm)..."
& $cmd /c (Join-Path $root "scripts\prepare-runtime.cmd")
if ($LASTEXITCODE -ne 0) { throw "prepare-runtime failed (exit $LASTEXITCODE)" }

# 3. Self-contained publish (target machine needs no .NET / Windows App SDK runtime)
Write-Host "[3/4] dotnet publish ($Configuration / $Runtime, self-contained)..."
& $dotnet publish $csproj -c $Configuration -r $Runtime --self-contained true -p:PublishDir=$publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# 3b. Copy bundled runtime into publish dir
Write-Host "[3b/4] Copying bundled runtime into publish dir..."
$srcRuntime = Join-Path $root "DshDesktop\runtime"
$dstRuntime = Join-Path $publishDir "runtime"
& $cmd /c "robocopy `"$srcRuntime`" `"$dstRuntime`" /E /NFL /NDL /NJH /NJS /NP >nul"
if ($LASTEXITCODE -gt 7) { throw "robocopy failed (exit $LASTEXITCODE)" }

# 4. Inno Setup compile installer
Write-Host "[4/4] Compiling installer with Inno Setup..."
& $IsccPath $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "Done. Installer located at:"
Get-ChildItem (Join-Path $root "artifacts") -Filter "DshDesktop-Setup-*.exe" | Select-Object FullName, @{n="SizeMB";e={[math]::Round($_.Length/1MB,1)}}
