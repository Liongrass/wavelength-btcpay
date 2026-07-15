#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

PLUGIN_NAME="BTCPayServer.Plugins.Wavelength"
PLUGIN_DIR="$PLUGIN_NAME"
CONFIGURATION="${1:-Release}"
STAGE_DIR="build/$PLUGIN_NAME"
OUTPUT_DIR="packaged"

if [ ! -f "$PLUGIN_DIR/$PLUGIN_NAME.csproj" ]; then
    echo "Error: project not found at $PLUGIN_DIR/$PLUGIN_NAME.csproj"
    exit 1
fi

echo "Publishing $PLUGIN_NAME ($CONFIGURATION)..."
dotnet publish "$PLUGIN_DIR/$PLUGIN_NAME.csproj" --configuration "$CONFIGURATION" --verbosity quiet

TFM=$(ls -d "$PLUGIN_DIR/bin/$CONFIGURATION"/net* 2>/dev/null | sort -V | tail -1 | xargs basename)
if [ -z "$TFM" ]; then
    echo "Error: could not detect target framework in $PLUGIN_DIR/bin/$CONFIGURATION/"
    exit 1
fi
PUBLISH_OUTPUT="$PLUGIN_DIR/bin/$CONFIGURATION/$TFM/publish"

# CopyLocalLockFileAssemblies=true (needed so our own extra dependencies - Grpc.Net.Client,
# Google.Protobuf, etc. - actually land in the output) also drags in BTCPayServer's entire
# package graph plus its localization satellite resources and static web assets, since
# <Private>false</Private> on the BTCPayServer ProjectReference doesn't suppress that for
# `dotnet publish`. BTCPay's host process already has all of that loaded; packaging it again
# would just bloat the .btcpay to 1000+ files. bark-btcpay's own build-plugin.sh works around
# the same CopyLocalLockFileAssemblies side effect the same way: keep the flat *.dll set (still
# bloated, but safe - guessing which DLLs are "safe" to drop risks breaking the plugin at load
# time) and drop the wwwroot/localization/runtimes noise, which is pure waste.
echo "Staging a trimmed copy at $STAGE_DIR..."
rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR"

cp "$PUBLISH_OUTPUT"/*.dll "$STAGE_DIR/"
cp "$PUBLISH_OUTPUT/$PLUGIN_NAME.deps.json" "$STAGE_DIR/"
[ -f "$PUBLISH_OUTPUT/$PLUGIN_NAME.pdb" ] && cp "$PUBLISH_OUTPUT/$PLUGIN_NAME.pdb" "$STAGE_DIR/"
[ -f "$PUBLISH_OUTPUT/$PLUGIN_NAME.xml" ] && cp "$PUBLISH_OUTPUT/$PLUGIN_NAME.xml" "$STAGE_DIR/"

if [ -d "$PUBLISH_OUTPUT/Native" ]; then
    cp -r "$PUBLISH_OUTPUT/Native" "$STAGE_DIR/"
else
    echo "Warning: no Native/ directory in publish output - the packaged plugin won't be able to start waved for any store."
fi

echo "Packing .btcpay via BTCPayServer.PluginPacker..."
dotnet run --project btcpayserver/BTCPayServer.PluginPacker -- "$STAGE_DIR" "$PLUGIN_NAME" "$OUTPUT_DIR"

echo ""
echo "Done. Upload the .btcpay file printed above via Server Settings -> Plugins -> Upload"
echo "(or drop it directly into your BTCPay data directory's Plugins/ folder)."
