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

Each store's wallet-unlock password is encrypted with BTCPay's own ASP.NET Core Data Protection
key ring, not with anything this plugin manages itself. **Back up and restore BTCPay's
`DataProtection-Keys` directory alongside its database** — moving only the database (a common
mistake with container/volume-based backups) leaves that key ring behind, and a fresh one can't
decrypt what an old one encrypted. A store's connection-string token is not encrypted with that key
ring (see below), so it needs no equivalent backup story — but a store whose settings row is lost
along with the database loses its token seed with it, and that store needs a fresh token.

A store whose password can no longer be decrypted still recovers automatically the next time its
`waved` process starts, *as long as its `wallet_password` file on disk is still intact* — that file
was never protected by the key ring to begin with, so this plugin falls back to it and re-encrypts
it under whatever key ring is active now.

### Connection string tokens are per-store and revocable

A connection string's `token` is **not** the store's BTCPay ID, and it is not the same for every
render of the setup page: it is derived from a random secret seed generated once per store and
stored in that store's own settings row (`WavedStoreSettings.TokenSeed`). Reaching a store's
`waved` instance therefore requires having actually seen that store's generated connection string,
not merely knowing its ID — which is often visible to anyone with even minor access to the store,
since it is in the store's URLs.

Because the token is derived from a stored seed, it **can be revoked**, and only for the store you
choose. The **Advanced** page of a store's Wavelength dashboard has a **Regenerate token** button:
pressing it replaces that store's seed, which immediately invalidates the connection string that
store currently has saved — so Lightning for that store stops working until you copy the new token
from the setup page and save it there. No other store is affected, and no other store's token,
password, or wallet changes. Use this when a connection string has been shared, committed, pasted
somewhere it shouldn't have been, or otherwise exposed.

**If you are upgrading from a version before this change, every existing connection string must be
regenerated.** Tokens issued by older versions carry no store binding and cannot be revoked, which
is exactly the problem this replaces them for; the plugin refuses them rather than accepting them
leniently, so a store still holding one will see a save-time error pointing at the setup page.
Generate a fresh token there, save it, and that store is up to date.

## Server administration

### Wavelength is opt-in, per server or per store

Running Wavelength means the server spawns a long-lived `waved` process for a store and — once
someone clicks **Create wallet** — holds a wallet seed on that store's behalf. That is a decision
about the server's resources and the server's custody of a key, not about one store, so it is gated
the way BTCPay gates its own internal Lightning node and hot wallets (**Server Settings → Policies**):
**a store owner cannot enable Wavelength for a store unless a server administrator has approved it.**

Two ways to approve:

- **For one store**: a server administrator saves that store's wavelength connection string once
  (the usual first step anyway, on the store's **Lightning → Setup Lightning Node** page). The store
  is recorded as approved, and from then on the people and API keys that operate it can use it
  normally — nothing needs re-approving, and store owners do not have to ask again for routine
  payments, payouts, or invoice creation. Merely viewing a store's Wavelength dashboard as an
  administrator is not what grants this; the deliberate save is.
- **For every store at once**: turn on **AllowForAllStores**. There is no settings page for this
  yet, so set it with an environment variable on whatever runs BTCPay:

  ```bash
  WAVELENGTH_ALLOW_ALL_STORES=true
  ```

  This is read at runtime (no rebuild or plugin change needed), and it overrides whatever is
  persisted. Turning it off again is a real revoke for every store that never had an individual
  approval: a save made while it was on does **not** leave a store with a private approval
  afterward — only an administrator's own save of that store's connection string records that.

A store that has not been approved sees a clear message rather than a broken page: saving a
connection string is refused with an explanation, and the store's Wavelength dashboard says the
same thing. An administrator is never refused.

### Limiting how many waved processes run at once

Each store needs its own `waved` process (`waved` is single-wallet-per-process), so a server with
many stores runs many processes. To keep that bounded — a runaway number of stores should not be
able to fork an unbounded number of daemons — the plugin refuses to start another one once
**100** stores have a `waved` process running, and the store's dashboard reports the limit as the
reason. Raise or lower it with:

```bash
WAVELENGTH_MAX_STORE_PROCESSES=250
```

This is a ceiling on *concurrently running* processes, not on stores that have ever used
Wavelength, so stopping one store's wallet (or the process exiting) frees a slot. The limit is
checked on every start path, including the restart of already-initialized stores at BTCPay startup
and the automatic restart after a crash; a store that is already running is never refused.

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

That token is unique to this store and stable across page loads, so the string shown here is the
one that works — see "Connection string tokens are per-store and revocable" above for what it is
made of, and how to cancel one. Don't share it or reuse it for another store; a connection string
carrying another store's token is refused when you try to save it, rather than being accepted and
quietly pointed at that store's wallet.

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

The flags that name an URL or a peer are also **checked by value**, not just by key: a
`wallet.esploraurl`, `wallet.feeurl`, `wallet.btcwallet_peers`, or `wallet.btcwallet_addpeers` value
that points at a local or private address — loopback, an RFC1918 or link-local address (including
`169.254.169.254`), an IPv6 link-local or unique-local address, or a `.internal`/`.local`/`.lan`
name — is refused, because `waved` runs on the server and would otherwise be a way to make the
server itself fetch or connect to addresses on its own internal network. A server administrator
saving such a value is allowed to (they may genuinely run their own esplora or a bitcoind peer on
their LAN); anyone else asks their administrator. Note that this validation does not resolve DNS, so
a public hostname that resolves to a private address at fetch time still gets through: the check
narrows what is reachable rather than closing the class of attack off entirely.

**Before switching a store's wallet backend or network, delete its existing wallet first** —
`waved` can't switch backend or network on an existing wallet in place. Changing flags on an
already-running store takes effect on its next restart, not immediately; use the **Restart waved**
button on the Advanced page to apply a flag change without a full BTCPay restart.

### Create a wallet

The first time you open the store's **Wavelength** dashboard, click **Create wallet**. This
generates a new seed and shows it exactly once — write it down before leaving that screen. If you
navigate away without acknowledging it, the dashboard will keep showing it again on your next visit
until you confirm you've recorded it.
