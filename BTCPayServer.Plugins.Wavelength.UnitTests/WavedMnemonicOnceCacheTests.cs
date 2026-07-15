using BTCPayServer.Plugins.Wavelength.Services;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

public class WavedMnemonicOnceCacheTests
{
    [Fact]
    public void TakeOnceReturnsStoredValueThenNull()
    {
        var cache = new WavedMnemonicOnceCache();
        cache.Store("store1", "word1 word2 word3");

        Assert.Equal("word1 word2 word3", cache.TakeOnce("store1"));
        Assert.Null(cache.TakeOnce("store1"));
    }

    [Fact]
    public void TakeOnceReturnsNullForUnknownStore()
    {
        var cache = new WavedMnemonicOnceCache();

        Assert.Null(cache.TakeOnce("never-stored"));
    }
}
