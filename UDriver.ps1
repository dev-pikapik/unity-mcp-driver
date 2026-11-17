# UDriver launcher for Windows. Start it by double-clicking UDriver.cmd, which runs this script.
# Remote mode runs the server only while this window is open. To stop: the "Stop everything" button in the panel, or close the window.
# Nothing is added to startup items.
$Dir = $PSScriptRoot
$U = Join-Path $env:USERPROFILE '.udriver'
$Port = 8787
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
# UDriver.cmd pauses after this script ends, so messages stay readable.
function Fail($msg) { Write-Host "`nERROR: $msg`n"; exit 1 }

New-Item -ItemType Directory -Force -Path "$U\bin", "$U\logs" | Out-Null
# Only you may open this folder (the Windows counterpart of chmod 700): inherited entries are removed and files created inside inherit this rule.
$Me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $U /inheritance:r /grant:r "*${Me}:(OI)(CI)F" /q | Out-Null
if ($LASTEXITCODE) { Fail "could not restrict access to $U" }

Write-Host 'UDriver for Windows'
Write-Host ''
Write-Host '  1  Claude Code on this computer (simplest: nothing is reachable from the internet)'
Write-Host '  2  Remote access through your Cloudflare domain, with a control panel (Claude app, claude.ai, Gemini)'
Write-Host ''
$Mode = (Read-Host 'Type 1 or 2 and press Enter').Trim()
if ($Mode -ne '1' -and $Mode -ne '2') { Fail 'please type 1 or 2' }

