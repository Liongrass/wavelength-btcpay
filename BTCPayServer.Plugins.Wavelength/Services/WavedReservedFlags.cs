namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// waved flags the plugin owns and never lets a store's connection string override. Each one
/// protects an invariant something else in the plugin depends on - see the comment per key.
/// Everything else waved accepts (--network, --wallet.esploraurl, --server.host, ...) is passed
/// through verbatim from the connection string.
/// </summary>
public static class WavedReservedFlags
{
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Per-store isolation depends on this directory being the plugin-computed one - the
        // whole point of one waved instance per store (see WavedProcessManager's doc comment).
        "datadir",

        // The channel is always loopback, but that alone is no longer the only thing keeping it
        // safe - see rpc.notls/rpc.no-macaroons below.
        "rpc.listenaddr",

        // Must point at the file WavedWalletCredentialStore's password is written to, or
        // auto-unlock breaks and every restart needs a manual UnlockWallet RPC.
        "wallet.password_file",

        // Deliberately left at waved's default (both enabled) rather than passed at all - see
        // WavedProcessManager.StartStoreAsync/BuildSecureChannel for why: waved auto-generates a
        // self-signed TLS cert and an instance-scoped admin macaroon per store, which this plugin
        // pins/attaches on every call, so a request can only succeed against the exact waved
        // process that generated that pair - a real authentication boundary enforced by waved
        // itself, not just this plugin's own store->port bookkeeping. Reserved here so a
        // connection string can't disable either and silently weaken that back down to the old
        // plaintext/no-auth behavior. Actual waved flag names are rpc.notls / rpc.no-macaroons
        // (confirmed from waved/config.go's mapstructure tags) - not the --no-tls/--no-macaroons
        // shorthand wavelength's INSTALL.md prose uses.
        "rpc.notls",
        "rpc.no-macaroons",

        // The HTTP/JSON gateway is a second listener entirely separate from rpc.listenaddr's
        // gRPC one, and defaults to a FIXED port (localhost:10031) no matter what rpc.listenaddr
        // is set to - every store's waved instance would collide on it otherwise. This plugin
        // only ever talks to waved over gRPC, so the gateway is disabled outright rather than
        // also allocating and tracking a second port per store.
        "rpc.gateway.enabled",
        "rpc.gateway.listenaddr",
    };
}
