@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================
echo  MicMate 一键构建并启动
echo ============================================

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet 命令。请先安装 .NET 9 SDK。
    pause
    exit /b 1
)

echo.
echo [1/2] 正在构建...
dotnet build MicMate.sln -c Debug --nologo
if errorlevel 1 (
    echo.
    echo [错误] 构建失败，请查看上方输出。
    pause
    exit /b 1
)

echo.
echo [2/2] 正在启动 MicMate...
start "" "MicMate\bin\Debug\net9.0-windows\MicMate.exe"

echo.
echo 已启动。窗口关闭后会最小化到系统托盘。
timeout /t 3 >nul
endlocal
