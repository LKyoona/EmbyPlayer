@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-win-x64.ps1"
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Please review the error above.
) else (
  echo.
  echo BUILD SUCCEEDED. Open publish\win-x64\EmbyRemotePlayer.exe
)
pause
