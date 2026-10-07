using BTCPayServer.Plugins.Wavelength.Services;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

/// <summary>
/// An in-memory <see cref="IWavedStoreApprovals"/>, so WavelengthLightningConnectionStringHandler's
/// save-time checks can be exercised without a database. Starts with no store approved, which is the
/// state that matters: a fresh store whose owner is not an administrator.
/// </summary>
public sealed class FakeStoreApprovals : IWavedStoreApprovals
{
    private readonly Dictionary<string, bool> _approved = new();
    private readonly Dictionary<string, bool> _allowLocalEndpoints = new();

    /// <summary>How many times an approval was recorded, so a test can assert a read wrote nothing.</summary>
    public int WriteCount { get; private set; }

    public void Approve(string storeId, bool allowLocalEndpoints = false)
    {
        _approved[storeId] = true;
        _allowLocalEndpoints[storeId] = allowLocalEndpoints;
    }

    public bool IsApproved(string storeId) => _approved.ContainsKey(storeId);

    public bool AllowsLocalEndpoints(string storeId) => _allowLocalEndpoints.TryGetValue(storeId, out var v) && v;

    public Task ApproveAsync(string storeId, bool allowLocalEndpoints, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        Approve(storeId, allowLocalEndpoints || AllowsLocalEndpoints(storeId));
        return Task.CompletedTask;
    }
}

/// <summary>
/// An in-memory <see cref="IWavelengthServerSettingsSource"/>. Defaults to a fresh
/// <see cref="WavelengthServerSettings"/> - AllowForAllStores false and the process cap at its
/// default - which is what a server that has never touched these settings presents.
/// </summary>
public sealed class FakeServerSettings(WavelengthServerSettings? settings = null) : IWavelengthServerSettingsSource
{
    private readonly WavelengthServerSettings _settings = settings ?? new WavelengthServerSettings();

    public Task<WavelengthServerSettings> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_settings);
}
