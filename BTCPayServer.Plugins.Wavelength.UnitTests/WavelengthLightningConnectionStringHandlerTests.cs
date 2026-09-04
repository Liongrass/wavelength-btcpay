using BTCPayServer.Plugins.Wavelength.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using Microsoft.AspNetCore.DataProtection;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

public class WavelengthLightningConnectionStringHandlerTests
{
    // Neither branch below constructs a WavelengthLightningClient, so a real IServiceProvider is
    // never needed - passing null! is safe there. The token protector, unlike the service
    // provider, IS exercised by every test (Create validates the token before anything else), so
    // it's a real one backed by an ephemeral (test-only, non-persisted) key ring.
    private readonly WavedStoreTokenProtector _tokenProtector = new(new EphemeralDataProtectionProvider());
    private readonly WavelengthLightningConnectionStringHandler _handler;

    public WavelengthLightningConnectionStringHandlerTests()
    {
        _handler = new WavelengthLightningConnectionStringHandler(null!, _tokenProtector);
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
        // Unlike RequiresToken/RejectsInvalidToken, this one needs a real, validly-protected
        // token - Create() checks that before it ever looks at extra flags, so a fake one would
        // never reach the allowlist check this test is actually exercising.
        var token = _tokenProtector.Protect("abc");
        var client = _handler.Create($"type=wavelength;token={token};{disallowedKey}=x", Network.Main, out var error);

        Assert.Null(client);
        Assert.Contains(disallowedKey, error);
    }

    // Uses TryParseExtraFlags directly rather than Create() - Create() would go on to resolve a
    // WavedProcessManager from the IServiceProvider once a key is actually allowed, and this test
    // has no interest in faking one out just to reach that point; TryParseExtraFlags is the exact
    // gate being exercised here regardless.
    [Theory]
    [InlineData("network")]
    [InlineData("debuglevel")]
    [InlineData("allow-mainnet")]
    [InlineData("maxoperatorfeesat")]
    [InlineData("wallet.type")]
    [InlineData("wallet.esploraurl")]
    [InlineData("wallet.feeurl")]
    [InlineData("wallet.recoverywindow")]
    [InlineData("oor.maxsubmitretry")]
    [InlineData("oor.limits.maxcheckpoints")]
    [InlineData("unroll.bumpafterblocks")]
    public void AllowsFlagKeysOnTheAllowlist(string allowedKey)
    {
        var ok = WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
            $"type=wavelength;token=abc;{allowedKey}=1", out var extraFlags, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("1", extraFlags[allowedKey]);
    }
}
