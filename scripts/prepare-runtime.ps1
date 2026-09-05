# 准备捆绑运行时：只捆绑 Node 运行时 + npm 发行版 + pnpm + VC++ 运行库。
# 注意：dsh 不再捆绑，由应用首次启动时用自带的 node+npm 自动安装。
# 用法: powershell -ExecutionPolicy Bypass -File scripts/prepare-runtime.ps1
$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$desktopDir = Split-Path -Parent $scriptsDir
$rt = Join-Path $desktopDir "DshDesktop\runtime"
$logFile = Join-Path $desktopDir "prepare-runtime.log"

function Log($msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $msg
    Add-Content -Path $logFile -Value $line -Encoding utf8
    Write-Host $line
}

New-Item -ItemType Directory -Force -Path $rt | Out-Null
Set-Content -Path $logFile -Value "prepare-runtime start" -Encoding utf8

# 1. 安装 pnpm 到捆绑运行时（供 dsh plugin 在运行时使用）
Log "npm install pnpm -> runtime ..."
& cmd /c "npm install -g --prefix `"$rt`" --no-audit --no-fund pnpm" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Log "npm install pnpm FAILED ($LASTEXITCODE)"; exit 1 }
Log "pnpm install OK"

# 2. 拷贝 Node 运行时 + VC++ 运行库（使 node 可独立运行）
if (-not (Test-Path (Join-Path $rt "node.exe"))) {
    $nodeExe = Get-Command node -ErrorAction SilentlyContinue
    if ($nodeExe) { Copy-Item $nodeExe.Source (Join-Path $rt "node.exe") -Force; Log "node.exe copied from $($nodeExe.Source)" }
    else { Log "node.exe NOT FOUND on PATH"; exit 1 }
}
foreach ($d in @("vcruntime140.dll","vcruntime140_1.dll","msvcp140.dll")) {
    $src = Join-Path $env:WINDIR "System32\$d"
    if (Test-Path $src) { Copy-Item $src (Join-Path $rt $d) -Force; Log "copied $d" }
}

# 3. 捆绑 npm 发行目录（应用内自动安装/升级 dsh 依赖）
$nodeDir = Split-Path -Parent (Get-Command node -ErrorAction SilentlyContinue).Source
$npmSrc = Join-Path $nodeDir "node_modules\npm"
if (Test-Path (Join-Path $npmSrc "bin\npm-cli.js")) {
    robocopy $npmSrc (Join-Path $rt "node_modules\npm") /E /NFL /NDL /NJH /NJS /NP | Out-Null
    Log "npm bundled"
} else {
    Log "WARNING: npm distribution not found; in-app dsh auto-install unavailable"
}

# 4. 验证捆绑运行时
$node = Join-Path $rt "node.exe"
$ver = & $node --version
Log "node --version => $ver"
Log "runtime ready at $rt"
Write-Host "DONE"
