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

### Backing up or migrating this instance

Each store's wallet-unlock password and each connection string's token are encrypted with
BTCPay's own ASP.NET Core Data Protection key ring, not with anything this plugin manages itself.
**Back up and restore BTCPay's `DataProtection-Keys` directory alongside its database** — moving
only the database (a common mistake with container/volume-based backups) leaves that key ring
behind, and a fresh one can't decrypt what an old one encrypted.

If that ever happens anyway, a store whose password can no longer be decrypted still recovers
automatically the next time its `waved` process starts, *as long as its `wallet_password` file on
disk is still intact* — that file was never protected by the key ring to begin with, so this
plugin falls back to it and re-encrypts it under whatever key ring is active now. A store's
connection string token has no equivalent fallback, though: if its key ring is truly gone, the
token is unrecoverable and that store's Lightning setup page needs a fresh connection string
generated and saved. There is also no way to rotate a single store's token in isolation short of
rotating the instance's entire key ring - which would break every other store's token and
password the same way. Treat a leaked connection string as a leaked credential for that store, not
something you can individually revoke.

## Using it

### Connect a store to Wavelength

On a store's **Lightning → Setup Lightning Node** page, open the "Custom Node" section and expand
the **Wavelength** entry for ready-made connection string samples, pre-filled with a token unique
to that store, e.g.:

```
type=wavelength;token=<generated for this store>
type=wavelength;token=<generated for this store>;network=signet
type=wavelength;token=<generated for this store>;wallet.type=btcwallet
```

That token isn't the store's BTCPay ID - it's an opaque value encrypted with BTCPay's own key
ring, so reaching a store's `waved` instance requires having actually seen that specific store's
generated connection string, not just knowing its ID (which is often visible to anyone with even
minor access to a store). Don't share it or reuse it for another store.

Wavelength can run with a Lightweight (`lwwallet`) or Neutrino (`btcwallet`) wallet backend from a
store's own connection string, on `mainnet`, `signet`, `testnet`, `simnet`, or `regtest`. (An
LND-backed wallet is possible too, but isn't offered here: it needs an lnd host and macaroon, and
letting a store's own connection string set those would let it exfiltrate that macaroon to a
server of its choosing.)

Besides `token`, a reviewed set of additional `waved` flags can be added the same way (e.g.
`network`, `wallet.esploraurl`, `wallet.feeurl`, `wallet.recoverywindow`, `debuglevel`,
`allow-mainnet`, the round fee/refresh knobs, and the `oor.*`/`unroll.*` tuning namespaces) and is
passed straight through as `--flag value` to that store's `waved` process. Anything not on that
list is rejected rather than passed through: this plugin manages this store's isolation and auth
(`datadir`, TLS/macaroon material, its listen address) itself, and several other `waved` flags
would otherwise let a connection string read or write an arbitrary file on the server, or expose
a debug endpoint, rather than just configure the wallet - see `WavedAllowedFlags` in the plugin
source for the exact list and the reasoning behind it.

**Before switching a store's wallet backend or network, delete its existing wallet first** —
`waved` can't switch backend or network on an existing wallet in place. Changing flags on an
already-running store takes effect on its next restart, not immediately; use the **Restart waved**
button on the Advanced page to apply a flag change without a full BTCPay restart.

### Create a wallet

The first time you open the store's **Wavelength** dashboard, click **Create wallet**. This
generates a new seed and shows it exactly once — write it down before leaving that screen. If you
navigate away without acknowledging it, the dashboard will keep showing it again on your next visit
until you confirm you've recorded it.
