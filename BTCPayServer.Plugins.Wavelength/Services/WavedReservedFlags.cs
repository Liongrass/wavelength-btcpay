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

        // The plaintext gRPC channel (no TLS) is only safe because this is always loopback -
        // see WavelengthPlugin.Execute's AppContext switch and GetWalletClient's doc comment.
        "rpc.listenaddr",

        // Must point at the file WavedWalletCredentialStore's password is written to, or
        // auto-unlock breaks and every restart needs a manual UnlockWallet RPC.
        "wallet.password_file",

        // Paired together intentionally for the loopback-only internal channel; see
        // wavelength's INSTALL.md ("a macaroon can't ride an unencrypted connection").
        "no-tls",
        "no-macaroons",
    };
}
