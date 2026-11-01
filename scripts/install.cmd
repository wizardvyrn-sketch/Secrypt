@echo off
rem Install Secrypt (runs install.ps1 regardless of the PowerShell execution policy).
rem   scripts\install.cmd                   install
rem   scripts\install.cmd -DesktopShortcut  install + desktop shortcut
rem   scripts\install.cmd -Uninstall        uninstall (password data is kept)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
exit /b %ERRORLEVEL%
