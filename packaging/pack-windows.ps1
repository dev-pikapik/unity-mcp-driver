# Builds the Windows installer (UDriver-win-Setup.exe) and its update files into packaging\releases.
# Usage: powershell -File packaging\pack-windows.ps1 1.0.0   (-Runtime win-arm64 for ARM computers)
param([Parameter(Mandatory)][string]$Version, [string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$out = "$PSScriptRoot\build\windows"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
dotnet publish "$PSScriptRoot\..\UDriver.Mcp" -c Release -r $Runtime --self-contained -p:Version=$Version -o $out -v q -nologo
if ($LASTEXITCODE) { exit 1 }
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
dotnet publish "$PSScriptRoot\..\apps\windows" -c Release -r $Runtime -p:Platform=$platform -p:Version=$Version -o $out -v q -nologo
if ($LASTEXITCODE) { exit 1 }
Copy-Item "$PSScriptRoot\udriver.cmd" $out
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) { dotnet tool install -g vpk }
vpk pack -u UDriver -v $Version -p $out -e UDriver.exe -r $Runtime --packTitle UDriver -i "$PSScriptRoot\udriver.ico" --shortcuts StartMenuRoot -o "$PSScriptRoot\releases"
