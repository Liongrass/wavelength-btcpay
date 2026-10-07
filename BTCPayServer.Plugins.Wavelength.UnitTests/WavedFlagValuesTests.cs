using BTCPayServer.Plugins.Wavelength.Services;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

/// <summary>
/// The value-level check on the waved flags a store's connection string may set - the other half of
/// WavedAllowedFlags, which only decides whether a store may set a key at all. Every rejection here
/// is an address the server's own waved process would have been made to connect to.
/// </summary>
public class WavedFlagValuesTests
{
    private static Dictionary<string, string> Flags(params (string Key, string Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    [Theory]
    // The cloud metadata endpoint, which is the highest-value target for a request from inside a
    // hosting provider's network.
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    // The same range, and IPv6 link-local/unique-local, which core's own check also misses - see
    // WavedFlagValues.IsLocalHost.
    [InlineData("http://169.254.0.1/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    // This instance's own NBXplorer, or another store's waved instance, both of which live on
    // loopback.
    [InlineData("http://127.0.0.1:10029/")]
    [InlineData("http://localhost:32838/")]
    // An RFC1918 host reachable only from inside the deployment.
    [InlineData("http://10.1.2.3:8080/api")]
    [InlineData("https://192.168.1.10/esplora")]
    [InlineData("http://172.16.0.5/")]
    // DNS names that are by convention internal rather than routable.
    [InlineData("https://nbxplorer.internal/api")]
    [InlineData("https://esplora.local/")]
    [InlineData("https://esplora.lan/")]
    // A bare, dotless name resolves through the search domain, which is the internal one.
    [InlineData("http://esplora/")]
    // IPv6 loopback and link-local, and the v6 wildcard: neither core's check nor the range
    // checks classify :: as private, but a dial to it reaches loopback on Linux, so it is just
    // as internal as ::1. 0.0.0.0 is the v4 wildcard for the same reason.
    [InlineData("http://[::1]:10029/")]
    [InlineData("http://[::]:10029/")]
    [InlineData("http://0.0.0.0:10029/")]
    // A trailing dot is the DNS root, invisible to .NET's IPAddress.TryParse and to core's
    // suffix rules, but Go's resolver strips it and resolves the literal - so waved would dial
    // exactly the local address this check sees as a public name.
    [InlineData("http://127.0.0.1./api")]
    [InlineData("http://169.254.169.254./latest/meta-data/")]
    [InlineData("http://localhost.:32838/")]
    [InlineData("http://10.0.0.1./api")]
    // IPv4 shorthand forms, which .NET 10's IPAddress.TryParse and Uri both normalize to full
    // dotted quads - blocked by the ordinary range rules once normalized.
    [InlineData("http://127.1/api")]
    [InlineData("http://0x7f.0.0.1/api")]
    [InlineData("http://2130706433/api")]
    public void LocalOrPrivateUrlsAreRejected(string url)
    {
        Assert.False(WavedFlagValues.Validate(Flags((WavedFlagValues.EsploraUrlFlag, url)), out var error));
        Assert.NotNull(error);
        Assert.Contains(WavedFlagValues.EsploraUrlFlag, error);
    }

    [Fact]
    public void LocalValueIsRejectedForTheFeeUrlToo()
    {
        // Nothing about the check is specific to esplora - every URL-shaped flag waved will fetch
        // goes through it, which is what keeps a new allowlisted URL flag from arriving unguarded.
        Assert.False(WavedFlagValues.Validate(Flags((WavedFlagValues.FeeUrlFlag, "http://127.0.0.1:5000/")), out var error));
        Assert.Contains(WavedFlagValues.FeeUrlFlag, error);
    }

    [Theory]
    [InlineData("https://esplora.example.com/api")]
    [InlineData("http://esplora.example.com:8080/api")] 
    [InlineData("https://user:pass@esplora.example.com/api")]
    [InlineData("https://mempool.space/api")]
    public void PublicUrlsAreAccepted(string url)
    {
        // Credentialed URLs are deliberately accepted: a store owner pointing at a private esplora
        // with an API key in the URL is the normal case that flag is on the allowlist for. The
        // credential is scrubbed from the log line instead (see WavedProcessManager.RedactUserInfo).
        Assert.True(WavedFlagValues.Validate(Flags((WavedFlagValues.EsploraUrlFlag, url)), out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("esplora.example.com/api")]
    [InlineData("ftp://esplora.example.com/api")]
    [InlineData("file:///etc/passwd")]
    // A non-http scheme is refused rather than passed through to waved's own parsing, which would
    // be a second, differently-behaved validator for the same value.
    [InlineData("ws://esplora.example.com")]
    public void NonHttpOrRelativeUrlsAreRejected(string url)
    {
        Assert.False(WavedFlagValues.Validate(Flags((WavedFlagValues.EsploraUrlFlag, url)), out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("10.0.0.1:9735")]
    [InlineData("127.0.0.1:9735")]
    [InlineData("169.254.169.254:80")]
    [InlineData("192.168.0.1:8333")]
    [InlineData("bitcoind.internal:8333")]
    [InlineData("bitcoind:8333")]
    public void LocalOrPrivatePeersAreRejected(string entry)
    {
        Assert.False(WavedFlagValues.Validate(Flags((WavedFlagValues.BtcwalletPeersFlag, entry)), out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void OneLocalPeerInAListRejectsTheWholeList()
    {
        // The check cannot be partial: waved would dial every entry, so accepting the list because
        // most of it is fine would leave the local one reachable.
        Assert.False(WavedFlagValues.Validate(
            Flags((WavedFlagValues.BtcwalletPeersFlag, "peer.example.com:9735,10.0.0.1:9735")), out var error));
        Assert.Contains("10.0.0.1", error);
    }

    [Theory]
    [InlineData("peer.example.com:9735")]
    [InlineData("peer.example.com:9735,other.example.com:9735")]
    [InlineData("203.0.113.10:9735")]
    [InlineData("[2001:db8::1]:9735")]
    public void PublicPeersAreAccepted(string entry)
    {
        Assert.True(WavedFlagValues.Validate(Flags((WavedFlagValues.BtcwalletPeersFlag, entry)), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void AddPeersIsValidatedTheSameWayAsPeers()
    {
        Assert.False(WavedFlagValues.Validate(
            Flags((WavedFlagValues.BtcwalletAddPeersFlag, "10.0.0.1:9735")), out var error));
        Assert.NotNull(error);
        Assert.Contains(WavedFlagValues.BtcwalletAddPeersFlag, error);
    }

    [Theory]
    // No port at all. waved's own pflag parsing would apply its own default here, which would mean
    // this check and waved disagreeing about what was configured - the gap this validation exists
    // to avoid.
    [InlineData("peer.example.com")]
    // An empty *entry* is not asserted here: RemoveEmptyEntries makes a blank value mean "no peers
    // at all" before any entry is validated (see AnEmptyPeerListIsAccepted), so the per-entry
    // contract has nothing to reject. The parsing-level theory below covers it.
    // Port out of range, and a non-numeric port.
    [InlineData("peer.example.com:0")]
    [InlineData("peer.example.com:65536")]
    [InlineData("peer.example.com:abc")]
    // A bare IPv6 literal: without brackets there is no way to tell its last group from a port, so
    // refusing is the only honest answer.
    [InlineData("2001:db8::1:9735")]
    [InlineData(":9735")]
    [InlineData("peer.example.com:")]
    public void MalformedPeerEntriesAreRejected(string entry)
    {
        Assert.False(WavedFlagValues.Validate(Flags((WavedFlagValues.BtcwalletPeersFlag, entry)), out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void AnEmptyPeerListIsAccepted(string entry)
    {
        // An empty list is a legitimate way to say "no extra peers" - there is no address in it to
        // be local, and refusing it would break a connection string that simply clears the field.
        Assert.True(WavedFlagValues.Validate(Flags((WavedFlagValues.BtcwalletPeersFlag, entry)), out var error));
        Assert.Null(error);
    }

    [Fact]
    public void AllowLocalFlipsTheVerdictForUrlsAndPeers()
    {
        // The escape hatch, for an administrator who really does run esplora or a bitcoind peer on
        // their own LAN. Only the connection-string handler passes true, and only with a verified
        // administrator behind it; the flags themselves are otherwise identical.
        var flags = Flags(
            (WavedFlagValues.EsploraUrlFlag, "http://127.0.0.1:5000/api"),
            (WavedFlagValues.FeeUrlFlag, "http://10.0.0.5/fees"),
            (WavedFlagValues.BtcwalletPeersFlag, "192.168.1.20:9735"),
            (WavedFlagValues.BtcwalletAddPeersFlag, "bitcoind.local:8333"));

        Assert.False(WavedFlagValues.Validate(flags, out var rejected, allowLocal: false));
        Assert.NotNull(rejected);

        Assert.True(WavedFlagValues.Validate(flags, out var allowed, allowLocal: true));
        Assert.Null(allowed);
    }

    [Fact]
    public void AllowLocalStillRejectsAMalformedValue()
    {
        // The escape hatch is about locality, not about skipping validation: a peer entry waved
        // could not dial at all is still refused, so an administrator cannot turn off the parsing
        // checks by being an administrator.
        Assert.False(WavedFlagValues.Validate(
            Flags((WavedFlagValues.BtcwalletPeersFlag, "peer.example.com")), out var error, allowLocal: true));
        Assert.NotNull(error);
    }

    [Fact]
    public void TheAdminBypassAndThePersistedMarkerAgreeOnWhatNeedsIt()
    {
        // The two halves of the admin escape hatch have to agree about which flag sets need it. The
        // handler records AllowLocalEndpoints for a set that would have been rejected strictly (see
        // WavelengthLightningConnectionStringHandler.HasLocalFlagValues), and the read paths pass
        // that marker back in as allowLocal; a set this test says "needs the marker" must therefore
        // be exactly the set that fails strict validation and passes the bypass. Getting this wrong
        // in either direction is a security bug (a local value accepted without the marker) or a
        // broken store (a value refused on a page while the process manager starts it).
        var needsMarker = Flags(
            (WavedFlagValues.EsploraUrlFlag, "http://10.0.0.5/api"),
            (WavedFlagValues.BtcwalletPeersFlag, "192.168.1.20:9735"));
        Assert.False(WavedFlagValues.Validate(needsMarker, out _, allowLocal: false));
        Assert.True(WavedFlagValues.Validate(needsMarker, out _, allowLocal: true));

        var doesNotNeedMarker = Flags(
            (WavedFlagValues.EsploraUrlFlag, "https://esplora.example.com/api"),
            (WavedFlagValues.BtcwalletPeersFlag, "peer.example.com:9735"));
        Assert.True(WavedFlagValues.Validate(doesNotNeedMarker, out _, allowLocal: false));
        Assert.True(WavedFlagValues.Validate(doesNotNeedMarker, out _, allowLocal: true));

        // A set with no address-valued flag at all is likewise not a reason to record the marker,
        // so an ordinary store does not accumulate a permission it has no use for.
        Assert.True(WavedFlagValues.Validate(
            Flags(("network", "signet"), ("debuglevel", "info")), out _, allowLocal: false));
    }

    [Fact]
    public void NonAddressFlagsPass()
    {
        // Everything else the allowlist permits is a count, a duration, or an enumeration - there is
        // no address in it, and WavedAllowedFlags has already decided the key may be set at all.
        var flags = Flags(
            ("network", "signet"),
            ("debuglevel", "info"),
            ("allow-mainnet", "true"),
            ("wallet.recoverywindow", "2500"),
            ("oor.limits.maxcheckpoints", "100"));

        Assert.True(WavedFlagValues.Validate(flags, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void AnEmptyFlagSetPasses()
    {
        Assert.True(WavedFlagValues.Validate(new Dictionary<string, string>(), out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("peer.example.com:9735", "peer.example.com", 9735)]
    [InlineData("peer.example.com:1", "peer.example.com", 1)]
    [InlineData("[2001:db8::1]:65535", "[2001:db8::1]", 65535)]
    public void HostPortParsingAcceptsWellFormedEntries(string entry, string expectedHost, int expectedPort)
    {
        Assert.True(WavedFlagValues.TryParseHostPort(entry, out var host, out var port));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    // The parsing-level contract: every shape waved could not dial, whether because the entry has no
    // port, a port outside 1..65535, a non-numeric port, or a bracketed IPv6 address with no port
    // after the bracket. The blank entry is here too, since it is the list parser above - not this
    // method - that decides an empty value means "no peers".
    [InlineData("")]
    [InlineData("peer.example.com")]
    [InlineData("peer.example.com:")]
    [InlineData(":9735")]
    [InlineData("peer.example.com:65536")]
    [InlineData("peer.example.com:abc")]
    [InlineData("[2001:db8::1]")]
    [InlineData("2001:db8::1:9735")]
    public void HostPortParsingRejectsMalformedEntries(string entry)
        => Assert.False(WavedFlagValues.TryParseHostPort(entry, out _, out _));
}
