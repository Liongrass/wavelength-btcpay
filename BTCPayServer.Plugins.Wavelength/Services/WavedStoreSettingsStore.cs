using System.Security.Cryptography;
using System.Collections.Concurrent;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// The token-seed operations WavedStoreTokenProtector needs, separated from how they are stored so
/// the protector's format and comparison logic can be exercised without a database.
/// </summary>
public interface IWavedTokenSeeds
{
    /// <summary>
    /// This store's current seed, or null if it has none yet (no token was ever issued for it).
    /// Synchronous by necessity - see WavedStoreSettingsStore.GetSeed for why, and for the
    /// guarantee that makes it cheap on the hot path.
    /// </summary>
    string? GetSeed(string storeId);

    /// <summary>This store's seed, generating and persisting one first if it has none.</summary>
    Task<string> GetOrCreateSeedAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces this store's seed with a fresh random one, which is what revokes every token
    /// previously issued for it. Returns the new seed.
    /// </summary>
    Task<string> RegenerateSeedAsync(string storeId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The two approval facts WavelengthLightningConnectionStringHandler needs about a store - just
/// these, rather than the whole settings row, because they are all the handler ever looks at and a
/// narrower interface is what lets its save-time checks be exercised without a database. The
/// implementation is WavedStoreSettingsStore, which owns the underlying persistence.
/// </summary>
public interface IWavedStoreApprovals
{
    /// <summary>
    /// Whether this store has already been adopted for Wavelength by someone entitled to adopt it.
    /// Synchronous for the same reason GetSeed is: the caller is the connection-string handler,
    /// which is synchronous by BTCPay's own interface, and this is a cached read.
    /// </summary>
    bool IsApproved(string storeId);

    /// <summary>
    /// Records that this store has been adopted, and whether it may carry local/private endpoint
    /// values. Called on every accepted save, so the implementation is responsible for writing
    /// nothing when neither fact would change - an ordinary Lightning operation reaches this on the
    /// hottest path there is.
    /// </summary>
    Task ApproveAsync(string storeId, bool allowLocalEndpoints, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single gateway to a store's <see cref="WavedStoreSettings"/> row.
///
/// Why a gateway rather than letting each caller talk to StoreRepository: the row is read on
/// BTCPay's hottest Lightning path. WavelengthLightningConnectionStringHandler.Create is called by
/// core for *every* Lightning operation a store performs - creating an invoice included, including
/// the public, unauthenticated checkout path - and this plugin's store-binding check (see
/// WavedStoreTokenProtector) needs the store's seed on each of those calls. Re-reading the row from
/// the database every time would put a query on that path for no reason. So reads are cached in
/// memory per store and writes go through this class, which keeps the cache in step with what it
/// just wrote.
///
/// The cache is deliberately not consulted on the way *into* a write: a write re-reads the row from
/// the database first, so a field changed by anyone else since this process last looked (a field
/// this plugin itself does not always be the one to change - WavedWalletCredentialStore owns
/// EncryptedWalletPassword, WavedProcessManager owns ExtraWavedFlags, and WavedStoreTokenProtector
/// owns TokenSeed and the two approval markers) is carried forward rather than reverted by a
/// record-copy built from a stale snapshot.
///
/// The cache is per process, which assumes the usual single BTCPay Server instance per
/// database: a seed regenerated from a second instance sharing one database would still take
/// effect there on its next read of the row, but this process would keep serving its cached
/// copy until restarted. BTCPay core makes the same assumption for comparable plugin state,
/// and a multi-instance deployment of this plugin would have bigger problems - each instance
/// would spawn its own waved process per store, on ports only it has reserved.
/// </summary>
public sealed class WavedStoreSettingsStore(StoreRepository storeRepository) : IWavedTokenSeeds, IWavedStoreApprovals
{
    private const int SeedEntropyBytes = 32;

    private readonly ConcurrentDictionary<string, WavedStoreSettings> _cache = new();

    /// <summary>
    /// This store's settings, from cache when possible. A miss reads the row (and caches it),
    /// treating "no row yet" as a store with no password, seed, flags, or approval - which is
    /// exactly what a store that has never used this plugin has.
    /// </summary>
    public async Task<WavedStoreSettings> GetAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(storeId, out var cached))
            return cached;

        var settings = await ReadAsync(storeId, cancellationToken);
        _cache[storeId] = settings;
        return settings;
    }

    /// <summary>
    /// Reads the row, bypassing the cache - for a caller that is about to write and must not build
    /// its replacement record from a possibly stale snapshot.
    /// </summary>
    public async Task<WavedStoreSettings> ReadAsync(string storeId, CancellationToken cancellationToken = default)
        => await storeRepository.GetSettingAsync<WavedStoreSettings>(storeId, WavedStoreSettings.SettingsKey)
           ?? new WavedStoreSettings();

    /// <summary>
    /// Applies <paramref name="update"/> to the store's current settings and persists the result.
    /// The update function is handed a freshly read record, not a cached one, for the reason in this
    /// class's doc comment.
    /// </summary>
    public async Task<WavedStoreSettings> UpdateAsync(
        string storeId, Func<WavedStoreSettings, WavedStoreSettings> update, CancellationToken cancellationToken = default)
    {
        var updated = update(await ReadAsync(storeId, cancellationToken));
        await storeRepository.UpdateSetting(storeId, WavedStoreSettings.SettingsKey, updated);
        _cache[storeId] = updated;
        return updated;
    }

    /// <summary>
    /// This store's token seed, or null if none has been issued.
    ///
    /// Synchronous on purpose, and this is the only place in the plugin that blocks on the
    /// database. WavelengthLightningConnectionStringHandler.Create is synchronous (it implements
    /// BTCPay's own ILightningConnectionStringHandler, which is), while verifying a token needs a
    /// value that lives in the store's settings row - there is no asynchronous form of that
    /// comparison to offer, and answering "unknown" instead of "no" would fail *closed* on a cold
    /// cache, rejecting a store's own valid token. Blocking an EF Core read does not deadlock here:
    /// ASP.NET Core installs no SynchronizationContext, so there is nothing for the continuation to
    /// be captured by. What keeps it off the hot path is the cache above - the wait happens once
    /// per store per process, and every later call (every invoice, every payout, every poll) is a
    /// dictionary lookup.
    /// </summary>
    public string? GetSeed(string storeId)
    {
        if (_cache.TryGetValue(storeId, out var cached))
            return cached.TokenSeed;

        var settings = storeRepository
            .GetSettingAsync<WavedStoreSettings>(storeId, WavedStoreSettings.SettingsKey)
            .GetAwaiter().GetResult() ?? new WavedStoreSettings();
        _cache[storeId] = settings;
        return settings.TokenSeed;
    }
    public async Task<string> GetOrCreateSeedAsync(string storeId, CancellationToken cancellationToken = default)
    {
        if (GetSeed(storeId) is { Length: > 0 } existing)
            return existing;

        var generated = NewSeed();
        await UpdateAsync(storeId, settings => settings.TokenSeed is { Length: > 0 } present
            ? settings
            : settings with { TokenSeed = generated }, cancellationToken);

        // A concurrent caller may have won the race and persisted its own seed; re-read so the
        // caller and the store's token always agree on which seed is current.
        return GetSeed(storeId) ?? generated;
    }

    public async Task<string> RegenerateSeedAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var generated = NewSeed();
        await UpdateAsync(storeId, settings => settings with { TokenSeed = generated }, cancellationToken);
        return generated;
    }

    private static string NewSeed() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(SeedEntropyBytes));

