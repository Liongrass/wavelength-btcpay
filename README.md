# wavelength-btcpay

A BTCPay Server plugin that brings [wavelength](https://github.com/lightninglabs/wavelength) —
a self-custodial Ark / Lightning-swap / on-chain wallet daemon — into BTCPay Server as a
**Lightning wallet backend**. Unlike other Ark-protocol BTCPay plugins, wavelength-btcpay does
not register its own wallet/payment method type; it exclusively shows up as a Lightning
connection option.

## Architecture

- Each BTCPay store gets its own isolated `waved` process (own data dir, own port, own wallet) -
  `waved` is single-wallet-per-process, so per-store isolation means per-store daemon instances.
  See `BTCPayServer.Plugins.Wavelength/Services/WavedProcessManager.cs`.
- The plugin is a thin gRPC client + UI layer over `waved`'s existing gRPC/REST API
  (`waverpc`/`rpc/wavewalletrpc` in the wavelength repo); it does not embed Go code via FFI/cgo.
- Registers as `ILightningConnectionStringHandler` (`type=wavelength;store-id=<storeId>`), not as
  a separate wallet type - see `BTCPayServer.Plugins.Wavelength/Lightning/`.
- On store removal, `waved` is stopped but its data directory (wallet seed, DB) is intentionally
  never auto-deleted.

## Development setup

This repo references BTCPay Server as a submodule (needed for the plugin's `BaseBTCPayServerPlugin`,
`ILightningClient`, `StoreRepository`, etc. - BTCPay plugins are developed against source, not a
stable NuGet SDK):

```bash
git submodule update --init --recursive
```

Requires the .NET SDK version pinned by `btcpayserver/Dockerfile`.

```bash
dotnet build BTCPayServer.Plugins.Wavelength/BTCPayServer.Plugins.Wavelength.csproj
dotnet test BTCPayServer.Plugins.Wavelength.UnitTests/BTCPayServer.Plugins.Wavelength.UnitTests.csproj
```

`waved`/`wavecli` binaries are not built as part of this plugin's build. For local development,
place them under `BTCPayServer.Plugins.Wavelength/Native/<rid>/` (e.g. `Native/linux-x64/waved`).
A publish-time fetch-and-verify step (mirroring how the bark-btcpay plugin pins and downloads
`barkd` release binaries) is planned once wavelength has tagged releases.

## Status

Early scaffolding. The Lightning client (`Lightning/WavelengthLightningClient.cs`) is stubbed
pending a vendored gRPC client generated from wavelength's `.proto` files.
