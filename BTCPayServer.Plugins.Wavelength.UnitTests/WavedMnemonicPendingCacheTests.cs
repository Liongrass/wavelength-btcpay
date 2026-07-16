using BTCPayServer.Plugins.Wavelength.Services;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

public class WavedMnemonicPendingCacheTests
{
    [Fact]
    public void PeekReturnsStoredValueRepeatedlyUntilAcknowledged()
    {
        var cache = new WavedMnemonicPendingCache();
        cache.Store("store1", "word1 word2 word3");

        Assert.Equal("word1 word2 word3", cache.Peek("store1"));
        Assert.Equal("word1 word2 word3", cache.Peek("store1"));
        Assert.True(cache.HasPending("store1"));

        cache.Acknowledge("store1");

        Assert.Null(cache.Peek("store1"));
        Assert.False(cache.HasPending("store1"));
    }

    [Fact]
    public void PeekReturnsNullForUnknownStore()
    {
        var cache = new WavedMnemonicPendingCache();

        Assert.Null(cache.Peek("never-stored"));
        Assert.False(cache.HasPending("never-stored"));
    }

    [Fact]
    public void AcknowledgeIsSafeToCallWithoutAPendingMnemonic()
    {
        var cache = new WavedMnemonicPendingCache();

        cache.Acknowledge("never-stored");
    }
}
