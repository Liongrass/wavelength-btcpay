namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Everything particular to waved's --server.host flag (the Ark operator address), which this
/// plugin surfaces on a store's connection string under the plain key "server" rather than
/// "server.host" itself.
///
/// Why: BTCPay core's own connection-string safety check
/// (BTCPayServer.Extensions.IsSafeLightningConnectionString, run by
/// LightningLikePaymentHandler.ValidatePaymentMethodConfig before a non-admin's save is even
/// handed to this plugin) treats any connection string with no literal "server" key as unsafe and
/// refuses it outright, requiring btcpay.server.canmodifyserversettings to override - a check that
/// runs before WavelengthLightningConnectionStringHandler.Create is ever called, so nothing in this
/// plugin's own approval model (WavelengthServerSettings.AllowForAllStores, per-store approval) can
/// affect it. Every wavelength connection string needs a "server" key with a non-local http(s)
/// value for a non-admin to be able to save it at all - this is not optional decoration.
///
/// This is also the one allowlisted flag where the connection-string key and the waved flag it
/// maps to genuinely differ (every other WavedAllowedFlags entry is used verbatim as the literal
/// waved flag name) - see WavedProcessManager.StartStoreAsync for where the rename and
/// scheme-stripping below actually happen. WavedAllowedFlags itself only ever sees the literal key
/// "server", never "server.host" - a connection string that tries to set "server.host" directly is
/// rejected as an unrecognized key, since that spelling would not satisfy BTCPay's own check above.
///
/// Carries no extra risk beyond what server.host already carried back when WavedAllowedFlags
/// treated the whole server.* namespace as operator-only: server.host alone was never the
/// primitive that made that namespace dangerous - that was server.macaroonpath (read an arbitrary
/// file and attach it as the outbound macaroon to this exact address), which stays off the
/// allowlist entirely. And a non-admin pointing "server" at a local/private address is refused by
/// BTCPay's own check before this plugin ever sees it, since that is exactly the same check this
/// value has to pass to be saved at all - no separate WavedFlagValues rule is needed for it.
/// </summary>
public static class WavedOperatorServer
{
    /// <summary>
    /// The default public Ark operator endpoint for a network, in the "https://host:port" shape a
    /// connection string's "server" key needs (see the class doc comment for why the scheme
    /// matters here specifically, unlike waved's own bare host:port default). Used only to
    /// pre-fill this store's connection-string samples on the setup page - waved resolves the
    /// identical default itself when server.host is left unset, so this exists purely so the
    /// string a store owner actually saves already satisfies BTCPay's own safety check, rather
    /// than silently omitting a key that check requires.
    ///
    /// regtest and simnet have no public operator deployment - their default is waved's own
    /// loopback dev address, which will not satisfy a non-admin save. That is an acceptable gap:
    /// those networks are for local development, where whoever is setting it up is ordinarily the
    /// server admin already.
    ///
    /// Verified against waved/config.go's defaultNetworkEndpoints (the gRPC addresses - empty
    /// transport selects gRPC, waved's own default). Source commit for this is the one vendored at
    /// Protos/VENDORED_COMMIT.
    /// </summary>
    public static string DefaultFor(string network) => network.ToLowerInvariant() switch
    {
        "mainnet" => "https://wavelength.lightning.finance:443",
        "testnet" => "https://test.wavelength.lightning.finance:443",
        "testnet4" => "https://lumosd-testnet4.testnet.lightningcluster.com:443",
        "signet" => "https://signet.wavelength.lightning.finance:443",
        // regtest, simnet, and anything unrecognized: waved's own local dev default.
        _ => "https://localhost:10010"
    };

    /// <summary>
    /// Maps a connection string's "server" value to the literal value waved's --server.host flag
    /// should receive: the same value with a leading http(s):// scheme removed, if present.
    ///
    /// Not passed through as-is: waved's own REST client tolerates (and ignores) a scheme prefix
    /// already - see operatorRESTBaseURL in waved/outbound_clients.go - but its default transport
    /// is gRPC, which dials the bare host:port form every one of waved's own network defaults
    /// uses. An https:// prefix is not a gRPC dial target and would break it, so the scheme is
    /// stripped here rather than relied upon to be harmless for whichever transport is active.
    /// </summary>
    public static string ToWavedFlagValue(string connectionStringValue)
    {
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (connectionStringValue.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return connectionStringValue[scheme.Length..];
        }

        return connectionStringValue;
    }
}
