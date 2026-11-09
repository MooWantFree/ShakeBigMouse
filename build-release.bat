@echo off
echo Closing ShakeBigMouse app...
taskkill /IM ShakeBigMouse.exe /F 2>nul
taskkill /IM ShakeToFindCursor.exe /F 2>nul

echo Waiting for app to close...
timeout /t 2 /nobreak >nul

echo Publishing both Windows ZIPs...
cd /d "%~dp0"
pwsh -NoProfile -Command "$ErrorActionPreference = 'Stop'; .\scripts\Publish-Windows.ps1; Expand-Archive -LiteralPath '.\dist\windows\ShakeBigMouse-win-x64-framework-dependent.zip' -DestinationPath '.\dist' -Force"
if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

echo.
echo Done! Run the app here:
echo %~dp0dist\ShakeBigMouse.exe
echo ZIP packages: %~dp0dist\windows
echo.
pause
