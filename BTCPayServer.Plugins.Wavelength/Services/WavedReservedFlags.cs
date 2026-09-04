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

        // Redirect this store's own RPC TLS cert/key or admin macaroon away from the
        // network-datadir location BuildSecureChannel hardcodes reading from (see
        // WavedProcessManager.StartStoreAsync), and this plugin's own pinning check either reads
        // stale/attacker-influenced material or throws trying to read a file waved never wrote.
        // No legitimate BTCPay-managed store ever needs to set these - this plugin generates and
        // consumes both itself. Same rationale as rpc.notls/rpc.no-macaroons above.
        "rpc.tlscertpath",
        "rpc.tlskeypath",
        "rpc.macaroonpath",

        // Arbitrary-file-read-and-exfiltrate primitive, confirmed against waved's source
        // (outbound_clients.go's operatorRESTOptions/rpcauth.HexFromFile does a raw os.ReadFile
        // of whatever path is given here, hex-encodes it, and attaches it as the "macaroon"
        // header on every outbound ArkService/MailboxService request to server.host - which the
        // SAME connection string also controls). A malicious store owner could point this at
        // another store's admin.macaroon (or any other file this OS user can read) and
        // server.host at their own server to steal it - the request fires automatically right
        // after wallet unlock (waved/server.go's connectAndBootstrapMailbox), no further action
        // needed beyond creating the wallet or restarting waved. lnd.macaroonpath is the same
        // shape for the lnd wallet backend (read, then attached as the lnd gRPC macaroon against
        // lnd.host). Neither has any legitimate use for a BTCPay-managed store - this plugin
        // never exposes an "operator" or "lnd node" macaroon concept to store owners at all.
        "server.macaroonpath",
        "lnd.macaroonpath",
    };
}
