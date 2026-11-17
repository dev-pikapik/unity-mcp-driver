#!/bin/sh
# UDriver launcher. Double-click this file (or run in a terminal: sh UDriver.command).
# The server runs only while this window is open. To stop: the "Stop everything" button in the panel, or close the window.
# Nothing is added to startup items.
DIR="$(cd "$(dirname "$0")" && pwd)"
U="$HOME/.udriver"
PORT=8787
mkdir -p "$U/bin" "$U/logs" && chmod 700 "$U"
fail() { printf '\nERROR: %s\n\nPress Enter to close.\n' "$1"; read -r _; exit 1; }

# Already running? Then just open the panel.
if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/panel"; then
  echo "UDriver is already running. Opening the panel."
  command -v open >/dev/null 2>&1 && open "http://127.0.0.1:$PORT/panel"
  exit 0
fi

# Remove the startup item left by older versions, if any.
if command -v launchctl >/dev/null 2>&1; then launchctl bootout "gui/$(id -u)/org.udriver.remote" 2>/dev/null; rm -f "$HOME/Library/LaunchAgents/org.udriver.remote.plist"; fi
pkill -f "cloudflared.* run udriver" 2>/dev/null

echo "Checking required programs..."
DOTNET="$(command -v dotnet || true)"
[ -z "$DOTNET" ] && [ -x "$U/dotnet/dotnet" ] && DOTNET="$U/dotnet/dotnet"
[ -z "$DOTNET" ] && [ -x /usr/local/share/dotnet/dotnet ] && DOTNET=/usr/local/share/dotnet/dotnet
if [ -z "$DOTNET" ] || [ -z "$("$DOTNET" --list-sdks 2>/dev/null)" ]; then
  echo "Installing .NET into $U/dotnet (no admin password needed, takes a couple of minutes)..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$U/dotnet-install.sh" || fail "could not download the .NET installer (no internet?)"
  bash "$U/dotnet-install.sh" --channel 8.0 --install-dir "$U/dotnet" >"$U/logs/dotnet-install.log" 2>&1 || fail ".NET installation failed, details in $U/logs/dotnet-install.log"
  DOTNET="$U/dotnet/dotnet"
fi
case "$DOTNET" in "$U/dotnet/dotnet") export DOTNET_ROOT="$U/dotnet" ;; esac

CF="$(command -v cloudflared || true)"
[ -z "$CF" ] && [ -x "$U/bin/cloudflared" ] && CF="$U/bin/cloudflared"
if [ -z "$CF" ]; then
  echo "Downloading cloudflared..."
  # Pinned release, checked against the SHA-256 digests GitHub publishes for these assets.
  CFV=2026.11.0
  A=amd64; case "$(uname -m)" in arm64|aarch64) A=arm64 ;; esac
  if [ "$(uname -s)" = "Darwin" ]; then F="cloudflared-darwin-$A.tgz"; else F="cloudflared-linux-$A"; fi
  case "$F" in
    cloudflared-darwin-amd64.tgz) SUM=8a59d51120cebbe902f0a8d6e1b7a7fd4f9a5bb4dcb3e0f05872483e75e7db6b ;;
    cloudflared-darwin-arm64.tgz) SUM=8c2f90277ef06a6080bb40b964d33f09f1188102f8798f2e344174b6cf41accf ;;
    cloudflared-linux-amd64) SUM=51f7c68b57ed7269164075d7e789efce5b59355982f06392deeb037d982a51fd ;;
    cloudflared-linux-arm64) SUM=abe54a006acf9faf2d19ee9d953cb0d0804e92f59c5382a162e57b84d086f56f ;;
  esac
  T="$U/bin/$F.download"
  curl -fsSL -o "$T" "https://github.com/cloudflare/cloudflared/releases/download/$CFV/$F" || { rm -f "$T"; fail "could not download cloudflared"; }
  GOT="$( (command -v sha256sum >/dev/null 2>&1 && sha256sum "$T" || shasum -a 256 "$T") | cut -d' ' -f1)"
  [ "$GOT" = "$SUM" ] || { rm -f "$T"; fail "the downloaded cloudflared does not match its published checksum; it was deleted"; }
  case "$F" in *.tgz) tar -xzf "$T" -C "$U/bin" cloudflared || fail "could not unpack cloudflared"; rm -f "$T" ;; *) mv "$T" "$U/bin/cloudflared" ;; esac
  chmod +x "$U/bin/cloudflared"
  CF="$U/bin/cloudflared"
fi

# Build the server if it is missing or the sources are newer.
DLL="$U/server/udriver-mcp.dll"
if [ ! -f "$DLL" ] || [ -n "$(find "$DIR/UDriver.Mcp" \( -name '*.cs' -o -name '*.csproj' \) -newer "$DLL" 2>/dev/null | head -1)" ]; then
  echo "Building the server (about a minute)..."
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET" publish "$DIR/UDriver.Mcp" -c Release -o "$U/server" -v q >"$U/logs/build.log" 2>&1 \
    || { tail -15 "$U/logs/build.log"; fail "server build failed, full log in $U/logs/build.log"; }
  touch "$DLL"
fi

echo
OPEN=1
while :; do
  # Settings are read as plain KEY="value" lines (never executed as shell code); only UDRIVER_* names are taken.
  if [ -f "$U/remote.env" ]; then
    while IFS= read -r L || [ -n "$L" ]; do
      L="${L%"$(printf '\r')"}"
      K="${L%%=*}"; V="${L#*=}"
      case "$L" in UDRIVER_*=*) ;; *) continue ;; esac
      case "$K" in *[!A-Z0-9_]*) continue ;; esac
      case "$V" in \"*\") V="${V#\"}"; V="${V%\"}" ;; esac
      export "$K=$V"
    done < "$U/remote.env"
  fi
  UDRIVER_HTTP_PORT="$PORT" UDRIVER_PANEL=1 UDRIVER_DIST="$DIR" UDRIVER_CLOUDFLARED="$CF" UDRIVER_OPEN_PANEL="$OPEN" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET" "$DLL"
  CODE=$?
  OPEN=0
  [ "$CODE" = "3" ] || break      # 3 = setup finished, restart with the new settings
done
pkill -f "cloudflared.* run udriver" 2>/dev/null
echo
echo "UDriver stopped. You can close this window."
