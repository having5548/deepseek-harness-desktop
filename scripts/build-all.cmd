@echo off
rem 一键构建：准备运行时 -> dotnet publish -> Inno Setup 安装器
setlocal EnableDelayedExpansion
rem 从脚本位置推导仓库根目录（scripts\ 的上一级），支持任意工作区路径
set "ROOT=%~dp0.."
set "CSPROJ=%ROOT%\DshDesktop\DshDesktop.csproj"
set "PUBDIR=%ROOT%\artifacts\win-x64"
set "ISS=%ROOT%\installer\setup.iss"

rem 通过 PATH 解析工具（不写死安装路径）；ISCC 默认不在 PATH，做常见目录兜底探测
call :find_tool dotnet DOTNET || exit /b 1
call :find_tool iscc ISCC || exit /b 1

echo ===[1/3] prepare runtime===
call "%ROOT%\scripts\prepare-runtime.cmd"
if errorlevel 1 (echo PREPARE_FAILED & exit /b 1)

echo ===[2/3] dotnet publish===
"%DOTNET%" publish "%CSPROJ%" -c Release -r win-x64 --self-contained true -p:PublishDir="%PUBDIR%"
if errorlevel 1 (echo PUBLISH_FAILED & exit /b 1)

echo ===[2b/3] copy bundled runtime into publish dir===
robocopy "%ROOT%\DshDesktop\runtime" "%PUBDIR%\runtime" /E /NFL /NDL /NJH /NJS /NP
if errorlevel 8 (echo ROBOCOPY_FAILED & exit /b 1)

echo ===[3/3] Inno Setup===
"%ISCC%" "%ISS%"
if errorlevel 1 (echo ISCC_FAILED & exit /b 1)

echo ALL_DONE
exit /b 0

:find_tool
rem %1=命令名  %2=返回变量名；先在 PATH 找，找不到再探测常见安装目录
set "%~2="
for /f "delims=" %%i in ('where %1 2^>nul') do if not defined %~2 set "%~2=%%i"
if defined %~2 (echo %1: !%~2! & exit /b 0)
if /i "%1"=="iscc" (
  for %%p in ("C:\Program Files\Inno Setup 7\ISCC.exe" "C:\Program Files (x86)\Inno Setup 7\ISCC.exe" "C:\Program Files\Inno Setup 6\ISCC.exe") do (
    if not defined %~2 if exist %%p set "%~2=%%~p"
  )
)
if defined %~2 (echo %1: !%~2! & exit /b 0)
echo %1_NOT_FOUND: 未在 PATH 中找到 %1，请安装并加入 PATH
exit /b 1
