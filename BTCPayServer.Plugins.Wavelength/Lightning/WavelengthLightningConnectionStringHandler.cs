using BTCPayServer.Lightning;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace BTCPayServer.Plugins.Wavelength.Lightning;

/// <summary>
/// Resolves "type=wavelength;store-id=..." connection strings to a per-store WavelengthLightningClient.
/// Unlike LND/CLN-style handlers, the connection string carries no externally-meaningful address -
/// waved is plugin-managed per store, so store-id is just a lookup key into WavedProcessManager.
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

        error = null;
        return ActivatorUtilities.CreateInstance<WavelengthLightningClient>(serviceProvider, network, storeId);
    }
}
