using BTCPayServer.Plugins.Wavelength.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

/// <summary>
/// The token's three security properties, and the reason each exists: a token is bound to the store
/// it was issued for (so possessing one store's string cannot be turned into access to another's
/// wallet), a token is revocable (so a leaked string can be killed without rotating the instance key
/// ring), and a token cannot be minted for a store without server-side access (so a store owner
/// cannot forge one). See WavedStoreTokenProtector for the design.
/// </summary>
public class WavedStoreTokenProtectorTests
{
    private readonly FakeTokenSeeds _seeds = new();

    private WavedStoreTokenProtector NewProtector() =>
        new(_seeds, NullLogger<WavedStoreTokenProtector>.Instance);

    [Fact]
    public async Task StoreCanUseItsOwnToken()
    {
        var protector = NewProtector();
        var token = await protector.GetOrCreateTokenAsync("store-a");

        Assert.True(protector.TryResolve(token, "store-a", out var storeId, out var error));
        Assert.Equal("store-a", storeId);
        Assert.Null(error);
    }

    [Fact]
    public async Task TokenIsStableAcrossCalls()
    {
        var protector = NewProtector();
        var first = await protector.GetOrCreateTokenAsync("store-a");
        var second = await protector.GetOrCreateTokenAsync("store-a");

        // The point of an HMAC over the store ID rather than encrypting "storeId|seed": Data
        // Protection's Protect mixes in a fresh nonce per call, so a token minted on every page
        // render would be a different string each time, and the operator could not tell the string
        // they saved from one that had since been superseded.
        Assert.Equal(first, second);

        // And stable without minting a second seed, which is what makes the second call cheap.
        Assert.Equal(1, _seeds.GenerationCount);
    }

    [Fact]
    public async Task ForeignTokenIsRejectedWhenAnotherStoreIsExpected()
    {
        // H-1 in one test: store A's owner pastes store B's connection string. The token is
        // genuinely valid, freshly minted, and unrevoked - and it still must not resolve for A.
        var protector = NewProtector();
        var storeBToken = await protector.GetOrCreateTokenAsync("store-b");

        Assert.False(protector.TryResolve(storeBToken, "store-a", out var storeId, out var error));
        Assert.Equal("", storeId);
        Assert.NotNull(error);

        // And B's own token is, of course, still fine for B - the rejection is about the mismatch,
        // not about the token.
        Assert.True(protector.TryResolve(storeBToken, "store-b", out _, out _));
    }

    [Fact]
    public async Task TokenResolvesWithoutAnExpectedStoreForBackgroundConsumers()
    {
        // A background consumer (the Lightning listener, a payout processor) builds a client from a
        // store's own saved connection string and has no request context to check against. Null
        // means "no expectation", and the token still has to name a store with a current seed.
        var protector = NewProtector();
        var token = await protector.GetOrCreateTokenAsync("store-a");

        Assert.True(protector.TryResolve(token, null, out var storeId, out _));
        Assert.Equal("store-a", storeId);
    }

    [Fact]
    public async Task RegeneratingATokenInvalidatesTheOldOneAndOnlyThatStore()
    {
        var protector = NewProtector();
        var oldTokenA = await protector.GetOrCreateTokenAsync("store-a");
        var tokenB = await protector.GetOrCreateTokenAsync("store-b");

        var newTokenA = await protector.RegenerateTokenAsync("store-a");

        // The revoke. This is the whole reason a seed is persisted per store: without a value to
        // compare the embedded seed against, the only way to invalidate oldTokenA would be rotating
        // Data Protection's key ring, which would invalidate tokenB and every other store's token
        // and wallet password at the same time.
        Assert.False(protector.TryResolve(oldTokenA, "store-a", out _, out var revokedError));
        Assert.NotNull(revokedError);
        Assert.False(protector.TryResolve(oldTokenA, null, out _, out _));

        // The replacement works...
        Assert.True(protector.TryResolve(newTokenA, "store-a", out _, out _));
        Assert.NotEqual(oldTokenA, newTokenA);

        // ...and no other store was touched.
        Assert.True(protector.TryResolve(tokenB, "store-b", out _, out _));
    }

    [Fact]
    public async Task RegeneratedTokenIsAlsoStable()
    {
        var protector = NewProtector();
        await protector.GetOrCreateTokenAsync("store-a");
        var regenerated = await protector.RegenerateTokenAsync("store-a");

        Assert.Equal(regenerated, await protector.GetOrCreateTokenAsync("store-a"));
    }

