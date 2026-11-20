#!/bin/sh
# Builds the Linux AppImage and its update files into packaging/releases.
# Usage: packaging/pack-linux.sh 1.0.0   (RID=linux-arm64 for ARM computers)
set -e
V="${1:?Usage: pack-linux.sh <version, for example 1.0.0>}"
D="$(cd "$(dirname "$0")" && pwd)"
RID="${RID:-linux-x64}"
OUT="$D/build/linux"
rm -rf "$OUT" && mkdir -p "$OUT"
dotnet publish "$D/../UDriver.Mcp" -c Release -r "$RID" --self-contained -p:Version="$V" -o "$OUT" -v q -nologo
dotnet publish "$D/../apps/linux" -c Release -r "$RID" --self-contained -p:Version="$V" -o "$OUT" -v q -nologo
command -v "${VPK:-vpk}" >/dev/null || dotnet tool install -g vpk
"${VPK:-vpk}" "[linux]" pack -u UDriver -v "$V" -p "$OUT" -e UDriver -r "$RID" --packTitle UDriver -i "$D/udriver.png" --categories Development -o "${RELEASES:-$D/releases}"
