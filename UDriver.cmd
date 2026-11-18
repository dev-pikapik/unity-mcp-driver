@echo off
rem UDriver launcher for Windows: runs UDriver.ps1, since Windows does not run a .ps1 on double-click.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0UDriver.ps1"
pause
