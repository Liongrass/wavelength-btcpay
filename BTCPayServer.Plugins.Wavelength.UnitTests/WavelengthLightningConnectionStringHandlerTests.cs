using BTCPayServer.Plugins.Wavelength.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

public class WavelengthLightningConnectionStringHandlerTests
{
    // None of these tests construct a WavelengthLightningClient (Create resolves a real
    // WavedProcessManager from the service provider once every check has passed, and no test here
    // gets that far on the accepting path), so a real IServiceProvider is never needed - passing
    // null! is safe. The token protector, unlike the service provider, IS exercised by every test,
    // so it is a real one, keyed by the fake seed store's per-store seeds rather than by any key
    // ring (see WavedStoreTokenProtector for why a token is an HMAC and not ciphertext).
    //
    // There is deliberately no IHttpContextAccessor in the provider: Create's approval and
    // store-binding checks are driven off the ambient request, and these tests are exercising the
    // no-request case, which is exactly what a background consumer (the Lightning listener, a payout
    // processor) presents. The request-present cases are covered by the token protector's own tests
    // and by WavedFlagValuesTests, since building a fake HttpContext with an authenticated
    // ClaimsPrincipal here would test ASP.NET rather than this plugin.
    private readonly FakeTokenSeeds _seeds = new();
    private readonly WavedStoreTokenProtector _tokenProtector;
    private readonly WavelengthLightningConnectionStringHandler _handler;

    public WavelengthLightningConnectionStringHandlerTests()
    {
        _tokenProtector = new WavedStoreTokenProtector(_seeds, NullLogger<WavedStoreTokenProtector>.Instance);

        // Neither the approvals nor the server settings are consulted on any path these tests reach:
        // the approval check is driven off an ambient request context, and there is none here (see
        // the class-level comment). Passing empty implementations keeps that explicit rather than
        // letting a stub that answers "approved" quietly hide a regression in the real check.
        _handler = new WavelengthLightningConnectionStringHandler(
            null!, _tokenProtector, new FakeStoreApprovals(), new FakeServerSettings(), NullLogger<WavelengthLightningConnectionStringHandler>.Instance);
    }

    /// <summary>A token for a store that has one, in the shape Create accepts.</summary>
    private string TokenFor(string storeId)
    {
        _seeds.Seed(storeId, $"seed-for-{storeId}");
        return _tokenProtector
            .GetOrCreateTokenAsync(storeId)
            .GetAwaiter().GetResult();
    }

    [Fact]
    public void IgnoresConnectionStringsOfOtherTypes()
    {
        var client = _handler.Create("type=lnd-rest;server=https://example.com", Network.Main, out var error);

        Assert.Null(client);
        Assert.Null(error);
    }

