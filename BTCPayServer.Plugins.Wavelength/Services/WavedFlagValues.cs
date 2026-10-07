using System.Net;
using System.Net.Sockets;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Value-level checks on the waved flags a store's own connection string may set, complementing
/// <see cref="WavedAllowedFlags"/>. The allowlist answers "may a store owner set this flag at all";
/// this answers "may it be set to this value", which is a different question with a different
/// answer for the same key.
///
/// Why the second question is needed: several flags the allowlist rightly permits are addresses the
/// server's own waved process will then connect to itself. wallet.esploraurl and wallet.feeurl are
/// URLs waved fetches over HTTP(S); wallet.btcwallet_peers and wallet.btcwallet_addpeers are
/// host:port pairs it dials. A store owner who can set those can aim the *server* at anything the
/// *server* can reach - a cloud metadata endpoint on 169.254.169.254, this instance's own NBXplorer,
/// another store's waved instance on loopback, an RFC1918 host on the internal network - turning a
/// store they own into a request-forwarding primitive against their hosting provider's network.
/// The dashboard's error surfaces (a startup failure's stderr, a gRPC status message) are the
/// response half of that primitive, which is why the controllers cap what they display - see
/// UIWavelengthController.TruncateErrorText.
///
/// What this deliberately does not do: resolve DNS. A name that looks public here can resolve to an
/// internal address by the time waved resolves it, so this raises the cost of the attack rather
/// than closing it. Documented as a residual risk in the README, and the same gap BTCPay core has
/// for its own Lightning connection strings (see Extensions.IsLocalNetwork, which this reuses so
/// the plugin's notion of "local" is exactly core's rather than a second, divergent one).
/// </summary>
public static class WavedFlagValues
{
    /// <summary>The flag naming the esplora endpoint waved fetches chain data from.</summary>
    public const string EsploraUrlFlag = "wallet.esploraurl";

    /// <summary>The flag naming the endpoint waved fetches fee estimates from.</summary>
    public const string FeeUrlFlag = "wallet.feeurl";

    /// <summary>The flag listing btcwallet's initial peers, as a comma-separated host:port list.</summary>
    public const string BtcwalletPeersFlag = "wallet.btcwallet_peers";

    /// <summary>The flag listing btcwallet peers to add to its address manager, same shape as <see cref="BtcwalletPeersFlag"/>.</summary>
    public const string BtcwalletAddPeersFlag = "wallet.btcwallet_addpeers";

    /// <summary>
    /// Validates every address-valued flag in <paramref name="flags"/>, returning false and a
    /// message naming the offending flag and value when one is not acceptable.
    ///
    /// <paramref name="allowLocal"/> is the deliberate escape hatch for a server administrator who
    /// really does run esplora, a fee service, or a btcwallet peer on their own LAN. It has to be
    /// passed explicitly at each call site, and the only call site that ever passes true is the
    /// connection-string handler with a verified administrator behind it - a background restart
    /// re-reading already-persisted flags has no user to check and so always validates strictly.
    /// What carries an admin's decision past that point is
    /// <see cref="WavedStoreSettings.AllowLocalEndpoints"/>, recorded when the string was saved, so
    /// strictness at restart time does not silently undo a choice an admin deliberately made. See
    /// WavelengthLightningConnectionStringHandler for both halves.
    ///
    /// Keys this class does not know about pass. That is safe only because the allowlist has already
    /// decided which keys can get here at all, and because a key added to it later is by definition
    /// a reviewed one - if a future waved release gives an allowlisted flag its own address or path
    /// semantics, adding that key here is part of reviewing it.
    /// </summary>
    public static bool Validate(IReadOnlyDictionary<string, string> flags, out string? error, bool allowLocal = false)
    {
        error = null;

        foreach (var (key, value) in flags)
        {
            if (IsUrlFlag(key))
            {
                if (!ValidateUrl(key, value, allowLocal, out error))
                    return false;
            }
            else if (IsPeerListFlag(key))
            {
                if (!ValidatePeerList(key, value, allowLocal, out error))
                    return false;
            }
        }

        return true;
    }

    private static bool IsUrlFlag(string key)
        => key.Equals(EsploraUrlFlag, StringComparison.OrdinalIgnoreCase)
           || key.Equals(FeeUrlFlag, StringComparison.OrdinalIgnoreCase);

    private static bool IsPeerListFlag(string key)
        => key.Equals(BtcwalletPeersFlag, StringComparison.OrdinalIgnoreCase)
           || key.Equals(BtcwalletAddPeersFlag, StringComparison.OrdinalIgnoreCase);

    private static bool ValidateUrl(string key, string value, bool allowLocal, out string? error)
    {
        error = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"'{key}' must be an absolute http:// or https:// URL.";
            return false;
        }

        if (allowLocal || !IsLocalHost(uri.DnsSafeHost))
            return true;

