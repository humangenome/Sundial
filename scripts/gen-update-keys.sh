#!/usr/bin/env bash
# Generate the Solarpunk launcher auto-update signing keypair (ed25519).
# Run ONCE. The private key signs manifests (sign-and-publish-manifest.sh); the
# public key hex must be embedded in the launcher's AutoUpdater (replaces the
# placeholder pubkey) so it verifies signed manifests.
set -euo pipefail
DIR="$HOME/.sundial-update-keys"
KEY="$DIR/sundial-update-ed25519.pem"
mkdir -p "$DIR"; chmod 700 "$DIR"
if [[ -f "$KEY" ]]; then echo "key already exists at $KEY — refusing to overwrite" >&2; exit 1; fi
openssl genpkey -algorithm ed25519 -out "$KEY"
chmod 600 "$KEY"
PUB_HEX="$(openssl pkey -in "$KEY" -pubout -outform DER | tail -c 32 | xxd -p | tr -d '\n')"
echo "private key: $KEY (chmod 600)"
echo "PUBLIC KEY HEX (embed in the launcher AutoUpdater, 64 hex chars):"
echo "  $PUB_HEX"
