namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Allowlist of waved flags a store's own connection string may set. Default-deny: anything
/// waved accepts that isn't listed here (as an exact key, or under one of AllowedPrefixes) is
/// rejected outright rather than silently dropped or passed through.
///
/// This replaced a denylist (formerly WavedReservedFlags) that named specific dangerous flags one
/// at a time. That model meant every flag a future waved release adds is store-owner-settable by
/// default - the wrong default for a connection string that carries no authorization beyond
/// itself. A security review turned up several arbitrary-file-read/write and shared-resource
/// primitives across waved's operator/lnd/rpc/swap/pprof/metrics config surface (confirmed
/// against waved/config.go and friends):
///  - server.macaroonpath / lnd.macaroonpath: read an arbitrary file, hex-encode it, and attach
///    it as the outbound macaroon header to server.host/lnd.host - both attacker-controlled from
///    the same connection string. Fires automatically right after wallet unlock.
///  - rpc.tlscertpath / rpc.tlskeypath / rpc.macaroonpath: MkdirAll+write this store's own auth
///    material at an attacker-chosen path (rpcauth/tls.go, waved/rpc_security.go) - pointed at
///    another store's datadir, this can corrupt that store's cert/macaroon files.
///  - logdir: same MkdirAll+write shape, appends attacker-influenced content to a log file at an
///    arbitrary path.
///  - swap.databasefilename: absolute-path override for the swap SQLite DB - two stores pointed
///    at the same file share (and corrupt) one DB tracking in-flight swap state.
///  - wallet.btcwallet_datadir/blockheaderssource/filterheaderssource: a datadir-relocation
///    primitive plus a "local file path or HTTP(S) URL that neutrino imports [headers] from" -
///    an arbitrary-local-file-import primitive, per waved's own doc comment.
///  - pprof.listen / metrics.listen: bind an HTTP server at an arbitrary address; waved's own
///    PprofConfig doc comment warns a wallet process's heap dump can contain seed/password/
///    macaroon material.
/// Under an allowlist, none of these - or any flag like them a future waved release adds - is
/// reachable from a connection string until someone deliberately reviews and adds it here.
/// </summary>
public static class WavedAllowedFlags
{
    // Exact flag keys a store's own connection string may set, reviewed against waved/config.go:
    // none of them takes a local file/directory path, a listen/bind address, or auth material,
    // and none shares a resource across stores.
    private static readonly IReadOnlySet<string> ExactKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Network/backend selection and general daemon behavior.
        "network",
        "debuglevel",
        "allow-mainnet",

        // Round/registration/signing timing knobs - plain durations/counts, no I/O of their own.
        "forfeitcollectiontimeout",
        "signingworkers",
        "registrationtimeout",

        // Fee/refresh budget knobs the wallet's own automatic maintenance is bound by - the
        // wallet-side half of the #270 seal-time fee handshake, not anything server-authoritative.
        "maxoperatorfeesat",
        "autorefreshfeefloorsat",
        "autorefreshfeerateppm",
        "maxpaymentcltv",
        "eagerroundjoin",

        // wallet.* leaves that configure the wallet backend's own behavior, not its auth/file-
        // system footprint. wallet.password_file (plugin-owned, written by
        // WavedWalletCredentialStore) and the btcwallet_datadir/blockheaderssource/
        // filterheaderssource path-import primitives are deliberately absent - see the class doc
        // comment.
        "wallet.type",
        "wallet.esploraurl",
        "wallet.pollinterval",
        "wallet.recoverywindow",
        "wallet.feeurl",
        "wallet.btcwallet_peers",
        "wallet.btcwallet_addpeers",
        "wallet.persist_filters",
        "wallet.disable_btcwallet_global_logs",
    };

    // Whole namespaces that are entirely numeric safety-tuning knobs (caps/durations) with no
    // path, listen-address, or auth-material field anywhere in them - verified against
    // waved/config.go's OORConfig/OORLimitsConfig and UnrollConfig.
    private static readonly string[] AllowedPrefixes = ["oor.", "unroll."];

    public static bool IsAllowed(string key)
        => ExactKeys.Contains(key)
           || AllowedPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
