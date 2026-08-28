@echo off
REM build.cmd - double-click to build DSH-Launcher (delegates to build.ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if errorlevel 1 (
  echo.
  echo Build FAILED - see the error above.
)
pause
