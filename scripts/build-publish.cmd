@echo off
rem Publish + copy bundled runtime + build installer (assumes runtime is already prepared)
setlocal
set "ROOT=d:\software\deepseek-harness\desktop"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
set "ISCC=C:\Program Files\Inno Setup 7\ISCC.exe"
set "PUB=%ROOT%\artifacts\win-x64"

echo ===[1/3] dotnet publish===
"%DOTNET%" publish "%ROOT%\DshDesktop\DshDesktop.csproj" -c Release -r win-x64 --self-contained true -p:PublishDir="%PUB%"
if errorlevel 1 (echo PUBLISH_FAILED & exit /b 1)

echo ===[2/3] copy bundled runtime===
robocopy "%ROOT%\DshDesktop\runtime" "%PUB%\runtime" /E /NFL /NDL /NJH /NJS /NP
if errorlevel 8 (echo ROBOCOPY_FAILED & exit /b 1)

echo ===[3/3] Inno Setup===
"%ISCC%" "%ROOT%\installer\setup.iss"
if errorlevel 1 (echo ISCC_FAILED & exit /b 1)

echo PUBLISH_ISCC_DONE
