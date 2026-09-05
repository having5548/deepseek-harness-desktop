@echo off
rem 准备捆绑运行时：只捆绑 Node 运行时 + npm 发行版 + pnpm + VC++ 运行库。
rem 注意：dsh 不再捆绑进安装包，由应用首次启动时用自带的 node+npm 自动安装
rem       （见 DshDesktop/Services/DshPaths.cs 与 DshUpdater）。
setlocal EnableDelayedExpansion
rem 从脚本位置推导仓库根目录（scripts\ 的上一级），支持任意工作区路径
set "RT=%~dp0..\DshDesktop\runtime"

rem 通过 PATH 定位 node（不写死安装路径）
set "NODE="
for /f "delims=" %%i in ('where node 2^>nul') do if not defined NODE set "NODE=%%i"
if not defined NODE (
  echo NODE_NOT_FOUND: 未在 PATH 中找到 node，请安装 Node.js 并加入 PATH
  exit /b 1
)
echo Using node: %NODE%
rem 由 node.exe 路径推导其安装目录（含 npm 发行目录）
for %%i in ("%NODE%") do set "NODE_DIR=%%~dpi"
set "CUR=%CD%"

rem 1. 清理旧版捆绑的 dsh 残留（历史版本曾把 dsh 装到 runtime；新架构由应用首次启动自动安装）
if exist "%RT%\node_modules\@deepseek-ai" (
  rd /s /q "%RT%\node_modules\@deepseek-ai"
  echo removed stale bundled dsh: "%RT%\node_modules\@deepseek-ai"
)
rem    清理旧版 dsh 的 bin shim（npm install -g --prefix 曾在此目录生成 dsh/dsh.cmd/dsh.ps1）
for %%f in ("%RT%\dsh" "%RT%\dsh.cmd" "%RT%\dsh.ps1") do (
  if exist %%f (
    del /q %%f
    echo removed stale dsh shim: %%f
  )
)

rem 2. 安装 pnpm 到捆绑运行时（供 dsh plugin 在运行时使用）
if not exist "%RT%" mkdir "%RT%"
cd /d "%RT%"
call npm install -g --prefix "%RT%" --no-audit --no-fund pnpm
if errorlevel 1 (
  echo PNPM_INSTALL_FAILED
  cd /d "%CUR%"
  exit /b 1
)
cd /d "%CUR%"

rem 3. copy node runtime + VC++ runtime libs
if not exist "%RT%\node.exe" copy /Y "%NODE%" "%RT%\node.exe" >nul
if not exist "%RT%\vcruntime140.dll" copy /Y "%WINDIR%\System32\vcruntime140.dll" "%RT%\vcruntime140.dll" >nul 2>nul
if not exist "%RT%\vcruntime140_1.dll" copy /Y "%WINDIR%\System32\vcruntime140_1.dll" "%RT%\vcruntime140_1.dll" >nul 2>nul
if not exist "%RT%\msvcp140.dll" copy /Y "%WINDIR%\System32\msvcp140.dll" "%RT%\msvcp140.dll" >nul 2>nul

rem 4. bundle npm distribution into the runtime (enables in-app dsh auto-install / upgrade
rem    without requiring npm installed on the target machine)
set "NPM_SRC=%NODE_DIR%node_modules\npm"
if exist "%NPM_SRC%\bin\npm-cli.js" (
  robocopy "%NPM_SRC%" "%RT%\node_modules\npm" /E /NFL /NDL /NJH /NJS /NP >nul
  echo npm bundled: "%RT%\node_modules\npm\bin\npm-cli.js"
) else (
  echo WARNING: npm distribution not found at "%NPM_SRC%"; in-app dsh auto-install will be unavailable
)

echo === node version ===
"%RT%\node.exe" --version
echo PREPARE_DONE

