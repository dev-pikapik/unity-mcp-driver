#!/bin/sh
# UDriver launcher. Double-click this file (or run in a terminal: sh UDriver.command).
DIR="$(cd "$(dirname "$0")" && pwd)"
U="$HOME/.udriver"
# Settings kept in remote.env by earlier versions; read as literal text, a variable already set wins.
for K in UDRIVER_HTTP_PORT UDRIVER_BUILDS_DIR UDRIVER_DISABLE_TOOLS UDRIVER_READONLY UDRIVER_OAUTH_REDIRECT_HOSTS; do
  V="$(sed -n "s/^$K=//p" "$U/remote.env" 2>/dev/null | head -n 1)"
  case "$V" in \"*\") V="${V#\"}"; V="${V%\"}" ;; esac
  [ -n "$V" ] && [ -z "$(printenv "$K")" ] && export "$K=$V"
done
PORT="${UDRIVER_HTTP_PORT:-8787}"
PANEL="http://127.0.0.1:$PORT/panel"
mkdir -p "$U/logs" && chmod 700 "$U"
fail() { printf '\n%s\n\nPress Enter to close.\n' "$1"; read -r _; exit 1; }
browse() { if command -v open >/dev/null 2>&1; then open "$1"; else xdg-open "$1" >/dev/null 2>&1; fi; }

echo "UDriver"
echo
echo "  1  Set up in the browser (recommended)"
echo "  2  Set up here in the terminal"
echo
printf 'Type 1 or 2 and press Enter [1]: '
read -r MODE
case "$MODE" in ""|1) MODE=1 ;; 2) ;; *) fail "Please type 1 or 2. Start UDriver again to retry." ;; esac

# The panel key goes only to our own server (the process on the port is the one in panel.pid), and only in a request body.
LISTENER="$(lsof -nP -t -iTCP:"$PORT" -sTCP:LISTEN 2>/dev/null | head -n 1)"
if [ "$MODE" = 1 ] && [ -n "$LISTENER" ] && [ "$LISTENER" = "$(cat "$U/panel.pid" 2>/dev/null)" ] \
  && TICKET="$(curl -sf --max-time 2 --data-binary "@$U/panel.key" "$PANEL/ticket")"; then
  browse "$PANEL?ticket=$TICKET"
  echo
  echo "UDriver is already running. Its panel is open in your browser: $PANEL"
  echo "To start a newer version, press Stop in the panel, then start UDriver again."
  exit 0
fi
if [ "$MODE" = 1 ] && { [ -n "$LISTENER" ] || curl -s -o /dev/null --max-time 2 "http://127.0.0.1:$PORT/"; }; then
  fail "Another program already uses port $PORT, most likely an older version of UDriver. Stop it first (close the window it runs in; to see what it is: lsof -nP -iTCP:$PORT -sTCP:LISTEN), then start UDriver again."
fi

# Private copy of the Unity package, so setup works if this folder moves or sits where macOS protects it.
if [ -d "$DIR/com.udriver.agent" ]; then
  rm -rf "$U/package" && mkdir -p "$U/package" && cp -R "$DIR/com.udriver.agent" "$U/package/" || fail "Could not copy the Unity package from $DIR"
fi

has_sdk() { [ -n "$1" ] && "$1" --list-sdks 2>/dev/null | grep -qE '^([89]|[1-9][0-9])\.'; }
find_dotnet() {
  for D in "$(command -v dotnet)" "$U/dotnet/dotnet" /usr/local/share/dotnet/dotnet; do has_sdk "$D" && { echo "$D"; return; }; done
}
DOTNET="$(find_dotnet)"
if [ -z "$DOTNET" ]; then
  echo
  echo "UDriver needs the .NET 8 SDK, a free program from Microsoft."
  for BREW in "$(command -v brew)" /opt/homebrew/bin/brew /usr/local/bin/brew; do [ -x "$BREW" ] && break; done
  if [ "$(uname -s)" = Darwin ] && [ -x "$BREW" ]; then
    printf 'Install it now with Homebrew? macOS will ask for your password. [Y/n] '
    read -r A
    case "$A" in [nN]*) ;; *) "$BREW" install --cask dotnet-sdk@8 ;; esac
    DOTNET="$(find_dotnet)"
  fi
  if [ -z "$DOTNET" ]; then
    browse https://dotnet.microsoft.com/download/dotnet/8.0
    fail "Install the .NET 8 SDK from the Microsoft page that just opened, then start UDriver again."
  fi
fi

# Build the server if it is missing or the sources are newer.
DLL="$U/server/udriver-mcp.dll"
HASH="$(cat "$DIR"/UDriver.Mcp/*.cs "$DIR"/UDriver.Mcp/*.csproj "$DIR"/com.udriver.agent/Runtime/ToolManifest.cs 2>/dev/null | (shasum -a 256 2>/dev/null || sha256sum) | cut -d' ' -f1)"
if [ ! -f "$DLL" ] || [ "$(cat "$U/server/.source-hash" 2>/dev/null)" != "$HASH" ]; then
  echo "Preparing UDriver (about a minute the first time)..."
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 "$DOTNET" publish "$DIR/UDriver.Mcp" -c Release -o "$U/server" -v q >"$U/logs/build.log" 2>&1 \
    || { tail -15 "$U/logs/build.log"; fail "UDriver could not be built. The full log is in $U/logs/build.log"; }
  printf '%s' "$HASH" > "$U/server/.source-hash"
fi

export UDRIVER_DIST="$U/package" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [ "$MODE" = 2 ]; then
  "$DOTNET" "$DLL" setup
  printf '\nPress Enter to close.\n'; read -r _
  exit 0
fi

echo
UDRIVER_HTTP_PORT="$PORT" UDRIVER_PANEL=1 UDRIVER_OPEN_PANEL=1 "$DOTNET" "$DLL"
echo
echo "UDriver stopped. You can close this window."
