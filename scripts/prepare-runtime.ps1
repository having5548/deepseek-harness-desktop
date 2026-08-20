# 准备捆绑运行时：确保 dsh 安装完成，并拷贝 Node + VC++ 运行库。
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

# 1. 安装捆绑 dsh（幂等；npm 全局 prefix 安装）
$dshPkg = Join-Path $rt "node_modules\@deepseek-ai\dsh"
if (-not (Test-Path (Join-Path $dshPkg "lib\bin.js"))) {
    Log "npm install @deepseek-ai/dsh -> runtime ..."
    & cmd /c "npm install -g --prefix `"$rt`" --no-audit --no-fund @deepseek-ai/dsh" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Log "npm install FAILED ($LASTEXITCODE)"; exit 1 }
    Log "npm install OK"
} else {
    Log "dsh already installed, skip npm install"
}

# 2. 清理 npm 残留临时文件
Get-ChildItem $rt -Force -File | Where-Object { $_.Name -like ".dsh*" } | Remove-Item -Force -ErrorAction SilentlyContinue

# 3. 拷贝 Node 运行时 + VC++ 运行库（使 node 可独立运行）
if (-not (Test-Path (Join-Path $rt "node.exe"))) {
    $nodeExe = Get-Command node -ErrorAction SilentlyContinue
    if ($nodeExe) { Copy-Item $nodeExe.Source (Join-Path $rt "node.exe") -Force; Log "node.exe copied from $($nodeExe.Source)" }
    else { Log "node.exe NOT FOUND on PATH"; exit 1 }
}
foreach ($d in @("vcruntime140.dll","vcruntime140_1.dll","msvcp140.dll")) {
    $src = Join-Path $env:WINDIR "System32\$d"
    if (Test-Path $src) { Copy-Item $src (Join-Path $rt $d) -Force; Log "copied $d" }
}

# 4. 验证捆绑运行时
$node = Join-Path $rt "node.exe"
$binJs = Join-Path $dshPkg "lib\bin.js"
$ver = & $node --version
Log "node --version => $ver"
if (-not (Test-Path $binJs)) { Log "FAIL: bin.js missing"; exit 1 }
$dshVer = & $node $binJs --version
Log "dsh --version => $dshVer"
Log "runtime ready at $rt"
Write-Host "DONE"
