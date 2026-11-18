# UDriver launcher for Windows, started by UDriver.cmd.
$Dir = $PSScriptRoot
$U = Join-Path $env:USERPROFILE '.udriver'
# Settings kept in remote.env by earlier versions; read as literal text, a variable already set wins.
$OldEnv = if (Test-Path -LiteralPath "$U\remote.env") { Get-Content -LiteralPath "$U\remote.env" } else { @() }
foreach ($K in 'UDRIVER_HTTP_PORT', 'UDRIVER_BUILDS_DIR', 'UDRIVER_DISABLE_TOOLS', 'UDRIVER_READONLY', 'UDRIVER_OAUTH_REDIRECT_HOSTS') {
    $Line = $OldEnv | Where-Object { $_.StartsWith("$K=") } | Select-Object -First 1
    if (-not $Line) { continue }
    $V = $Line.Substring($K.Length + 1)
    if ($V.Length -ge 2 -and $V.StartsWith('"') -and $V.EndsWith('"')) { $V = $V.Substring(1, $V.Length - 2) }
    if ($V -and -not [Environment]::GetEnvironmentVariable($K)) { [Environment]::SetEnvironmentVariable($K, $V) }
}
$Port = if ($env:UDRIVER_HTTP_PORT) { $env:UDRIVER_HTTP_PORT } else { '8787' }
$Panel = "http://127.0.0.1:$Port/panel"
$ProgressPreference = 'SilentlyContinue'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
# UDriver.cmd pauses after this script ends, so messages stay readable.
function Fail($msg) { Write-Host "`n$msg`n"; exit 1 }

New-Item -ItemType Directory -Force -Path "$U\logs" | Out-Null
# Only you may open this folder (the Windows counterpart of chmod 700).
$Me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $U /inheritance:r /grant:r "*${Me}:(OI)(CI)F" /q | Out-Null
if ($LASTEXITCODE) { Fail "Could not restrict access to $U" }

Write-Host 'UDriver'
Write-Host ''
Write-Host '  1  Set up in the browser (recommended)'
Write-Host '  2  Set up here in this window'
Write-Host ''
$Mode = (Read-Host 'Type 1 or 2 and press Enter [1]').Trim()
if (-not $Mode) { $Mode = '1' }
if ($Mode -ne '1' -and $Mode -ne '2') { Fail 'Please type 1 or 2. Start UDriver again to retry.' }

if ($Mode -eq '1') {
    # The panel key goes only to our own server (the process on the port is the one in panel.pid), and only in a request body.
    $Listener = try { (Get-NetTCPConnection -LocalPort ([int]$Port) -State Listen -ErrorAction Stop | Select-Object -First 1).OwningProcess } catch { $null }
    $Own = if (Test-Path -LiteralPath "$U\panel.pid") { (Get-Content -LiteralPath "$U\panel.pid" -Raw).Trim() } else { '' }
    $Ticket = if ($Listener -and "$Listener" -eq $Own) { try { (Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$Panel/ticket" -InFile "$U\panel.key" -TimeoutSec 2).Content.Trim() } catch { '' } } else { '' }
    if ($Ticket) {
        Start-Process "${Panel}?ticket=$Ticket"
        Write-Host ''
        Write-Host "UDriver is already running. Its panel is open in your browser: $Panel"
        Write-Host 'To start a newer version, press Stop in the panel, then start UDriver again.'
        exit 0
    }
    $Busy = try { $Tcp = [Net.Sockets.TcpClient]::new(); $Tcp.ConnectAsync('127.0.0.1', [int]$Port).Wait(2000) } catch { $false } finally { if ($Tcp) { $Tcp.Dispose() } }
    if ($Busy) { Fail "Another program already uses port $Port, most likely an older version of UDriver. Stop it first (close the window it runs in; to see what it is: netstat -ano | findstr :$Port), then start UDriver again." }
}

# Private copy of the Unity package, so setup works if this folder moves.
if (Test-Path -LiteralPath "$Dir\com.udriver.agent") {
    try {
        if (Test-Path -LiteralPath "$U\package") { Remove-Item -LiteralPath "$U\package" -Recurse -Force -ErrorAction Stop }
        New-Item -ItemType Directory -Force -Path "$U\package" -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath "$Dir\com.udriver.agent" -Destination "$U\package" -Recurse -Force -ErrorAction Stop
    } catch { Fail "Could not copy the Unity package from $Dir" }
}

# .NET 8 or newer SDK (the server targets .NET 8 and runs on newer runtimes too).
function Find-Dotnet {
    foreach ($d in @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, "$U\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")) {
        if ($d -and (Test-Path -LiteralPath $d) -and (& $d --list-sdks 2>$null | Where-Object { [int](($_ -split '\.')[0]) -ge 8 })) { return $d }
    }
}
$Dotnet = Find-Dotnet
if (-not $Dotnet) {
    Write-Host ''
    Write-Host 'UDriver needs the .NET 8 SDK, a free program from Microsoft.'
    if ((Get-Command winget -ErrorAction SilentlyContinue) -and (Read-Host 'Install it now with winget? Windows may ask for permission. [Y/n]') -notmatch '^[nN]') {
        winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        $Dotnet = Find-Dotnet
    }
    if (-not $Dotnet) {
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/8.0'
        Fail 'Install the .NET 8 SDK from the Microsoft page that just opened, then start UDriver again.'
    }
}

# Build the server if it is missing or the sources are newer.
$Dll = "$U\server\udriver-mcp.dll"
$Sources = @(Get-ChildItem -LiteralPath "$Dir\UDriver.Mcp" -File | Where-Object { $_.Extension -in '.cs', '.csproj' } | Sort-Object Name) +
    @(Get-Item -LiteralPath "$Dir\com.udriver.agent\Runtime\ToolManifest.cs")
$Hash = ($Sources | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash }) -join ''
$Built = if (Test-Path -LiteralPath "$U\server\.source-hash") { (Get-Content -LiteralPath "$U\server\.source-hash" -Raw).Trim() } else { '' }
if (-not (Test-Path -LiteralPath $Dll) -or $Built -ne $Hash) {
    Write-Host 'Preparing UDriver (about a minute the first time)...'
    & $Dotnet publish "$Dir\UDriver.Mcp" -c Release -o "$U\server" -v q *> "$U\logs\build.log"
    if ($LASTEXITCODE) {
        Get-Content -LiteralPath "$U\logs\build.log" -Tail 15
        Fail "UDriver could not be built. The full log is in $U\logs\build.log. If an agent that uses UDriver is open, close it and try again."
    }
    Set-Content -LiteralPath "$U\server\.source-hash" -Value $Hash -NoNewline
}

$env:UDRIVER_DIST = "$U\package"
if ($Mode -eq '2') {
    & $Dotnet $Dll setup
    exit 0
}

Write-Host ''
$env:UDRIVER_HTTP_PORT = $Port; $env:UDRIVER_PANEL = '1'; $env:UDRIVER_OPEN_PANEL = '1'
& $Dotnet $Dll
Write-Host ''
Write-Host 'UDriver stopped. You can close this window.'
