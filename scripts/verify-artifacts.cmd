@echo off
rem 验证发布目录中的捆绑运行时可用，并检查安装器时间戳
setlocal
rem 从脚本位置推导仓库根目录（scripts\ 的上一级），支持任意工作区路径
set "ROOT=%~dp0.."
set "RT=%ROOT%\artifacts\win-x64\runtime"
echo === node version ===
"%RT%\node.exe" --version
echo === dsh version ===
"%RT%\node.exe" "%RT%\node_modules\@deepseek-ai\dsh\lib\bin.js" --version
echo === setup exe (time) ===
dir "%ROOT%\artifacts\DshDesktop-Setup-*.exe"
echo === main exe (time) ===
dir "%ROOT%\artifacts\win-x64\DshDesktop.exe"
echo VERIFY_DONE
