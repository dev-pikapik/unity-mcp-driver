#!/bin/sh
# Regenerates Assets/Scenes/Main.unity and makes Development (Mono) builds in Builds/<platform>/.
# Usage: ./build.sh [mac|linux|windows|all]   (default: all)
set -e
cd "$(dirname "$0")"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity}"
case "${1:-all}" in
  mac) method=Mac ;;
  linux) method=Linux ;;
  windows) method=Windows ;;
  all) method=All ;;
  *) echo "usage: $0 [mac|linux|windows|all]" >&2; exit 2 ;;
esac
mkdir -p Logs
"$UNITY" -batchmode -quit -nographics -projectPath "$PWD" -executeMethod "BuggyGameBuilder.$method" -logFile "$PWD/Logs/build-${1:-all}.log" \
  || { echo "build failed, see Logs/build-${1:-all}.log" >&2; exit 1; }
grep "^BuggyGame " "Logs/build-${1:-all}.log"
