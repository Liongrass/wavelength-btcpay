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

    [Fact]
    public void BuildFlagArgumentsCombinesEachKeyAndValueIntoOneToken()
    {
        var flags = new Dictionary<string, string?>
        {
            ["network"] = "signet",
            ["allow-mainnet"] = "true"
        };

        var args = WavedProcessManager.BuildFlagArguments(flags).ToArray();

        Assert.Equal(["--network=signet", "--allow-mainnet=true"], args);
    }

    [Fact]
    public void BuildFlagArgumentsOmitsTheValueTokenEntirelyWhenNull()
    {
        var flags = new Dictionary<string, string?> { ["eagerroundjoin"] = null };

        Assert.Equal(["--eagerroundjoin"], WavedProcessManager.BuildFlagArguments(flags).ToArray());
    }

    // Regression test for the pflag bool-flag argument-injection bypass: pflag/cobra bool flags
    // don't consume the next argv token as their value, so if a bool-typed allowlisted key's
    // "value" were ever emitted as a SEPARATE token, waved's own flag parser would parse it as an
    // entirely independent, unvalidated flag - completely bypassing WavedAllowedFlags. Asserting
    // the whole thing lands in one array element (not two) is the actual safety property; the
    // exact string is secondary.
    [Fact]
    public void BuildFlagArgumentsNeverSplitsAnInjectionAttemptIntoASeparateToken()
    {
        var flags = new Dictionary<string, string?>
        {
            ["allow-mainnet"] = "--rpc.macaroonpath=/some/other/store/admin.macaroon"
        };

        var args = WavedProcessManager.BuildFlagArguments(flags).ToArray();

        Assert.Equal(["--allow-mainnet=--rpc.macaroonpath=/some/other/store/admin.macaroon"], args);
    }

    [Fact]
    public void FlagsEqualTreatsNullPersistedAsEqualOnlyToEmptyCurrent()
    {
        Assert.True(WavedProcessManager.FlagsEqual(null, new Dictionary<string, string>()));
        Assert.False(WavedProcessManager.FlagsEqual(null, new Dictionary<string, string> { ["network"] = "signet" }));
    }

    [Fact]
    public void FlagsEqualComparesKeysCaseInsensitivelyAndValuesCaseSensitively()
    {
        var persisted = new Dictionary<string, string> { ["Network"] = "signet" };
        var sameValue = new Dictionary<string, string> { ["network"] = "signet" };
        var differentValueCase = new Dictionary<string, string> { ["network"] = "Signet" };

        Assert.True(WavedProcessManager.FlagsEqual(persisted, sameValue));
        Assert.False(WavedProcessManager.FlagsEqual(persisted, differentValueCase));
    }

    [Fact]
    public void FlagsEqualIsFalseWhenCountsOrContentsDiffer()
    {
        var persisted = new Dictionary<string, string> { ["network"] = "signet" };

        Assert.False(WavedProcessManager.FlagsEqual(persisted, new Dictionary<string, string>()));
        Assert.False(WavedProcessManager.FlagsEqual(persisted, new Dictionary<string, string> { ["network"] = "mainnet" }));
        Assert.False(WavedProcessManager.FlagsEqual(persisted,
            new Dictionary<string, string> { ["network"] = "signet", ["debuglevel"] = "info" }));
    }
}
