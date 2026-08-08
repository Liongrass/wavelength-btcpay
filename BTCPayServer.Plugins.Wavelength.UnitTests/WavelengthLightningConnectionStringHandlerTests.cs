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
    [InlineData("datadir")]
    [InlineData("rpc.listenaddr")]
    [InlineData("wallet.password_file")]
    [InlineData("rpc.notls")]
    [InlineData("rpc.no-macaroons")]
    public void RejectsReservedFlagKeys(string reservedKey)
    {
        // Unlike RequiresToken/RejectsInvalidToken, this one needs a real, validly-protected
        // token - Create() checks that before it ever looks at extra flags, so a fake one would
        // never reach the reserved-flags check this test is actually exercising.
        var token = _tokenProtector.Protect("abc");
        var client = _handler.Create($"type=wavelength;token={token};{reservedKey}=x", Network.Main, out var error);

        Assert.Null(client);
        Assert.Contains(reservedKey, error);
    }
}
