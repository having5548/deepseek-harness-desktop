@echo off
rem 一键构建：准备运行时 -> dotnet publish -> Inno Setup 安装器
setlocal
set "ROOT=d:\software\deepseek-harness\desktop"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
set "ISCC=C:\Program Files\Inno Setup 7\ISCC.exe"
set "CSPROJ=%ROOT%\DshDesktop\DshDesktop.csproj"
set "PUBDIR=%ROOT%\artifacts\win-x64"
set "ISS=%ROOT%\installer\setup.iss"

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
