using BTCPayServer.Plugins.Wavelength.Services;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

public class WavedProcessManagerTests
{
    [Theory]
    [InlineData("https://user:pass@esplora.example.com/api", "https://***@esplora.example.com/api")]
    [InlineData("https://apikey:@esplora.example.com/api", "https://***@esplora.example.com/api")]
    public void RedactUserInfoScrubsCredentialsFromUrls(string value, string expected)
    {
        Assert.Equal(expected, WavedProcessManager.RedactUserInfo(value));
    }

    [Theory]
    [InlineData("signet")]
    [InlineData("btcwallet")]
    [InlineData("https://esplora.example.com/api")]
    [InlineData("not a url at all")]
    public void RedactUserInfoLeavesCredentialFreeValuesUnchanged(string value)
    {
        Assert.Equal(value, WavedProcessManager.RedactUserInfo(value));
    }
}