    /// <summary>See <see cref="IWavedStoreApprovals.IsApproved"/> - a cached read, since this is
    /// asked on every connection string this store parses.</summary>
    public bool IsApproved(string storeId)
        => GetAsync(storeId).GetAwaiter().GetResult().ServerAdminApproved;

    /// <summary>See <see cref="IWavedStoreApprovals.ApproveAsync"/> - writes only when one of the two
    /// facts actually changes, so the unauthenticated invoice path (which reaches the handler with a
    /// request context attached, and therefore reaches this) stops at an in-memory comparison.</summary>
    public async Task ApproveAsync(
        string storeId, bool allowLocalEndpoints, CancellationToken cancellationToken = default)
    {
        var settings = await GetAsync(storeId, cancellationToken);

        // Once approved, always approved: this records that a decision was made, and a later save by
        // a store owner must not be able to clear the record by not being an admin. AllowLocalEndpoints
        // accumulates for the same reason - an admin who allowed local endpoints and then saves a
        // string without any must not drop the marker out from under a process already using one.
        var allowLocal = settings.AllowLocalEndpoints || allowLocalEndpoints;
        if (settings.ServerAdminApproved && settings.AllowLocalEndpoints == allowLocal)
            return;

        await UpdateAsync(storeId, current => current with
        {
            ServerAdminApproved = true,
            AllowLocalEndpoints = current.AllowLocalEndpoints || allowLocalEndpoints
        }, cancellationToken);
    }
}
