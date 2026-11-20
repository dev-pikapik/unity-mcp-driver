#!/bin/sh
# Builds the Mac package into packaging/releases: UDriver.app with the server inside, an installer and the update files.
# Usage: packaging/pack-mac.sh 1.0.0
# Signing and notarization are optional; set SIGN_APP, SIGN_INSTALL and NOTARY_PROFILE as the README explains.
set -e
V="${1:?Usage: pack-mac.sh <version, for example 1.0.0>}"
D="$(cd "$(dirname "$0")" && pwd)"
OUT="$D/build/mac"
rm -rf "$OUT" && mkdir -p "$OUT"
VERSION="$V" sh "$D/../apps/mac/build-app.sh"
cp -R "$D/../apps/mac/UDriver.app" "$OUT/"
S="$OUT/UDriver.app/Contents/MacOS/server"
dotnet publish "$D/../UDriver.Mcp" -c Release -r "osx-$(uname -m | sed s/x86_64/x64/)" --self-contained -p:Version="$V" -o "$S" -v q -nologo
cp "$D/udriver" "$S/"
sh "$D/../native/build.sh" && cp "$D/../native/udriver-il2cpp.dylib" "$S/"
[ -n "$SIGN_APP" ] || codesign --force --deep --sign - "$OUT/UDriver.app"
command -v "${VPK:-vpk}" >/dev/null || dotnet tool install -g vpk
"${VPK:-vpk}" pack -u UDriver -v "$V" -p "$OUT/UDriver.app" -e UDriver --packTitle UDriver -o "$D/releases" \
  ${SIGN_APP:+--signAppIdentity "$SIGN_APP"} ${SIGN_INSTALL:+--signInstallIdentity "$SIGN_INSTALL"} ${NOTARY_PROFILE:+--notaryProfile "$NOTARY_PROFILE"}
