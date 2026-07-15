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
}
