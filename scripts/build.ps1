# DeepSeek Harness 桌面客户端一键构建脚本
# 用法: powershell -ExecutionPolicy Bypass -File scripts/build.ps1
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$IsccPath = "C:\Program Files\Inno Setup 7\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root "DshDesktop\DshDesktop.csproj"
$publishDir = Join-Path $root "artifacts\$Runtime"
$iss = Join-Path $root "installer\setup.iss"

# 使用完整路径，避免 PATH 异常
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$cmd = "C:\Windows\System32\cmd.exe"

# 1. 图标缺失时生成
$icon = Join-Path $root "DshDesktop\Assets\AppIcon.ico"
if (-not (Test-Path $icon)) {
    Write-Host "[1/4] 生成应用图标…"
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root "scripts\generate-icon.ps1")
}

# 2. 准备捆绑运行时（node + dsh），实现打开即用
Write-Host "[2/4] 准备捆绑运行时（node + @deepseek-ai/dsh）…"
& $cmd /c (Join-Path $root "scripts\prepare-runtime.cmd")

# 3. 自包含发布（目标机器无需安装 .NET / Windows App SDK 运行时）
Write-Host "[3/4] dotnet publish ($Configuration / $Runtime, self-contained)…"
& $dotnet publish $csproj -c $Configuration -r $Runtime --self-contained true -p:PublishDir=$publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 (exit $LASTEXITCODE)" }

# 4. Inno Setup 编译安装器
if (-not (Test-Path $IsccPath)) { throw "未找到 ISCC.exe: $IsccPath" }
Write-Host "[4/4] Inno Setup 编译安装器…"
& $IsccPath $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败 (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "完成。安装包位于:"
Get-ChildItem (Join-Path $root "artifacts") -Filter "DshDesktop-Setup-*.exe" | Select-Object FullName, @{n="SizeMB";e={[math]::Round($_.Length/1MB,1)}}
