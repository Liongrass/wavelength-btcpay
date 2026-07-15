using BTCPayServer.Plugins.Wavelength.Lightning;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

public class WavelengthLightningConnectionStringHandlerTests
{
    // Neither branch below constructs a WavelengthLightningClient, so a real IServiceProvider
    // is never needed - passing null! is safe here.
    private readonly WavelengthLightningConnectionStringHandler _handler = new(null!);

    [Fact]
    public void IgnoresConnectionStringsOfOtherTypes()
    {
        var client = _handler.Create("type=lnd-rest;server=https://example.com", Network.Main, out var error);

        Assert.Null(client);
        Assert.Null(error);
    }

    [Fact]
    public void RequiresStoreId()
    {
        var client = _handler.Create("type=wavelength", Network.Main, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("datadir")]
    [InlineData("rpc.listenaddr")]
    [InlineData("wallet.password_file")]
    [InlineData("no-tls")]
    [InlineData("no-macaroons")]
    public void RejectsReservedFlagKeys(string reservedKey)
    {
        // Neither this nor RequiresStoreId construct a WavelengthLightningClient (both return
        // before reaching ActivatorUtilities.CreateInstance), so null! is safe here too.
        var client = _handler.Create($"type=wavelength;store-id=abc;{reservedKey}=x", Network.Main, out var error);

        Assert.Null(client);
        Assert.Contains(reservedKey, error);
    }
}
