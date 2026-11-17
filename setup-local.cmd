@echo off
setlocal EnableExtensions
rem Builds the UDriver MCP server and writes a ready-to-paste config block to mcp-config.json
cd /d "%~dp0."

rem Game build only (no Unity project): pass the build folder or .exe, e.g.  setup-local.cmd "C:\Games\MyGame"  (or set UDRIVER_BUILD)
set "BUILD=%~1"
if not defined BUILD set "BUILD=%UDRIVER_BUILD%"
if not defined BUILD goto :no_build

if "%BUILD:~-1%"=="\" set "BUILD=%BUILD:~0,-1%"
if exist "%BUILD%" goto :resolve_build
echo Build not found: "%BUILD%"
pause
exit /b 1

:resolve_build
for %%I in ("%BUILD%") do if exist "%%~fI\" (set "BUILD=%%~fI") else (set "BUILD=%%~dpI")
if "%BUILD:~-1%"=="\" set "BUILD=%BUILD:~0,-1%"

:no_build
dotnet build UDriver.Mcp -c Release
if not errorlevel 1 goto :build_ok
echo.
echo BUILD FAILED. Is the .NET 8 SDK installed? Check with: dotnet --version
pause
exit /b 1

:build_ok
if not exist "%~dp0Builds" mkdir "%~dp0Builds"
set "DLL=%~dp0UDriver.Mcp\bin\Release\net8.0\udriver-mcp.dll"
set "DLL=%DLL:\=/%"
set "BLD=%~dp0Builds"
set "BLD=%BLD:\=/%"
if not defined BUILD goto :write_config
set "BLD=%BLD%;%BUILD:\=/%"

:write_config
set "OUT=%~dp0mcp-config.json"
> "%OUT%" echo {
>> "%OUT%" echo   "mcpServers": {
>> "%OUT%" echo     "udriver": {
>> "%OUT%" echo       "command": "dotnet",
>> "%OUT%" echo       "args": ["%DLL%"],
>> "%OUT%" echo       "env": { "UDRIVER_BUILDS_DIR": "%BLD%" }
>> "%OUT%" echo     }
>> "%OUT%" echo   }
>> "%OUT%" echo }

rem Launcher with the settings baked in: lets another computer start this server over SSH.
set "RUN=%~dp0run-server.cmd"
> "%RUN%" echo @echo off
>> "%RUN%" echo set "UDRIVER_BUILDS_DIR=%BLD%"
>> "%RUN%" echo dotnet "%DLL%"

echo.
echo DONE.
echo.
echo === Claude Code: open a terminal in your Unity project folder (any folder for a game build) and run this one command ===
echo.
echo claude mcp add udriver -e UDRIVER_BUILDS_DIR="%BLD%" -- dotnet "%DLL%"
echo.
echo === Claude Code on ANOTHER computer: run this there in a terminal or PowerShell (replace USER and THIS-PC; SSH key login required) ===
echo.
echo claude mcp add udriver -- ssh -o BatchMode=yes USER@THIS-PC '"%RUN%"'
echo.
echo === Other agents: paste this block into their MCP settings (also saved in %OUT%) ===
echo.
type "%OUT%"
echo.
if not defined BUILD goto :finish
echo Then ask Claude to start the game with app_launch and the .exe inside "%BUILD%", or start it yourself and ask Claude to use app_connect.
echo.

:finish
pause