    [Fact]
    public void TokenForAStoreWithNoSeedIsRejected()
    {
        // A well-formed token - right shape, real-looking MAC - for a store that has never been
        // issued one. There is no seed to key the MAC with, so nothing can verify it: this is the
        // case of a token replayed from a database snapshot taken before the store's row existed,
        // or of a store whose seed was cleared. It must not resolve.
        var protector = NewProtector();
        var forged = "store-a.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        Assert.False(protector.TryResolve(forged, "store-a", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ForgedMacCannotBeAssembledFromAKnownStoreAndToken()
    {
        // The property that makes the store ID safe to expose: a store owner who has a valid token
        // for their own store still cannot mint one for another store, because doing so needs the
        // other store's seed, which never appears in any token. Swapping the ID and reusing the MAC
        // - the obvious attempt - fails, since the ID is inside the signed message.
        var protector = NewProtector();
        var tokenA = await protector.GetOrCreateTokenAsync("store-a");
        await protector.GetOrCreateTokenAsync("store-b");

        WavedStoreTokenProtector.TrySplitPayload(tokenA, out _, out var macA);
        var forgedForB = $"store-b{macA}";

        Assert.False(protector.TryResolve(forgedForB, "store-b", out _, out _));
        Assert.False(protector.TryResolve(forgedForB, null, out _, out _));
    }

    [Fact]
    public async Task TokenCarryingAStaleSeedIsRejected()
    {
        // The embedded seed no longer matches the store's persisted one - which is exactly the state
        // a revoked token is in, reached here by writing the seed rather than by rotating through
        // RegenerateToken.
        var protector = NewProtector();
        var token = await protector.GetOrCreateTokenAsync("store-a");
        _seeds.Seed("store-a", "a-different-seed");

        Assert.False(protector.TryResolve(token, "store-a", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task PreUpgradeTokenWithoutASeedIsRejected()
    {
        // The old format: Data Protection ciphertext over a bare store ID, which contains no
        // separator at all. Accepting it would mean accepting a token with no store binding and no
        // revocation, which is the finding - so it is refused, with a message that points the
        // operator at generating a fresh string.
        var protector = NewProtector();
        await protector.GetOrCreateTokenAsync("store-a");
        var legacy = new EphemeralDataProtectionProvider()
            .CreateProtector("BTCPayServer.Plugins.Wavelength.StoreToken")
            .Protect("store-a");

        Assert.False(protector.TryResolve(legacy, "store-a", out _, out var error));
        Assert.Contains("older version", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("!!!")]
    public void MalformedTokensAreRejected(string token)
    {
        var protector = NewProtector();

        Assert.False(protector.TryResolve(token, "store-a", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task TokenWhoseSeedDoesNotMatchThisInstancesSeedIsRejected()
    {
        // The same store ID and the same token, against a protector whose store has a *different*
        // seed - which is what a token from a completely separate server looks like, and also what
        // a database-only restore that lost the store's settings row looks like. Since the MAC is
        // keyed by the seed, a token from elsewhere cannot verify here.
        var token = await NewProtector().GetOrCreateTokenAsync("store-a");

        var otherSeeds = new FakeTokenSeeds();
        otherSeeds.Seed("store-a", "a-completely-different-seed");
        var otherInstance = new WavedStoreTokenProtector(otherSeeds, NullLogger<WavedStoreTokenProtector>.Instance);

        Assert.False(otherInstance.TryResolve(token, "store-a", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SplittingAPayloadKeepsTheFirstSeparatorOnly()
    {
        // Store IDs are opaque and this plugin never assumes anything about their contents, so the
        // split has to be defensively correct rather than incidentally so: everything after the
        // first separator is the seed, which cannot be used to shift the boundary.
        Assert.True(WavedStoreTokenProtector.TrySplitPayload("store.mac", out var storeId, out var mac));
        Assert.Equal("store", storeId);
        Assert.Equal("mac", mac);

        Assert.True(WavedStoreTokenProtector.TrySplitPayload("store.mac.more", out storeId, out mac));
        Assert.Equal("store", storeId);
        Assert.Equal("mac.more", mac);
    }

    [Theory]
    [InlineData("")]
    [InlineData("store-only")]
    [InlineData(".mac")]
    [InlineData("store.")]
    [InlineData(".")]
    public void PayloadsWithoutBothHalvesAreRejected(string payload)
        => Assert.False(WavedStoreTokenProtector.TrySplitPayload(payload, out _, out _));
}
