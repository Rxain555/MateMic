@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo  MicMate - build and run
echo ============================================

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet not found. Please install the .NET 9 SDK.
    pause
    exit /b 1
)

echo.
echo [1/2] Building...
rem -m:1 = serial build. In restricted environments (sandbox / no named-pipe permission)
rem MSBuild multi-node parallel build fails SILENTLY: it prints only "build failed /
rem 0 errors" with exit code 1, while the same solution builds fine serially.
dotnet build MicMate.sln -c Debug -m:1 --nologo
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. See the output above.
    pause
    exit /b 1
)

echo.
echo [2/2] Starting MicMate...
start "" "MicMate\bin\Debug\net9.0-windows\MicMate.exe"

echo.
echo Started. Closing the window minimizes it to the system tray.
timeout /t 3 >nul
endlocal
