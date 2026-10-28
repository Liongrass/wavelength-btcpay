namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Everything particular to waved's --server.host flag (the Ark operator address), which this
/// plugin surfaces on a store's connection string under the plain key "server" rather than
/// "server.host" itself.
///
/// Whether "server" is required for a non-admin to save a connection string at all depends on
/// exactly which BTCPay Server version is running - a genuine, confirmed difference between
/// versions, not a single fact this comment can state once:
///
///  - The exact version this plugin is pinned to and built against (the `btcpayserver` submodule,
///    currently v2.4.2) does NOT require it: `BTCPayServer.Extensions.IsSafe` there returns true
///    (safe) precisely when the "server" key is absent -
///    `if (!kv.TryGetValue("server", out var server)) return true;` - and
///    LightningLikePaymentHandler.ValidatePaymentMethodConfig calls
///    `_lightningClientFactory.Create(...)` (reaching this plugin's own Create, and therefore its
///    own token/approval/allowlist/value checks) BEFORE it ever calls `client.IsSafe(...)`. Both
///    verified directly against the pinned submodule's source, not inferred.
///  - BTCPay core's current release line (its `master` branch at the time of writing, and - by a
///    store owner's own reproduction - whatever more recent official release they were actually
///    running) DOES require it: the equivalent check there,
///    `BTCPayServer.Extensions.IsSafeLightningConnectionString`, returns false (unsafe) when
///    "server" is absent, and `LightningLikePaymentHandler.ValidatePaymentMethodConfig` calls that
///    check directly on the raw connection string BEFORE this plugin's Create is ever reached -
///    the exact ordering this comment used to wrongly claim for the pinned version too. A non-admin
///    store owner who saves this plugin's old "type=wavelength;token=..." shape (no "server" key)
///    against a BTCPay Server running that version is refused outright, with core's own generic
///    error naming none of this plugin's actual concerns ("...should not contain parameters ending
///    in 'filepath' or 'directorypath', use custom LND TLS handling, point to a local endpoint, or
///    use a DNS name for a non-HTTP endpoint.") - and this plugin cannot intercept, customize, or
///    work around that refusal, since it never even sees the connection string.
///
/// Because real-world BTCPay Server deployments are far more likely to be running something close
/// to that second version than the exact pinned submodule, the setup page's samples and the README
/// include "server" with a real default value in every one of them - not because this plugin or the
/// pinned core version requires it, but because leaving it out breaks a plain copy-paste on the
/// BTCPay Server versions a store owner is actually likely to be running.
///
/// Either way, a store owner who wants to point at a DIFFERENT Ark operator than waved's own
/// built-in default has to write that value as something `new Uri(value, UriKind.Absolute)` parses
/// into a real host - an absolute http(s) URL, in practice - rather than a bare "host:port". A bare
/// value like "wavelength.lightning.finance:443" parses as an opaque URI whose "scheme" is
/// "wavelength.lightning.finance" and whose host is empty, which fails core's own
/// Utils.TryParseEndpoint(uri.DnsSafeHost, ...) check on both versions above and makes the safety
/// check return false - requiring btcpay.server.canmodifyserversettings to save it, even though it
/// is exactly as safe as the equivalent https:// value and even though waved itself only ever wants
/// the bare form.
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
    /// The default public Ark operator endpoint for a network, in the "https://host:port" shape a
    /// connection string's "server" key needs (see the class doc comment for why the scheme
    /// matters here specifically, unlike waved's own bare host:port default). Used to pre-fill this
    /// store's connection-string samples on the setup page: waved resolves the identical default
    /// itself when server.host is left unset, so this exists purely so the string a store owner
    /// actually saves already satisfies core's own safety check on the BTCPay Server versions that
    /// have one, rather than depending on which exact version happens to be running.
    ///
    /// regtest and simnet have no public operator deployment - their default is waved's own
    /// loopback dev address, which will not satisfy a non-admin save on a BTCPay Server version that
    /// requires "server" at all. That is an acceptable gap: those networks are for local
    /// development, where whoever is setting it up is ordinarily the server admin already.
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
