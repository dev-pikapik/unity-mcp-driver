#!/bin/sh
# Stops the UDriver panel and removes UDriver from this computer. Leaves the Cloudflare tunnel and the packages in Unity projects in place.
U="$HOME/.udriver"
PORT="${UDRIVER_HTTP_PORT:-8787}"

# Startup item of older versions.
command -v launchctl >/dev/null 2>&1 && launchctl bootout "gui/$(id -u)/org.udriver.remote" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/org.udriver.remote.plist"

# Only the UDriver panel listening on $PORT; it closes its own tunnel when it stops.
for P in $(lsof -ti "tcp:$PORT" -sTCP:LISTEN 2>/dev/null); do
  ps -o command= -p "$P" | grep -q 'udriver-mcp\.dll' && kill "$P" && echo "Stopped the UDriver panel."
done
# A tunnel left behind by a panel that crashed.
pkill -f "cloudflared.*--config $U/" 2>/dev/null

echo
echo "Agents that use UDriver on this computer were not stopped, so their work is not interrupted."
echo "Remove UDriver from each agent's MCP settings, then restart the agent."

if [ -f "$U/targets.json" ]; then
  echo
  echo "To remove UDriver from a Unity project, delete the folder Packages/com.udriver.agent in it. Projects you added:"
  awk -F'"' '/"kind": "project"/ { p = 1; next } p && /"path":/ { print "  " $4; p = 0 }' "$U/targets.json"
fi

if [ -f "$U/cloudflared.yml" ]; then
  CERT="$U/cloudflare/.cloudflared/cert.pem"
  echo
  echo "Remote access used a Cloudflare tunnel named udriver. To delete it, do it before deleting the folder below. Run in another terminal window:"
  if [ -f "$CERT" ]; then echo "  cloudflared tunnel --origincert \"$CERT\" delete udriver"; else echo "  cloudflared tunnel delete udriver"; fi
  echo "or delete the tunnel udriver in the Cloudflare dashboard at any time."
fi

if [ -d "$U" ]; then
  echo
  echo "$U holds your UDriver settings, the panel password, the remote access setup and the built server."
  printf 'Delete it? This cannot be undone. Type yes to delete: '
  read -r A
  if [ "$A" = yes ]; then rm -rf "$U" && echo "Deleted."; else echo "Kept."; fi
fi
