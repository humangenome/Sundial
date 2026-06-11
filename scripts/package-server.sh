#!/usr/bin/env bash
# Package the Solarpunk headless-server bundle for the Bunny CDN.
# Produces SundialServer-<tag>.zip = the .NET supervisor (self-contained) +
# the UE4SS server distribution (settings + PDB signatures + the Solarpunk mod
# stack) + the pinned UE4SS runtime binaries. The sha256 it prints is the value
# to pin wherever your deployment verifies the bundle.
#
# Usage: scripts/package-server.sh v0.1.0
# UE4SS runtime: set UE4SS_RUNTIME_DIR to a folder containing UE4SS.dll + dwmapi.dll
#   (the 5.7-capable build the box runs), or UE4SS_ZIP_URL to fetch+unzip one.
set -euo pipefail
TAG="${1:-}"; [[ -z "$TAG" ]] && { echo "usage: $0 <git tag e.g. v0.1.0>" >&2; exit 1; }
HERE="$(cd "$(dirname "$0")/.." && pwd)"   # solarpunk/
OUT="$HERE/dist/out"; STAGE="$(mktemp -d)"; trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$OUT"

echo "==> Publishing SolarpunkServer (.NET 8, self-contained win-x64)"
dotnet publish "$HERE/server/SolarpunkServer/SolarpunkServer.csproj" \
  -c Release -r win-x64 --self-contained -o "$STAGE/SolarpunkServer" --nologo >/dev/null

echo "==> Staging UE4SS server distribution (settings + signatures + mods)"
# The supervisor drops ue4ss/ next to the game exe; ship it inside the bundle.
cp -r "$HERE/dist/ue4ss-server" "$STAGE/SolarpunkServer/ue4ss-server"

echo "==> Staging UE4SS runtime binaries (UE4SS.dll + dwmapi.dll)"
RT="$STAGE/SolarpunkServer/ue4ss-server"
if [[ -n "${UE4SS_RUNTIME_DIR:-}" ]]; then
  cp "$UE4SS_RUNTIME_DIR/UE4SS.dll" "$UE4SS_RUNTIME_DIR/dwmapi.dll" "$RT/"
elif [[ -n "${UE4SS_ZIP_URL:-}" ]]; then
  curl -fsSL "$UE4SS_ZIP_URL" -o "$STAGE/ue4ss.zip"
  unzip -oq "$STAGE/ue4ss.zip" UE4SS.dll dwmapi.dll -d "$RT/" || \
    unzip -oq "$STAGE/ue4ss.zip" -d "$STAGE/ue4ss-rt" && \
    cp "$STAGE"/ue4ss-rt/**/UE4SS.dll "$STAGE"/ue4ss-rt/**/dwmapi.dll "$RT/" 2>/dev/null || true
else
  echo "    !! UE4SS runtime not provided — set UE4SS_RUNTIME_DIR or UE4SS_ZIP_URL." >&2
  echo "    !! Bundle will be incomplete (mods won't load without UE4SS.dll+dwmapi.dll)." >&2
fi
[[ -f "$RT/UE4SS.dll" && -f "$RT/dwmapi.dll" ]] && echo "    runtime staged OK" || echo "    runtime MISSING (see above)"

echo "==> Zipping SundialServer-${TAG}.zip"
ZIP="$OUT/SundialServer-${TAG}.zip"; rm -f "$ZIP"
( cd "$STAGE" && zip -rq "$ZIP" SolarpunkServer )
SHA="$(openssl dgst -sha256 "$ZIP" | awk '{print $2}')"
echo
echo "==> $ZIP"
echo "    sha256 = $SHA"
echo "    -> publish via scripts/sign-and-publish-manifest.sh (mirrors the zip and records the hash)"
