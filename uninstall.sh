#!/bin/sh
# Stops UDriver and removes its traces from this computer. Leaves the Cloudflare tunnel and the package in the Unity project untouched.
U="$HOME/.udriver"
command -v launchctl >/dev/null 2>&1 && launchctl bootout "gui/$(id -u)/org.udriver.remote" 2>/dev/null
rm -f "$HOME/Library/LaunchAgents/org.udriver.remote.plist"
pkill -f "udriver-mcp.dll" 2>/dev/null
pkill -f "cloudflared.* run udriver" 2>/dev/null
echo "UDriver stopped."
echo "To delete settings, password and the built server:  rm -rf \"$U\""
echo "To delete the Cloudflare tunnel:                    cloudflared tunnel delete udriver"
[ -f "$U/project" ] && echo "To remove the package from Unity, delete the folder $(cat "$U/project")/Packages/com.udriver.agent"
