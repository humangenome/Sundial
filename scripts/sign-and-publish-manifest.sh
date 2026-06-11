#!/usr/bin/env bash
# Sign + publish the Solarpunk launcher auto-update manifest. Model: Beacon's
# scripts/sign-and-publish-manifest.sh. Run after the launcher/server release
# artifacts are built + uploaded to the private GitHub release.
#   1. Download Sundial-Launcher-Windows-x64-<tag>.zip, compute sha256.
#   2. Mirror launcher zip + installer + server package to Bunny (solarpunk/ path).
#   3. Build the manifest (version + sha256 + package_url), ed25519-sign it.
#   4. Upload signed manifest.json to Bunny, purge the edge cache.
# Usage: scripts/sign-and-publish-manifest.sh v0.1.0
# Requires: ~/.sundial-update-keys/sundial-update-ed25519.pem (gen-update-keys.sh),
#           gh CLI, openssl 1.1.1+, $BUNNY_STORAGE_KEY / $BUNNY_PURGE_KEY env vars.
set -euo pipefail
TAG="${1:-}"; [[ -z "$TAG" ]] && { echo "usage: $0 <git tag e.g. v0.1.0>" >&2; exit 1; }
VERSION="${TAG#v}"
KEYFILE="$HOME/.sundial-update-keys/sundial-update-ed25519.pem"
[[ -f "$KEYFILE" ]] || { echo "missing signing key at $KEYFILE — run scripts/gen-update-keys.sh" >&2; exit 1; }

# CDN credentials come from the environment — never commit them.
BUNNY_STORAGE_KEY="${BUNNY_STORAGE_KEY:?set BUNNY_STORAGE_KEY (Bunny storage zone access key)}"
BUNNY_PURGE_KEY="${BUNNY_PURGE_KEY:?set BUNNY_PURGE_KEY (Bunny account API key for cache purge)}"

LAUNCHER_ZIP_NAME="Sundial-Launcher-Windows-x64-${TAG}.zip"
INSTALLER_NAME="SundialSetup-${VERSION}.exe"
SERVER_ZIP_NAME="Solarpunk-Server-Windows-x64-${TAG}.zip"
SRC_OWNER="${SRC_OWNER:-HumanGenome}"; SRC_REPO="${SRC_REPO:-SolarpunkClient}"
PUBLIC_OWNER="${PUBLIC_OWNER:-HumanGenome}"; PUBLIC_REPO="${PUBLIC_REPO:-Solarpunk}"

WORK_DIR="$(mktemp -d)"; trap 'rm -rf "$WORK_DIR"' EXIT

echo "==> Downloading $LAUNCHER_ZIP_NAME from $SRC_OWNER/$SRC_REPO@$TAG"
gh release download "$TAG" -R "$SRC_OWNER/$SRC_REPO" -p "$LAUNCHER_ZIP_NAME" -D "$WORK_DIR" || {
  echo "could not download launcher zip; check the release artifacts uploaded" >&2; exit 1; }
LAUNCHER_ZIP="$WORK_DIR/$LAUNCHER_ZIP_NAME"
SHA256="$(openssl dgst -sha256 "$LAUNCHER_ZIP" | awk '{print $2}')"; echo "    sha256 = $SHA256"
gh release download "$TAG" -R "$SRC_OWNER/$SRC_REPO" -p "$INSTALLER_NAME" -D "$WORK_DIR" || echo "    warning: no installer asset"
gh release download "$TAG" -R "$SRC_OWNER/$SRC_REPO" -p "$SERVER_ZIP_NAME" -D "$WORK_DIR" || echo "    warning: no server package asset"

PACKAGE_URL="https://survivalservers.b-cdn.net/sundial/launcher/$LAUNCHER_ZIP_NAME"
RELEASE_NOTES_URL="https://github.com/$PUBLIC_OWNER/$PUBLIC_REPO/releases/tag/$TAG"
PAYLOAD_FILE="$WORK_DIR/payload.bin"
printf '%s\n%s\n%s' "$VERSION" "$SHA256" "$PACKAGE_URL" > "$PAYLOAD_FILE"
SIG_HEX="$(openssl pkeyutl -sign -inkey "$KEYFILE" -rawin -in "$PAYLOAD_FILE" | xxd -p | tr -d '\n')"
MANIFEST="$WORK_DIR/manifest.json"
cat > "$MANIFEST" <<JSON
{"version":"$VERSION","release_notes_url":"$RELEASE_NOTES_URL","package_url":"$PACKAGE_URL","sha256":"$SHA256","signature":"$SIG_HEX","signature_alg":"ed25519"}
JSON
echo "==> Manifest:"; cat "$MANIFEST"; echo

put() { curl -fsS -X PUT "https://la.storage.bunnycdn.com/survivalservers/sundial/$1" \
  -H "AccessKey: $BUNNY_STORAGE_KEY" -H "Content-Type: $2" --data-binary @"$3" > /dev/null; }
echo "==> Mirroring launcher zip"; put "launcher/$LAUNCHER_ZIP_NAME" application/octet-stream "$LAUNCHER_ZIP"
if [[ -f "$WORK_DIR/$INSTALLER_NAME" ]]; then
  echo "==> Mirroring installer + latest alias"
  put "launcher/$INSTALLER_NAME" application/octet-stream "$WORK_DIR/$INSTALLER_NAME"
  put "launcher/SundialSetup-latest.exe" application/octet-stream "$WORK_DIR/$INSTALLER_NAME"
fi
if [[ -f "$WORK_DIR/$SERVER_ZIP_NAME" ]]; then
  SERVER_SHA256="$(openssl dgst -sha256 "$WORK_DIR/$SERVER_ZIP_NAME" | awk '{print $2}')"
  echo "==> Mirroring $SERVER_ZIP_NAME -> SundialServer-${TAG}.zip"
  put "SundialServer-${TAG}.zip" application/octet-stream "$WORK_DIR/$SERVER_ZIP_NAME"
  echo "    server zip sha256 = $SERVER_SHA256 (record it wherever your deployment pins the bundle)"
fi
echo "==> Uploading signed manifest"; put "launcher/manifest.json" application/json "$MANIFEST"
echo "==> Purging manifest edge cache"
curl -fsS -X POST "https://api.bunny.net/purge?url=https://survivalservers.b-cdn.net/sundial/launcher/manifest.json&async=false" -H "AccessKey: $BUNNY_PURGE_KEY" > /dev/null
echo "==> Done. Verify:"; curl -s "https://survivalservers.b-cdn.net/sundial/launcher/manifest.json"; echo
