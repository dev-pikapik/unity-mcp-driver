#!/bin/sh
# Builds the UDriver MCP server and writes a ready-to-paste config block to mcp-config.json
# Game build only (no Unity project): pass the build, e.g.  sh setup-local.sh ~/Games/MyGame.app  (or set UDRIVER_BUILD)
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
BLD="$DIR/Builds"
BUILD="${1:-$UDRIVER_BUILD}"
if [ -n "$BUILD" ]; then
  [ -e "$BUILD" ] || { echo "Build not found: $BUILD"; exit 1; }
  BUILD="$(cd "$(dirname "$BUILD")" && pwd)/$(basename "$BUILD")"
  # A macOS .app is approved by itself; for a game file (Linux/Windows) its folder is approved.
  case "$BUILD" in *.app) ;; *) [ -f "$BUILD" ] && BUILD="$(dirname "$BUILD")" ;; esac
  BLD="$BLD:$BUILD"
fi
dotnet build "$DIR/UDriver.Mcp" -c Release || { echo "BUILD FAILED. Is the .NET 8 SDK installed? Check with: dotnet --version"; exit 1; }
mkdir -p "$DIR/Builds"
cat > "$DIR/mcp-config.json" << JSON
{
  "mcpServers": {
    "udriver": {
      "command": "dotnet",
      "args": ["$DIR/UDriver.Mcp/bin/Release/net8.0/udriver-mcp.dll"],
      "env": { "UDRIVER_BUILDS_DIR": "$BLD" }
    }
  }
}
JSON
# Launcher with the settings baked in: lets another computer start this server over SSH.
cat > "$DIR/run-server.sh" << RUN
#!/bin/sh
export UDRIVER_BUILDS_DIR="$BLD"
exec dotnet "$DIR/UDriver.Mcp/bin/Release/net8.0/udriver-mcp.dll"
RUN
chmod +x "$DIR/run-server.sh"
echo
echo "DONE."
echo
echo "=== Claude Code: open a terminal in your Unity project folder (any folder for a game build) and run this one command ==="
echo
echo "claude mcp add udriver -e UDRIVER_BUILDS_DIR=\"$BLD\" -- dotnet \"$DIR/UDriver.Mcp/bin/Release/net8.0/udriver-mcp.dll\""
echo
echo "=== Claude Code on ANOTHER computer: run this there instead (replace USER and THIS-PC; SSH key login required) ==="
echo
echo "claude mcp add udriver -- ssh -o BatchMode=yes USER@THIS-PC '\"$DIR/run-server.sh\"'"
echo
echo "=== Other agents: paste this block into their MCP settings (also saved in $DIR/mcp-config.json) ==="
echo
cat "$DIR/mcp-config.json"
[ -n "$BUILD" ] && printf '\nThen ask Claude to start the game with app_launch (path: %s), or start it yourself and ask Claude to use app_connect.\n' "$BUILD"
exit 0
