@echo off
rem Install Secrypt from this folder (no build, no .NET needed).
rem   setup.cmd                              install
rem   setup.cmd -DesktopShortcut -AddToPath  install + desktop shortcut + pwm on PATH
rem   setup.cmd -Uninstall                   uninstall (password data is kept)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set rc=%ERRORLEVEL%
echo.
pause
exit /b %rc%
