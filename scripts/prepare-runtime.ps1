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
#    固定 pnpm 11.x：pnpm 12+ 自带 "pn" 运行时，会在包内塞入 8 份各约 42MB 的重复二进制
#    （pn / pn.exe / pnpm / pnpm.exe / pnpx / pnpx.exe / pnx / pnx.exe），
#    使捆绑运行时从约 19MB 膨胀到约 398MB、安装包凭空多出约 105MB。
#    11.x 仅约 19MB，且为 dsh plugin 实测可用的版本。
#    先清掉旧版本残留（含其 bin shim），避免降级后留下孤儿文件。
$pnpmDir = Join-Path $rt "node_modules\pnpm"
if (Test-Path $pnpmDir) { Remove-Item $pnpmDir -Recurse -Force; Log "removed existing pnpm (clean install)" }
foreach ($shim in @("pn","pn.cmd","pn.ps1","pnx","pnx.cmd","pnx.ps1","pnpx","pnpx.cmd","pnpx.ps1")) {
    $p = Join-Path $rt $shim
    if (Test-Path $p) { Remove-Item $p -Force }
}
Log "npm install pnpm@11 -> runtime ..."
& cmd /c "npm install -g --prefix `"$rt`" --no-audit --no-fund pnpm@11" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Log "npm install pnpm FAILED ($LASTEXITCODE)"; exit 1 }
$pnpmV = (& cmd /c "`"$rt\pnpm.cmd`" --version" 2>&1 | Select-Object -First 1)
Log "pnpm install OK (version $pnpmV)"

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
