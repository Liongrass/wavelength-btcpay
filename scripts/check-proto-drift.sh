#!/usr/bin/env bash
set -euo pipefail

# Fails if the vendored .proto files under Protos/ differ from their source in
# lightninglabs/wavelength. Run in CI on every PR that touches Protos/; on failure, copy the
# updated files from a local wavelength checkout and commit them - see Protos/VENDORED_COMMIT
# for the commit they were last vendored from.

WAVELENGTH_REPO="${WAVELENGTH_REPO:-https://github.com/lightninglabs/wavelength.git}"
WAVELENGTH_REF="${WAVELENGTH_REF:-main}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROTOS_DIR="$ROOT_DIR/BTCPayServer.Plugins.Wavelength/Protos"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "Fetching $WAVELENGTH_REPO@$WAVELENGTH_REF to check for proto drift..."
git clone --depth 1 --branch "$WAVELENGTH_REF" "$WAVELENGTH_REPO" "$WORK_DIR" --quiet

drifted=0

check() {
  local upstream_rel="$1" vendored_abs="$2"
  if ! diff -q "$WORK_DIR/$upstream_rel" "$vendored_abs" > /dev/null 2>&1; then
    echo ""
    echo "DRIFT: $vendored_abs is stale relative to $WAVELENGTH_REPO@$WAVELENGTH_REF:$upstream_rel"
    echo "  Copy the updated file from a local wavelength checkout and commit it, e.g.:"
    echo "  cp \$WAVELENGTH_CHECKOUT/$upstream_rel $vendored_abs"
    drifted=1
  fi
}

check "waverpc/daemon.proto" "$PROTOS_DIR/waverpc/daemon.proto"
check "rpc/wavewalletrpc/wallet.proto" "$PROTOS_DIR/wavewalletrpc/wallet.proto"

if [ "$drifted" -ne 0 ]; then
  echo ""
  echo "After copying, update the recorded source commit:"
  echo "  git -C \$WAVELENGTH_CHECKOUT rev-parse HEAD > $PROTOS_DIR/VENDORED_COMMIT"
  exit 1
fi

echo "Vendored protos match $WAVELENGTH_REPO@$WAVELENGTH_REF."
