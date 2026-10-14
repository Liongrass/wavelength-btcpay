using BTCPayServer.Plugins.Wavelength.Services;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

public class WavedOperatorServerTests
{
    [Theory]
    [InlineData("mainnet", "https://wavelength.lightning.finance:443")]
    [InlineData("testnet", "https://test.wavelength.lightning.finance:443")]
    [InlineData("testnet4", "https://lumosd-testnet4.testnet.lightningcluster.com:443")]
    [InlineData("signet", "https://signet.wavelength.lightning.finance:443")]
    [InlineData("regtest", "https://localhost:10010")]
    [InlineData("simnet", "https://localhost:10010")]
    [InlineData("MAINNET", "https://wavelength.lightning.finance:443")]
    public void DefaultForMatchesWavedsOwnNetworkDefaults(string network, string expected)
    {
        Assert.Equal(expected, WavedOperatorServer.DefaultFor(network));
    }

    [Theory]
    [InlineData("https://wavelength.lightning.finance:443", "wavelength.lightning.finance:443")]
    [InlineData("HTTPS://wavelength.lightning.finance:443", "wavelength.lightning.finance:443")]
    [InlineData("http://localhost:10010", "localhost:10010")]
    [InlineData("wavelength.lightning.finance:443", "wavelength.lightning.finance:443")]
    public void ToWavedFlagValueStripsAnyHttpSchemeWavedsGrpcDialerCannotUse(string connectionStringValue, string expected)
    {
        Assert.Equal(expected, WavedOperatorServer.ToWavedFlagValue(connectionStringValue));
    }
}
