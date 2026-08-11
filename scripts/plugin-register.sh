#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

# Configures this BTCPay Server developer environment to load the plugin during a debug
# session - lets you attach a debugger and hit breakpoints directly instead of packaging
# and uploading a .btcpay file on every change. See scripts/build-plugin.sh for the
# separate release-packaging path.

source scripts/plugin-env.sh

TARGET_PATH="$(dotnet build "$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath)"

printf '{ "DEBUG_PLUGINS": "%s" }' "$TARGET_PATH" > "btcpayserver/BTCPayServer/appsettings.dev.json"

echo "The plugin will now start when debugging BTCPay Server"
