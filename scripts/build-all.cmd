@echo off
rem 一键全流程构建（Windows，Rust/Tauri 版）：
rem   prepare-runtime（捆绑 node/npm/pnpm）→ tauri build（不打包，出裸 exe）→ Inno Setup 安装器
setlocal
cd /d "%~dp0.."

echo [1/4] 准备捆绑运行时 ...
powershell -ExecutionPolicy Bypass -File scripts\prepare-runtime.ps1 || goto :fail

echo [2/4] 编译发布版（tauri build，跳过自带 bundler）...
call tauri build --no-bundle || goto :fail
if not exist "src-tauri\target\release\DshDesktop.exe" (
    echo ERROR: 未找到 src-tauri\target\release\DshDesktop.exe
    goto :fail
)

echo [3/4] 生成 Inno Setup 安装器 ...
set "ISCC="
for %%P in (
    "C:\Program Files\Inno Setup 7\ISCC.exe"
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) do (
    if not defined ISCC if exist "%%~P" set "ISCC=%%~P"
)
where iscc >nul 2>&1 && set "ISCC=iscc"
if not defined ISCC (
    echo ERROR: 找不到 Inno Setup（ISCC.exe）。请安装 Inno Setup 7 或加入 PATH。
    goto :fail
)
mkdir artifacts 2>nul
"%ISCC%" installer\setup.iss || goto :fail

echo [4/4] 验证产物 ...
dir /b artifacts\DshDesktop-Setup-*-rust.exe
echo.
echo DONE: artifacts\DshDesktop-Setup-*-rust.exe 与 src-tauri\target\release\DshDesktop.exe
exit /b 0

:fail
echo BUILD FAILED
exit /b 1
