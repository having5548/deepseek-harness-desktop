@echo off
rem Prepare bundled runtime: install dsh (prod deps), copy node + VC++ runtime.
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

rem 1. reinstall bundled dsh with --omit=dev (prod deps only)
rem    allow-scripts: node-pty/koffi/subprocess helper are native deps needed at runtime
if not exist "%RT%" mkdir "%RT%"
if exist "%RT%\node_modules" rd /s /q "%RT%\node_modules"
cd /d "%RT%"
call npm install -g --prefix "%RT%" --omit=dev --no-audit --no-fund --allow-scripts=@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs @deepseek-ai/dsh
if errorlevel 1 (
  echo NPM_INSTALL_FAILED
  cd /d "%CUR%"
  exit /b 1
)
cd /d "%CUR%"

rem 2. install pnpm into the bundled runtime (needed by `dsh plugin`)
call npm install -g --prefix "%RT%" --no-audit --no-fund pnpm
if errorlevel 1 (
  echo PNPM_INSTALL_FAILED
  cd /d "%CUR%"
  exit /b 1
)

rem 3. copy node runtime + VC++ runtime libs
if not exist "%RT%\node.exe" copy /Y "%NODE%" "%RT%\node.exe" >nul
if not exist "%RT%\vcruntime140.dll" copy /Y "%WINDIR%\System32\vcruntime140.dll" "%RT%\vcruntime140.dll" >nul 2>nul
if not exist "%RT%\vcruntime140_1.dll" copy /Y "%WINDIR%\System32\vcruntime140_1.dll" "%RT%\vcruntime140_1.dll" >nul 2>nul
if not exist "%RT%\msvcp140.dll" copy /Y "%WINDIR%\System32\msvcp140.dll" "%RT%\msvcp140.dll" >nul 2>nul

rem 4. bundle npm distribution into the runtime (enables in-app dsh self-upgrade
rem    without requiring npm installed on the target machine)
set "NPM_SRC=%NODE_DIR%node_modules\npm"
if exist "%NPM_SRC%\bin\npm-cli.js" (
  robocopy "%NPM_SRC%" "%RT%\node_modules\npm" /E /NFL /NDL /NJH /NJS /NP >nul
  echo npm bundled: "%RT%\node_modules\npm\bin\npm-cli.js"
) else (
  echo WARNING: npm distribution not found at "%NPM_SRC%"; in-app dsh upgrade will be unavailable
)

echo === node version ===
"%RT%\node.exe" --version
echo === dsh version ===
"%RT%\node.exe" "%RT%\node_modules\@deepseek-ai\dsh\lib\bin.js" --version
echo PREPARE_DONE

