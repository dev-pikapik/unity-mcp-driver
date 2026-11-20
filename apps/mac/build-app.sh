#!/bin/sh
# Builds UDriver.app next to this script, with the server inside.
set -e
cd "$(dirname "$0")"
VERSION="${VERSION:-1.0.1}"
swift build -c release
BIN="$(swift build -c release --show-bin-path)/UDriver"
APP=UDriver.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/UDriver"

TMP="$(mktemp -d)"
"$BIN" --write-icon "$TMP/icon.png"
mkdir "$TMP/UDriver.iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s "$TMP/icon.png" --out "$TMP/UDriver.iconset/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) "$TMP/icon.png" --out "$TMP/UDriver.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$TMP/UDriver.iconset" -o "$APP/Contents/Resources/UDriver.icns"
rm -rf "$TMP"
S="$APP/Contents/MacOS/server"
dotnet publish ../../UDriver.Mcp -c Release -r "osx-$(uname -m | sed s/x86_64/x64/)" --self-contained -p:Version="$VERSION" -o "$S" -v q -nologo
cp ../../packaging/udriver "$S/"
sh ../../native/build.sh && cp ../../native/udriver-il2cpp.dylib "$S/"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>UDriver</string>
  <key>CFBundleDisplayName</key><string>UDriver</string>
  <key>CFBundleIdentifier</key><string>com.udriver.app</string>
  <key>CFBundleExecutable</key><string>UDriver</string>
  <key>CFBundleIconFile</key><string>UDriver</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>UDriver</string>
</dict>
</plist>
EOF
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
echo "Built $(pwd)/$APP"
