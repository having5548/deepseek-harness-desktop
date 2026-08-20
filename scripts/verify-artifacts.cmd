@echo off
rem 验证发布目录中的捆绑运行时可用，并检查安装器时间戳
setlocal
set "RT=d:\software\deepseek-harness\desktop\artifacts\win-x64\runtime"
echo === node version ===
"%RT%\node.exe" --version
echo === dsh version ===
"%RT%\node.exe" "%RT%\node_modules\@deepseek-ai\dsh\lib\bin.js" --version
echo === setup exe (time) ===
dir "d:\software\deepseek-harness\desktop\artifacts\DshDesktop-Setup-*.exe"
echo === main exe (time) ===
dir "d:\software\deepseek-harness\desktop\artifacts\win-x64\DshDesktop.exe"
echo VERIFY_DONE
