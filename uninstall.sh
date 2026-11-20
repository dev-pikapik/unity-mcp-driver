#!/bin/sh
# Stops the UDriver panel and removes UDriver from this computer. Leaves the Cloudflare tunnel and the packages in Unity projects in place.
U="$HOME/.udriver"
PORT="${UDRIVER_HTTP_PORT:-$(sed -n 's/^UDRIVER_HTTP_PORT="\{0,1\}\([0-9]*\)"\{0,1\}$/\1/p' "$U/remote.env" 2>/dev/null | head -n 1)}"
PORT="${PORT:-8787}"

# Startup item of older versions.
command -v launchctl >/dev/null 2>&1 && launchctl bootout "gui/$(id -u)/org.udriver.remote" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/org.udriver.remote.plist"

# The Linux AppImage holds the server that restores games: the running one, or one in the home folder.
APPIMG=
if [ "$(uname -s)" = Linux ]; then
  for P in $(pgrep -x UDriver 2>/dev/null); do APPIMG="$(tr '\0' '\n' < "/proc/$P/environ" 2>/dev/null | sed -n 's/^APPIMAGE=//p' | head -n 1)"; [ -n "$APPIMG" ] && break; done
  [ -n "$APPIMG" ] || APPIMG="$(find "$HOME" -maxdepth 3 -name 'UDriver*.AppImage' -type f 2>/dev/null | head -n 1)"
fi

# The UDriver app, so it does not start the panel again.
pkill -x UDriver 2>/dev/null && echo "Closed the UDriver app."
# Only the UDriver panel listening on $PORT; it closes its own tunnel when it stops.
for P in $(lsof -ti "tcp:$PORT" -sTCP:LISTEN 2>/dev/null); do
  ps -o command= -p "$P" | grep -q 'udriver-mcp' && kill "$P" && echo "Stopped the UDriver panel."
done
# A tunnel left behind by a panel that crashed.
pkill -f "cloudflared.*--config $U/" 2>/dev/null

# Put back the original files of every game UDriver was added to; the backups stay if that fails.
RESTORED=yes
if [ -n "$(ls -A "$U/backups" 2>/dev/null)" ]; then
  RESTORED=
  for R in /Applications/UDriver.app/Contents/MacOS/server/udriver-mcp "$HOME/Applications/UDriver.app/Contents/MacOS/server/udriver-mcp" "$(command -v udriver)" "$APPIMG"; do
    [ -z "$RESTORED" ] && [ -x "$R" ] && "$R" restore --all && RESTORED=yes
  done
  for D in "$(command -v dotnet)" "$U/dotnet/dotnet" "$HOME/.dotnet/dotnet" /usr/local/share/dotnet/dotnet; do
    [ -z "$RESTORED" ] && [ -x "$D" ] && [ -f "$U/server/udriver-mcp.dll" ] && "$D" "$U/server/udriver-mcp.dll" restore --all 2>/dev/null && RESTORED=yes
  done
fi

# The app holds the server that restores games, so it goes only after every game is restored.
for A in /Applications/UDriver.app "$HOME/Applications/UDriver.app"; do
  [ -d "$A" ] && [ -n "$RESTORED" ] || continue
  osascript -e "tell application \"Finder\" to delete POSIX file \"$A\"" >/dev/null 2>&1 && echo "Moved $A to the Trash." || { echo; echo "Move $A to the Trash to remove the app."; }
done

echo
echo "Agents that use UDriver on this computer were not stopped, so their work is not interrupted."
echo "Remove UDriver from each agent's MCP settings, then restart the agent."

PROJECTS=$([ -f "$U/targets.json" ] && awk -F'"' '/"kind": "project"/ { p = 1; next } p && /"path":/ { print "  " $4; p = 0 }' "$U/targets.json")
if [ -n "$PROJECTS" ]; then
  echo
  echo "To remove UDriver from a Unity project, delete the folder Packages/com.udriver.agent in it. Projects you added:"
  echo "$PROJECTS"
fi

if [ -f "$U/cloudflared.yml" ]; then
  CERT="$U/cloudflare/.cloudflared/cert.pem"
  echo
  echo "Remote access used a Cloudflare tunnel named udriver. To delete it, do it before deleting the folder below. Run in another terminal window:"
  if [ -f "$CERT" ]; then echo "  cloudflared tunnel --origincert \"$CERT\" delete udriver"; else echo "  cloudflared tunnel delete udriver"; fi
  echo "or delete the tunnel udriver in the Cloudflare dashboard at any time."
fi

if [ -z "$RESTORED" ]; then
  echo
  echo "Some games still have UDriver in them, so $U was kept: it holds their original files."
  echo "Close those games and run this again."
elif [ -d "$U" ]; then
  echo
  echo "$U holds your UDriver settings, the remote access setup and the built server."
  printf 'Delete it? This cannot be undone. Type yes to delete: '
  read -r A
  if [ "$A" = yes ]; then rm -rf "$U" && echo "Deleted."; else echo "Kept."; fi
fi
