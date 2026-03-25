@echo off
echo 正在清理 Git 缓存中的 环境文件...
echo 只清理Git记录，不会删除你本地文件，放心使用

git rm -r --cached .vs/ 2>nul
git rm -r --cached bin/ 2>nul
git rm -r --cached obj/ 2>nul
git rm -r --cached packages/ 2>nul

echo.
echo ==============================
echo 清理完成！
echo 现在回到 GitHub Desktop 提交即可
echo 以后永远不会再出现 obj / bin / .vs 文件
echo ==============================
pause