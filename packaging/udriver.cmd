@echo off
rem UDriver in the terminal. No arguments opens the full screen; otherwise: status, approve ^<id^>, deny ^<id^>, code, tools on^|off ^<tool^> [--for ^<app^>].
set "args=%*"
if "%~1"=="" set "args=tui"
if exist "%~dp0udriver-mcp.exe" ("%~dp0udriver-mcp.exe" %args%) else (dotnet "%USERPROFILE%\.udriver\server\udriver-mcp.dll" %args%)
