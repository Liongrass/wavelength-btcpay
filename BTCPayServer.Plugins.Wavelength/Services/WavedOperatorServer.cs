namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Everything particular to waved's --server.host flag (the Ark operator address), which this
/// plugin surfaces on a store's connection string under the plain key "server" rather than
/// "server.host" itself.
///
/// "server" is entirely OPTIONAL, not required. An earlier version of this comment (and of the
/// README/setup-tab text) claimed BTCPay core's own connection-string safety check
/// (BTCPayServer.Extensions.IsSafe, run by LightningLikePaymentHandler.ValidatePaymentMethodConfig)
/// refuses to save any connection string with no "server" key at all. That is true of core's
/// current `master` branch, but it is NOT true of the exact BTCPay Server version this plugin is
/// pinned to and built against (see the `btcpayserver` submodule, currently v2.4.2): there,
/// `Extensions.IsSafe` returns true (safe) precisely when the "server" key is ABSENT -
/// `if (!kv.TryGetValue("server", out var server)) return true;` - so the plain
/// "type=wavelength;token=..." shape this plugin shipped with before "server" existed saves and
/// works for a non-admin exactly as before, with no "server" key needed at all. Verified directly
/// against BTCPayServer/Extensions.cs in the pinned submodule, not inferred from core's newer
/// behaviour.
///
/// The ordering claim this comment also used to make was backwards for the pinned version too:
/// ValidatePaymentMethodConfig calls `_lightningClientFactory.Create(config.ConnectionString, ...)`
/// - reaching WavelengthLightningConnectionStringHandler.Create, and therefore this plugin's own
/// token/approval/allowlist/value checks - BEFORE it ever calls `client.IsSafe(...)`. So in the
/// pinned version, core's safety check is an additional veto layered ON TOP of whatever this plugin
/// already approved, not a gate this plugin's own approval model sits behind. This plugin's own
/// checks (WavedAllowedFlags, WavedFlagValues) are the primary defense regardless of what core's
/// check does or does not require.
///
/// What IS still true, and is this class's actual reason to exist: a store owner who wants to point
/// at a DIFFERENT Ark operator than waved's own built-in default has to write that value as
/// something `new Uri(value, UriKind.Absolute)` parses into a real host - an absolute http(s) URL,
/// in practice - rather than a bare "host:port". A bare value like
/// "wavelength.lightning.finance:443" parses as an opaque URI whose "scheme" is
/// "wavelength.lightning.finance" and whose host is empty, which fails core's own
/// Utils.TryParseEndpoint(uri.DnsSafeHost, ...) check and makes IsSafe return false - requiring
/// btcpay.server.canmodifyserversettings to save it, even though it is exactly as safe as the
/// equivalent https:// value and even though waved itself only ever wants the bare form. So: no
/// scheme needed if "server" is left out entirely (the common case); a scheme is needed if a store
/// owner chooses to override the default with their own operator address.
///
/// This is also the one allowlisted flag where the connection-string key and the waved flag it
/// maps to genuinely differ (every other WavedAllowedFlags entry is used verbatim as the literal
/// waved flag name) - see WavedProcessManager.StartStoreAsync for where the rename and
/// scheme-stripping below actually happen. WavedAllowedFlags itself only ever sees the literal key
/// "server", never "server.host" - a connection string that tries to set "server.host" directly is
/// rejected as an unrecognized key.
///
/// Carries no extra risk beyond what server.host already carried back when WavedAllowedFlags
/// treated the whole server.* namespace as operator-only: server.host alone was never the
/// primitive that made that namespace dangerous - that was server.macaroonpath (read an arbitrary
/// file and attach it as the outbound macaroon to this exact address), which stays off the
/// allowlist entirely.
///
/// Whether or not core's own check even applies, it does NOT cover this value the way it covers an
/// ordinary HTTP(S) flag: it validates the "server" value by parsing it as a URI and checking only
/// its Host, but waved hands the same string to grpc.NewClient unparsed, and gRPC's own target
/// syntax treats a "scheme://" prefix as selecting a resolver rather than as part of an address
/// validated as a whole - a value can have a public, non-local Host by core's reckoning while gRPC
/// actually dials a completely different, attacker-chosen endpoint buried after the slash (e.g.
/// "passthrough://8.8.8.8/127.0.0.1:10029" - core sees host 8.8.8.8, gRPC's passthrough resolver,
/// genuinely registered in this dependency tree (see sdk/ark/inprocess.go and embedded.go, which
/// both dial "passthrough:///bufnet"), dials 127.0.0.1:10029 instead). See
/// WavedFlagValues.ValidateServer, which is the check that actually closes this, independent of
/// whatever core's own check does or does not require - it does not reuse core's check or
/// ValidateUrl, and rejects anything but a bare, optionally http(s)-prefixed host:port outright
/// before even looking at whether the host is local.
/// </summary>
public static class WavedOperatorServer
{
    /// <summary>
    /// The default public Ark operator endpoint for a network - the same one waved itself resolves
    /// when "server" (--server.host) is left unset entirely, which is the ordinary case: see the
    /// class doc comment for why leaving "server" out altogether needs no scheme, no admin, and no
    /// special handling at all. Returned in the "https://host:port" shape purely as a convenience
    /// for the ONE place this is actually used - the setup page's optional "point at a different
    /// operator" example - so that example shows a real, working address rather than a placeholder,
    /// and already has the scheme a store owner would need if they typed it themselves (see the
    /// class doc comment for why a scheme is needed only in that optional case).
    ///
    /// regtest and simnet have no public operator deployment - their default is waved's own
    /// loopback dev address. Shown here anyway for completeness; nobody needs to type it, since it
    /// is also what waved falls back to on its own.
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
