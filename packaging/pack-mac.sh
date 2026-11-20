#!/bin/sh
# Builds the Mac package into packaging/releases: UDriver.app with the server inside, an installer and the update files.
# Usage: packaging/pack-mac.sh 1.0.0
# Without SIGN_APP the app is signed ad hoc, so macOS can open it after "Open Anyway". To sign and notarize, set SIGN_APP, SIGN_INSTALL and NOTARY_PROFILE as the README explains.
set -e
V="${1:?Usage: pack-mac.sh <version, for example 1.0.0>}"
D="$(cd "$(dirname "$0")" && pwd)"
OUT="$D/build/mac"
rm -rf "$OUT" && mkdir -p "$OUT"
VERSION="$V" sh "$D/../apps/mac/build-app.sh"
cp -R "$D/../apps/mac/UDriver.app" "$OUT/"
command -v "${VPK:-vpk}" >/dev/null || dotnet tool install -g vpk
"${VPK:-vpk}" pack -u UDriver -v "$V" -p "$OUT/UDriver.app" -e UDriver --packTitle UDriver -o "$D/releases" \
  --signAppIdentity "${SIGN_APP:--}" ${SIGN_INSTALL:+--signInstallIdentity "$SIGN_INSTALL"} ${NOTARY_PROFILE:+--notaryProfile "$NOTARY_PROFILE"}
