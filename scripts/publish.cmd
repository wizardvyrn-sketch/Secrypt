@echo off
rem Build the distribution folder + ZIP for other PCs (publish\Secrypt-<version>-win-x64).
rem   scripts\publish.cmd            build folder and ZIP
rem   scripts\publish.cmd -RunTests  run tests first
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
exit /b %ERRORLEVEL%
