using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.Wavelength.Lightning;

/// <summary>
/// Resolves "type=wavelength;store-id=...;(extra waved flags)" connection strings to a per-store
/// WavelengthLightningClient. Unlike LND/CLN-style handlers, the connection string carries no
/// externally-meaningful address - waved is plugin-managed per store, so store-id is just a
/// lookup key into WavedProcessManager. Every other key (besides "type"/"store-id") is passed
/// through verbatim as a "--key value" waved flag - e.g. "network=regtest" becomes
/// "--network regtest" - except the handful WavedReservedFlags.Keys owns, which are rejected
/// here rather than silently dropped or silently overridden.
/// </summary>
public sealed class WavelengthLightningConnectionStringHandler(IServiceProvider serviceProvider)
    : ILightningConnectionStringHandler
{
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "wavelength")
        {
            error = null;
            return null;
        }

        if (!kv.TryGetValue("store-id", out var storeId))
        {
            error = "The key 'store-id' is required for wavelength connection strings";
            return null;
        }

        var extraFlags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in kv)
        {
            if (key is "type" or "store-id")
                continue;

            if (WavedReservedFlags.Keys.Contains(key))
            {
                error = $"The key '{key}' is managed by the plugin and cannot be set in a wavelength " +
                        "connection string. Remove it - everything else waved accepts is passed through.";
                return null;
            }

            extraFlags[key] = value;
        }

        error = null;
        return ActivatorUtilities.CreateInstance<WavelengthLightningClient>(
            serviceProvider, network, storeId, (IReadOnlyDictionary<string, string>)extraFlags);
    }
}
