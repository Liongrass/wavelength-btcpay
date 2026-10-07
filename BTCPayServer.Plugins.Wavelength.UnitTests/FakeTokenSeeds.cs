using BTCPayServer.Plugins.Wavelength.Services;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

/// <summary>
/// An in-memory <see cref="IWavedTokenSeeds"/> backed by a plain dictionary, so the token format,
/// store binding and revocation rules can be exercised without a database. Seeding a store's entry
/// directly is how a test says "this store was already issued a token".
/// </summary>
public sealed class FakeTokenSeeds : IWavedTokenSeeds
{
    private readonly Dictionary<string, string> _seeds = new();

    /// <summary>How many times a seed was generated. Lets a test assert a read did not mint one.</summary>
    public int GenerationCount { get; private set; }

    public void Seed(string storeId, string seed) => _seeds[storeId] = seed;

    public string? GetSeed(string storeId) => _seeds.TryGetValue(storeId, out var seed) ? seed : null;

    public Task<string> GetOrCreateSeedAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (_seeds.TryGetValue(storeId, out var existing))
            return Task.FromResult(existing);

        return Task.FromResult(SeedNew(storeId));
    }

    public Task<string> RegenerateSeedAsync(string storeId, CancellationToken cancellationToken = default)
        => Task.FromResult(SeedNew(storeId));

    private string SeedNew(string storeId)
    {
        GenerationCount++;
        var seed = $"seed-{GenerationCount}";
        _seeds[storeId] = seed;
        return seed;
    }
}
