# wavelength-btcpay

A BTCPay Server plugin that brings [Wavelength](https://github.com/lightninglabs/wavelength) — a self-custodial Bitcoin wallet — into BTCPay Server as a **Lightning wallet backend**.

Each BTCPay store that uses it gets its own isolated `waved` process — its own data directory, its own port, its own wallet and seed. `waved` is single-wallet-per-process, so per-store isolation means a per-store daemon instance managed entirely by the plugin.

## What you get

- Transaction log
- Send
- Receive
- Unilateral exit, anytime

## Build

You will need a `waved` binary, which you can either download from the [Wavelength Releases](https://github.com/lightninglabs/wavelength/releases), or compile yourself.
On Linux for example, place the binary into your `wavelength-btcpay/BTCPayServer.Plugins.Wavelength/Native/linux-x64` folder.

Then, run the `build-plugin.sh` script.

```bash
./scripts/build-plugin.sh
```

This publishes the plugin, stages a trimmed copy (dropping localization/static-web-asset noise
BTCPay's host process already has loaded), and packs a `.btcpay` file under `packaged/`.

Upload that file as described below.

## Installation

1. In BTCPay Server, go to **Server Settings → Plugins → Upload Plugin** and upload the `.btcpay`
   file, or drop it directly into your BTCPay data directory's `Plugins/` folder.
2. Restart BTCPay Server.
3. Confirm it loaded: **Server Settings → Plugins** should list "Wavelength."

By default, each store's `waved` data lives under `<BTCPay data dir>/Plugins/Wavelength/stores/`,
listens on loopback starting at port `10029`, and defaults to `mainnet`.

## Using it

### Connect a store to Wavelength

On a store's **Lightning → Setup Lightning Node** page, open the "Custom Node" section and expand
the **Wavelength** entry for ready-made connection string samples, e.g.:

```
type=wavelength;store-id=<your store's ID>
type=wavelength;store-id=<your store's ID>;network=signet
type=wavelength;store-id=<your store's ID>;wallet.type=btcwallet
```

Wavelength can run with a Lightweight (`lwwallet`), Neutrino (`btcwallet`), or LND wallet backend,
on `mainnet`, `signet`, `testnet`, `simnet`, or `regtest`. Besides `store-id`, any flag `waved`
itself accepts can be added the same way (e.g. `network`, `wallet.esploraurl`, `server.host`) and
is passed straight through as `--flag value` to that store's `waved` process. A handful of flags
(`datadir`, `rpc.listenaddr`, `wallet.password_file`, `rpc.notls`, `rpc.no-macaroons`,
`rpc.gateway.enabled`, `rpc.gateway.listenaddr`) are managed by the plugin and can't be overridden.

**Before switching a store's wallet backend or network, delete its existing wallet first** —
`waved` can't switch backend or network on an existing wallet in place. Changing flags on an
already-running store takes effect on its next restart, not immediately; use the **Restart waved**
button on the Advanced page to apply a flag change without a full BTCPay restart.

### Create a wallet

The first time you open the store's **Wavelength** dashboard, click **Create wallet**. This
generates a new seed and shows it exactly once — write it down before leaving that screen. If you
navigate away without acknowledging it, the dashboard will keep showing it again on your next visit
until you confirm you've recorded it.