function Stop-Tunnel {
    Get-CimInstance Win32_Process -Filter "Name = 'cloudflared.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '* run udriver*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

if ($Mode -eq '2') {
    # Keep a private copy of the Unity package under .udriver, so setup still works if this folder is moved or deleted later.
    if (Test-Path -LiteralPath "$Dir\com.udriver.agent") {
        try {
            if (Test-Path -LiteralPath "$U\package") { Remove-Item -LiteralPath "$U\package" -Recurse -Force -ErrorAction Stop }
            New-Item -ItemType Directory -Force -Path "$U\package" -ErrorAction Stop | Out-Null
            Copy-Item -LiteralPath "$Dir\com.udriver.agent" -Destination "$U\package" -Recurse -Force -ErrorAction Stop
        } catch { Fail "could not copy the Unity package from $Dir" }
    }
    # Already running? Stop only the panel server listening on $Port: other udriver-mcp.dll processes are agents' servers and must keep running.
    $old = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue } | Where-Object { $_.ProcessName -eq 'dotnet' }
    if ($old) {
        Write-Host 'UDriver was already running. Restarting it with the files from this folder.'
        $old | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Stop-Tunnel
}

Write-Host 'Checking required programs...'
# .NET 8 or newer SDK (the server targets .NET 8 and runs on newer runtimes too).
function Find-Dotnet {
    foreach ($d in @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$U\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")) {
        if ($d -and (Test-Path -LiteralPath $d) -and (& $d --list-sdks 2>$null | Where-Object { [int](($_ -split '\.')[0]) -ge 8 })) { return $d }
    }
}
$Dotnet = Find-Dotnet
if (-not $Dotnet) {
    Write-Host "Installing .NET into $U\dotnet (no admin password needed, takes a couple of minutes)..."
    try { Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile "$U\dotnet-install.ps1" -ErrorAction Stop }
    catch { Fail 'could not download the .NET installer (no internet?)' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$U\dotnet-install.ps1" -Channel 8.0 -InstallDir "$U\dotnet" *> "$U\logs\dotnet-install.log"
    $Dotnet = Find-Dotnet
    if (-not $Dotnet) { Fail ".NET installation failed, details in $U\logs\dotnet-install.log" }
}
if ($Dotnet -eq "$U\dotnet\dotnet.exe") { $env:DOTNET_ROOT = "$U\dotnet" }

# Build the server if it is missing or the sources are newer.
$Dll = "$U\server\udriver-mcp.dll"
$Hash = (Get-ChildItem -LiteralPath "$Dir\UDriver.Mcp" -File | Where-Object { $_.Extension -in '.cs', '.csproj' } | Sort-Object Name |
    Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash }) -join ''
$Built = if (Test-Path -LiteralPath "$U\server\.source-hash") { (Get-Content -LiteralPath "$U\server\.source-hash" -Raw).Trim() } else { '' }
if (-not (Test-Path -LiteralPath $Dll) -or $Built -ne $Hash) {
    Write-Host 'Building the server (about a minute)...'
    & $Dotnet publish "$Dir\UDriver.Mcp" -c Release -o "$U\server" -v q *> "$U\logs\build.log"
    if ($LASTEXITCODE) {
        Get-Content -LiteralPath "$U\logs\build.log" -Tail 15
        Fail "server build failed, full log in $U\logs\build.log. If Claude Code or another UDriver window is open, close it and try again."
    }
    Set-Content -LiteralPath "$U\server\.source-hash" -Value $Hash -NoNewline
}

if ($Mode -eq '1') {
    Write-Host ''
    $Pick = (Read-Host 'Drag your Unity project folder (or, for a game build only, the game .exe) into this window and press Enter').Trim().Trim('"').TrimEnd('\')
    if (-not $Pick) { Fail 'nothing was given' }
    $Dirs = @("$env:USERPROFILE\UDriverBuilds")
    New-Item -ItemType Directory -Force -Path $Dirs[0] | Out-Null
    $Build = ''
    if ((Test-Path -LiteralPath "$Pick\Assets" -PathType Container) -and (Test-Path -LiteralPath "$Pick\Packages" -PathType Container)) {
        # Replaced as a whole: Copy-Item into an existing folder would nest a second com.udriver.agent inside it.
        $Pkg = "$Pick\Packages\com.udriver.agent"
        try {
            if (Test-Path -LiteralPath $Pkg) { Remove-Item -LiteralPath $Pkg -Recurse -Force -ErrorAction Stop }
            Copy-Item -LiteralPath "$Dir\com.udriver.agent" -Destination $Pkg -Recurse -Force -ErrorAction Stop
        } catch { Fail "could not copy the Unity package into $Pick\Packages" }
        Write-Host "Package copied into $Pick\Packages"
    } else {
        $Build = $Pick
        if (Test-Path -LiteralPath $Build -PathType Leaf) { $Build = [IO.Path]::GetDirectoryName($Build) }
        if (-not (Test-Path -LiteralPath $Build -PathType Container)) { Fail "build not found: $Build" }
        $Build = (Resolve-Path -LiteralPath $Build).ProviderPath.TrimEnd('\')
        if ($Build.Contains(';')) { Fail "the build path cannot contain ';'. Move or rename the build folder." }
        if (-not (Get-ChildItem -LiteralPath $Build -Directory -Filter '*_Data')) { Fail "$Build is neither a Unity project (no Assets and Packages folders) nor a Unity game build (no <Name>_Data folder)" }
        $Dirs += $Build
    }
    $Envs = @('-e', "UDRIVER_BUILDS_DIR=$($Dirs -join ';')")
    if ($env:DOTNET_ROOT) { $Envs += '-e', "DOTNET_ROOT=$env:DOTNET_ROOT" }
    Write-Host ''
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        & claude mcp remove udriver -s user *> $null
        # '--' is quoted so PowerShell passes it on instead of treating it as its own end-of-parameters marker.
        & claude mcp add udriver -s user @Envs '--' $Dotnet $Dll
        if ($LASTEXITCODE -eq 0) {
            Write-Host ''
            Write-Host 'DONE. UDriver is added to Claude Code for all your folders. Start (or restart) Claude Code and ask it to run self_test.'
            if ($Build) { Write-Host "Then ask Claude to start the game with app_launch (path: $Build), or start it yourself and ask Claude to use app_connect." }
            exit 0
        }
        Write-Host 'Claude Code could not add UDriver automatically.'
    } else {
        Write-Host 'Claude Code was not found on this computer.'
    }
    Write-Host 'Run this command in a terminal once Claude Code is installed:'
    Write-Host ''
    $q = { param($a) if ($a -match ' ') { "`"$a`"" } else { $a } }
    Write-Host ("claude mcp add udriver -s user " + (($Envs | ForEach-Object { & $q $_ }) -join ' ') + " -- " + (& $q $Dotnet) + " " + (& $q $Dll))
    Write-Host ''
    Write-Host 'For other agents, run setup-local.cmd: it prints a ready-to-paste config block.'
    exit 0
}

# Remote mode: cloudflared from PATH, from .udriver\bin, from where winget installs it, or the path in UDRIVER_CLOUDFLARED.
function Find-Cloudflared {
    @($env:UDRIVER_CLOUDFLARED, (Get-Command cloudflared -ErrorAction SilentlyContinue).Source, "$U\bin\cloudflared.exe",
      "${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe", "$env:ProgramFiles\cloudflared\cloudflared.exe",
      "$env:LOCALAPPDATA\Microsoft\WinGet\Links\cloudflared.exe") | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
$Cf = Find-Cloudflared
if (-not $Cf -and (Get-Command winget -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing cloudflared with winget (Windows may ask for permission)...'
    winget install --id Cloudflare.cloudflared -e --accept-source-agreements --accept-package-agreements
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    $Cf = Find-Cloudflared
}
if (-not $Cf) { Fail "cloudflared was not found. Install it with:  winget install --id Cloudflare.cloudflared   (or put cloudflared.exe into $U\bin), then start UDriver.cmd again." }

Write-Host ''
$Open = '1'
do {
    # Settings are read as plain KEY="value" lines (never executed as code); only UDRIVER_* names are taken.
    if (Test-Path -LiteralPath "$U\remote.env") {
        foreach ($l in Get-Content -LiteralPath "$U\remote.env") {
            if ($l -cnotmatch '^(UDRIVER_[A-Z0-9_]*)=(.*)$') { continue }
            $k = $Matches[1]; $v = $Matches[2]
            if ($v.Length -ge 2 -and $v.StartsWith('"') -and $v.EndsWith('"')) { $v = $v.Substring(1, $v.Length - 2) }
            [Environment]::SetEnvironmentVariable($k, $v)
        }
    }
    $env:UDRIVER_HTTP_PORT = "$Port"; $env:UDRIVER_PANEL = '1'; $env:UDRIVER_DIST = "$U\package"
    $env:UDRIVER_CLOUDFLARED = $Cf; $env:UDRIVER_OPEN_PANEL = $Open
    & $Dotnet $Dll
    $Code = $LASTEXITCODE
    $Open = '0'
} while ($Code -eq 3)   # 3 = setup finished, restart with the new settings
Stop-Tunnel
Write-Host ''
Write-Host 'UDriver stopped. You can close this window.'
