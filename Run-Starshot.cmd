@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Starshot.ps1" %*
set "result=%errorlevel%"
if not "%result%"=="0" (
  echo.
  echo Starshot did not start. See the message above.
  pause
)
exit /b %result%
