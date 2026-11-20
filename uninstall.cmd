@echo off
rem Stops UDriver and removes it from this computer. Leaves the Cloudflare tunnel and the packages in Unity projects in place.
rem Double-click this file. The PowerShell part below the line #ps runs from here.
set "UDRIVER_UNINSTALL=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$l = Get-Content -LiteralPath $env:UDRIVER_UNINSTALL; Invoke-Expression ($l[([array]::IndexOf($l, '#ps') + 1)..($l.Count - 1)] -join [char]10)"
pause
goto :eof
#ps
$U = Join-Path $env:USERPROFILE '.udriver'
$Port = $env:UDRIVER_HTTP_PORT
if (-not $Port -and (Test-Path -LiteralPath "$U\remote.env")) {
    foreach ($Line in Get-Content -LiteralPath "$U\remote.env") { if (-not $Port -and $Line -match '^UDRIVER_HTTP_PORT="?(\d+)"?$') { $Port = $Matches[1] } }
}
if (-not $Port) { $Port = '8787' }

# Only the UDriver panel listening on the port.
$Listeners = try { @(Get-NetTCPConnection -LocalPort ([int]$Port) -State Listen -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess -Unique) } catch { @() }
foreach ($P in $Listeners) {
    $Cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$P" -ErrorAction SilentlyContinue).CommandLine
    if ($Cmd -match 'udriver-mcp\.(dll|exe)') {
        Stop-Process -Id $P -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $P -Timeout 5 -ErrorAction SilentlyContinue
        Write-Host 'Stopped the UDriver panel.'
    }
}
# The UDriver app and its panel, on any port.
Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$env:LOCALAPPDATA\UDriver\*" } | Stop-Process -Force -ErrorAction SilentlyContinue
# Its tunnel: a panel stopped this way cannot close it.
Get-CimInstance Win32_Process -Filter "Name='cloudflared.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf("$U\", [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# Put back the original files of every game UDriver was added to; the backups stay if that fails.
$Restored = -not (Get-ChildItem -LiteralPath "$U\backups" -ErrorAction SilentlyContinue)
$Dll = "$U\server\udriver-mcp.dll"
foreach ($Exe in @("$env:LOCALAPPDATA\UDriver\current\udriver-mcp.exe", (Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$U\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")) {
    if ($Restored -or -not $Exe -or -not (Test-Path -LiteralPath $Exe)) { continue }
    if ($Exe -like '*udriver-mcp.exe') { & $Exe restore --all } elseif (Test-Path -LiteralPath $Dll) { & $Exe $Dll restore --all } else { continue }
    $Restored = $LASTEXITCODE -eq 0
}
if ($Restored -and (Test-Path -LiteralPath "$env:LOCALAPPDATA\UDriver\Update.exe")) {
    & "$env:LOCALAPPDATA\UDriver\Update.exe" --uninstall
    Write-Host 'Removed the UDriver app.'
}

Write-Host ''
Write-Host 'Agents that use UDriver on this computer were not stopped, so their work is not interrupted.'
Write-Host 'Remove UDriver from each agent''s MCP settings, then restart the agent.'

$Projects = if (Test-Path -LiteralPath "$U\targets.json") { try { @((Get-Content -LiteralPath "$U\targets.json" -Raw -Encoding UTF8 | ConvertFrom-Json).games | Where-Object kind -eq 'project') } catch { @() } } else { @() }
if ($Projects) {
    Write-Host ''
    Write-Host 'To remove UDriver from a Unity project, delete the folder Packages\com.udriver.agent in it. Projects you added:'
    foreach ($G in $Projects) { Write-Host "  $($G.path)" }
}

if (Test-Path -LiteralPath "$U\cloudflared.yml") {
    $Cert = "$U\cloudflare\.cloudflared\cert.pem"
    Write-Host ''
    Write-Host 'Remote access used a Cloudflare tunnel named udriver. To delete it, do it before deleting the folder below. Run in another window:'
    if (Test-Path -LiteralPath $Cert) { Write-Host "  cloudflared tunnel --origincert `"$Cert`" delete udriver" } else { Write-Host '  cloudflared tunnel delete udriver' }
    Write-Host 'or delete the tunnel udriver in the Cloudflare dashboard at any time.'
}

if (-not $Restored) {
    Write-Host ''
    Write-Host "Some games still have UDriver in them, so $U was kept: it holds their original files."
    Write-Host 'Close those games and run this again.'
} elseif (Test-Path -LiteralPath $U) {
    Write-Host ''
    Write-Host "$U holds your UDriver settings, the remote access setup and the built server."
    if ((Read-Host 'Delete it? This cannot be undone. Type Y and press Enter to delete, or just press Enter to keep it') -match '^\s*[yY]') {
        try { Remove-Item -LiteralPath $U -Recurse -Force -ErrorAction Stop; Write-Host 'Deleted.' }
        catch { Write-Host "Could not delete it: $($_.Exception.Message) Close the UDriver window and try again." }
    } else { Write-Host 'Kept.' }
}