    [Fact]
    public void RequiresToken()
    {
        var client = _handler.Create("type=wavelength", Network.Main, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
    }

    [Fact]
    public void HintsAtMigrationWhenLeftoverStoreIdKeyIsFound()
    {
        var client = _handler.Create("type=wavelength;store-id=abc", Network.Main, out var error);

        Assert.Null(client);
        Assert.Contains("store-id", error);
    }

    [Fact]
    public void RejectsInvalidToken()
    {
        var client = _handler.Create("type=wavelength;token=not-a-real-token", Network.Main, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
    }

    // H-1's rejection, at the level the store owner actually experiences it: a store that pastes a
    // connection string belonging to a different store is refused while saving it, because core
    // validates a connection string by parsing it through this same Create (see
    // LightningLikePaymentHandler.ValidatePaymentMethodConfig). The token itself is perfectly valid
    // - it is only valid for the store it was issued to.
    [Fact]
    public void RejectsATokenIssuedForADifferentStore()
    {
        var otherStoresToken = TokenFor("store-b");

        // No request context here, which is "no expectation" rather than "any store will do" - so
        // this specific rejection belongs to the token protector (see
        // WavedStoreTokenProtectorTests.ForeignTokenIsRejectedWhenAnotherStoreIsExpected). What this
        // test pins down is the contract that matters to the save path: an unrevoked token for a
        // store that no longer has a matching seed does not produce a client.
        _seeds.Seed("store-b", "a-different-seed");

        var client = _handler.Create($"type=wavelength;token={otherStoresToken}", Network.Main, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
    }

    // A revoked string must stop working no matter who re-reads it - including a background
    // consumer with no request context at all, which is the case that decides whether revoking a
    // token actually revokes anything.
    [Fact]
    public void RejectsARevokedToken()
    {
        var before = TokenFor("store-a");
        var after = _tokenProtector.RegenerateTokenAsync("store-a").GetAwaiter().GetResult();

        Assert.NotEqual(before, after);

        var client = _handler.Create($"type=wavelength;token={before}", Network.Main, out var error);
        Assert.Null(client);
        Assert.NotNull(error);

        // The positive half - that the replacement token is accepted - cannot be asserted through
        // Create here, because acceptance carries on to resolve a real WavedProcessManager from the
        // service provider, which these tests deliberately do not build. The protector's own tests
        // cover it directly (see WavedStoreTokenProtectorTests).
    }

    // A pre-upgrade token was Data Protection ciphertext over a bare store ID (see
    // WavedStoreTokenProtector). It carries no store binding and cannot be revoked, both of which
    // are the finding, so it is refused. The string itself must be realistic ciphertext - an
    // arbitrary string would be rejected for being malformed rather than for being pre-upgrade -
    // which is why one is minted here the way the old code did.
    [Fact]
    public void RejectsAPreUpgradeToken()
    {
        var legacy = new EphemeralDataProtectionProvider()
            .CreateProtector("BTCPayServer.Plugins.Wavelength.StoreToken")
            .Protect("store-a");

        var client = _handler.Create($"type=wavelength;token={legacy}", Network.Main, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
    }

    [Theory]
    // Plugin-owned invariants - never store-owner-settable regardless of allowlist model.
    [InlineData("datadir")]
    [InlineData("rpc.listenaddr")]
    [InlineData("wallet.password_file")]
    [InlineData("rpc.notls")]
    [InlineData("rpc.no-macaroons")]
    [InlineData("rpc.gateway.enabled")]
    [InlineData("rpc.gateway.listenaddr")]
    // Arbitrary-file-write/relocate primitives against this store's own auth material or logs.
    [InlineData("rpc.tlscertpath")]
    [InlineData("rpc.tlskeypath")]
    [InlineData("rpc.macaroonpath")]
    [InlineData("logdir")]
    // Arbitrary-file-read-and-exfiltrate primitives (read a file, send it to an
    // attacker-controlled host the same connection string also sets).
    [InlineData("server.macaroonpath")]
    [InlineData("lnd.macaroonpath")]
    // Cross-store shared-resource / arbitrary-local-file-import primitives.
    [InlineData("swap.databasefilename")]
    [InlineData("wallet.btcwallet_datadir")]
    [InlineData("wallet.btcwallet_blockheaderssource")]
    [InlineData("wallet.btcwallet_filterheaderssource")]
    // Arbitrary listen-address primitives - a wallet process's heap/metrics can leak seed,
    // password, or macaroon material.
    [InlineData("pprof.listen")]
    [InlineData("metrics.listen")]
    // Entire operator-only namespaces this plugin never exposes to a store's own connection
    // string at all, plus a made-up key standing in for any future waved flag nobody has
    // reviewed yet - this is the actual point of an allowlist: unlisted means rejected, not
    // "passed through until someone notices it's dangerous."
    [InlineData("server.host")]
    [InlineData("lnd.host")]
    [InlineData("swap.serveraddress")]
    [InlineData("db.sqlite.synchronous")]
    [InlineData("some-future-waved-flag-nobody-has-reviewed-yet")]
    public void RejectsFlagKeysNotOnTheAllowlist(string disallowedKey)
    {
        // Unlike RequiresToken/RejectsInvalidToken, this one needs a real, current token - Create()
        // checks that before it ever looks at extra flags, so a fake or revoked one would never
        // reach the allowlist check this test is actually exercising.
        var token = TokenFor("store-a");
        var client = _handler.Create($"type=wavelength;token={token};{disallowedKey}=x", Network.Main, out var error);

        Assert.Null(client);
        Assert.Contains(disallowedKey, error);
    }

    // Uses TryParseExtraFlags directly rather than Create() - Create() would go on to resolve a
    // WavedProcessManager from the IServiceProvider once a key is actually allowed, and this test
    // has no interest in faking one out just to reach that point; TryParseExtraFlags is the exact
    // gate being exercised here regardless.
    // The value-level half: a key the allowlist permits can still be set to something waved would
    // then connect to on the server's behalf, so TryParseExtraFlags applies WavedFlagValues too. See
    // WavedFlagValuesTests for the rules themselves.
    [Theory]
    [InlineData("wallet.esploraurl", "http://169.254.169.254/latest/meta-data/")]
    [InlineData("wallet.esploraurl", "http://127.0.0.1:10029/")]
    [InlineData("wallet.esploraurl", "http://10.1.2.3:8080/api")]
    [InlineData("wallet.esploraurl", "not a url")]
    [InlineData("wallet.feeurl", "http://169.254.169.254/")]
    [InlineData("wallet.btcwallet_peers", "10.0.0.1:9735")]
    [InlineData("wallet.btcwallet_addpeers", "127.0.0.1:9735")]
    [InlineData("wallet.btcwallet_peers", "peer.example.com")]
    public void RejectsAllowlistedFlagKeysWithDisallowedValues(string key, string value)
    {
        var ok = WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
            $"type=wavelength;token=abc;{key}={value}", out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains(key, error);
    }

    [Theory]
    [InlineData("wallet.esploraurl", "https://esplora.example.com/api")]
    [InlineData("wallet.feeurl", "https://fees.example.com/")]
    [InlineData("wallet.btcwallet_peers", "peer.example.com:9735")]
    [InlineData("wallet.btcwallet_addpeers", "peer.example.com:9735,other.example.com:9735")]
    public void AcceptsAllowlistedFlagKeysWithAllowedValues(string key, string value)
    {
        var ok = WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
            $"type=wavelength;token=abc;{key}={value}", out var extraFlags, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(value, extraFlags[key]);
    }

    [Fact]
    public void AllowLocalFlipsTheValueVerdictForAnAdministratorsSave()
    {
        // The escape hatch only ever gets passed by the connection-string handler with a verified
        // administrator behind it, which is what a LAN esplora depends on.
        const string connectionString = "type=wavelength;token=abc;wallet.esploraurl=http://127.0.0.1:5000/api";

        Assert.False(WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
            connectionString, out _, out _));
        Assert.True(WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
            connectionString, out var extraFlags, out var error, allowLocal: true));
        Assert.Null(error);
        Assert.Equal("http://127.0.0.1:5000/api", extraFlags["wallet.esploraurl"]);
    }

    // Each key carries a value that passes its own check as well as the key allowlist: the two
    // URL-shaped keys need a real public URL, and everything else is a plain count/duration/enum.
    [Theory]
    [InlineData("network", "1")]
    [InlineData("debuglevel", "1")]
    [InlineData("allow-mainnet", "1")]
    [InlineData("maxoperatorfeesat", "1")]
    [InlineData("wallet.type", "1")]
    [InlineData("wallet.esploraurl", "https://esplora.example.com/api")]
    [InlineData("wallet.feeurl", "https://fees.example.com/")]
    [InlineData("wallet.recoverywindow", "1")]
    [InlineData("oor.maxsubmitretry", "1")]
    [InlineData("oor.limits.maxcheckpoints", "1")]
    [InlineData("unroll.bumpafterblocks", "1")]
    public void AllowsFlagKeysOnTheAllowlist(string allowedKey, string value)
    {
        var ok = WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
            $"type=wavelength;token=abc;{allowedKey}={value}", out var extraFlags, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(value, extraFlags[allowedKey]);
    }
}