        error = $"'{key}' may not point at a local or private address ({uri.DnsSafeHost}). Ask the " +
                "server administrator to allow it if this endpoint really is on your own network.";
        return false;
    }

    private static bool ValidatePeerList(string key, string value, bool allowLocal, out string? error)
    {
        error = null;

        // An empty value is a legitimate way to say "no peers" (pflag's StringSlice accepts an empty
        // list), so it is not an error - there is no address in it to be local.
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseHostPort(entry, out var host, out _))
            {
                error = $"'{key}' must be a comma-separated list of host:port entries, and '{entry}' " +
                        "is not one.";
                return false;
            }

            if (!allowLocal && IsLocalHost(host))
            {
                error = $"'{key}' may not contain a local or private address ({host}). Ask the server " +
                        "administrator to allow it if this peer really is on your own network.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a host:port entry, accepting exactly the shapes waved's own pflag-based parsing
    /// accepts and rejecting the rest: a bracketed IPv6 literal (with its port after the closing
    /// bracket), or a host with a single colon separating a numeric port in 1..65535. Anything with
    /// no port at all is rejected rather than defaulted, because a peer entry waved would resolve
    /// differently from how it was checked here is exactly the gap this validation exists to avoid.
    /// </summary>
    internal static bool TryParseHostPort(string entry, out string host, out int port)
    {
        host = "";
        port = 0;

        var closingBracket = entry.LastIndexOf(']');
        int portSeparator;
        if (entry.StartsWith('[') && closingBracket > 0)
        {
            // "[::1]:9735" - the host is bracketed, so the port separator is the colon after the
            // bracket, and an address with no following colon is malformed rather than portless.
            host = entry[..(closingBracket + 1)];
            var remainder = entry[(closingBracket + 1)..];
            if (!remainder.StartsWith(':'))
                return false;
            return int.TryParse(remainder[1..], out port) && port is >= 1 and <= 65535;
        }

        portSeparator = entry.LastIndexOf(':');
        if (portSeparator <= 0)
            return false;

        host = entry[..portSeparator];
        // A bare IPv6 literal without brackets has more than one colon; without brackets there is no
        // way to tell its last group from a port, so refuse it rather than guess.
        if (host.Contains(':'))
            return false;

        return int.TryParse(entry[(portSeparator + 1)..], out port) && port is >= 1 and <= 65535;
    }

    /// <summary>
    /// Whether a host name or literal address is one waved should not be sent to. Core's own
    /// <see cref="BTCPayServer.Extensions.IsLocalNetwork"/> is asked first, so the plugin's answer
    /// and core's answer to "is this a local address" cannot drift apart for the shapes core
    /// recognises - loopback, RFC1918, the .internal/.local/.lan suffixes, a dotless name.
    ///
    /// It is not enough on its own, which is why this wrapper exists rather than the call being
    /// made directly. Core's check compares against its own fixed list of prefixes and does not
    /// cover every address that is only reachable from inside a deployment: an IPv4 link-local
    /// address (169.254.0.0/16) is not classified local by it - and that is exactly the range a
    /// cloud metadata service answers on at 169.254.169.254, which is the highest-value target for
    /// a request issued from inside a hosting provider's network. IPv6 link-local (fe80::/10) and
    /// unique-local (fc00::/7) addresses are missed too. Those three are added here.
    ///
    /// What this deliberately does not do is resolve DNS, so a public name that resolves to a
    /// private address at fetch time still gets through - see the class doc comment. This narrows
    /// the reachable set; it does not close the class of attack.
    /// </summary>
    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        // A host the caller wrote as a bracketed IPv6 literal keeps its brackets for URI purposes;
        // both core's check and the parses below want the bare address. Unwrapping also normalises
        // the IPv4-mapped form (::ffff:127.0.0.1), which IPAddress parses to a v4 address.
        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

        if (BTCPayServer.Extensions.IsLocalNetwork(bare))
            return true;

        if (!IPAddress.TryParse(bare, out var address))
            return false;

        // An IPv4-mapped IPv6 address (::ffff:169.254.169.254) is the v4 address wearing a v6
        // spelling, and IPAddress does not normalise it away - so it has to be unwrapped here or it
        // would evade every check below by looking like an ordinary v6 address. That mapping is
        // exactly how the cloud metadata address reaches a fetcher that only vets v4-shaped input.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        // Covers IPv4 link-local (169.254.0.0/16 - the cloud metadata range) and IPv6 link-local
        // (fe80::/10) and unique-local (fc00::/7) addresses.
        if (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || IsIPv4LinkLocal(address))
            return true;

        // The unspecified addresses (0.0.0.0 and ::) are neither loopback nor any private range,
        // so neither core's check nor the ones above catch them - but a dial to them reaches
        // loopback on Linux (the kernel substitutes the localhost address for the wildcard), which
        // is exactly where every other store's waved instance listens. :: slips through because
        // IPAddress parses it as the v6 Any address, not as a v6 literal of 0.0.0.0.
        return address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Any);
    }

    /// <summary>
    /// Whether this is an IPv4 link-local (169.254.0.0/16) address. Checked explicitly rather than
    /// through the framework's own notion of a local address, which excludes this range.
    /// </summary>
    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
