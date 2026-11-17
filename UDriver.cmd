@echo off
rem UDriver launcher for Windows: double-click this file. It runs UDriver.ps1 from the same folder
rem (Windows does not run a .ps1 on double-click, and blocks scripts by default).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0UDriver.ps1"
pause
