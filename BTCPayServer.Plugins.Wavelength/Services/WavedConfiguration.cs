namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Server-wide defaults for launching per-store waved instances. Loaded from environment
/// variables so operators can override without rebuilding the plugin.
/// </summary>
public sealed class WavedConfiguration
{
    /// <summary>Root directory under which each store gets its own subdirectory (--datadir).</summary>
    public string DataDir { get; }

    /// <summary>Loopback host waved instances bind to. Never expose this beyond localhost.</summary>
    public string Host { get; }

    /// <summary>First port handed out; each subsequent store instance gets the next free port above it.</summary>
    public int BasePort { get; }

    /// <summary>Network passed to waved via --network (mainnet, testnet, testnet4, signet, regtest, simnet).</summary>
    public string Network { get; }

    public WavedConfiguration()
    {
        DataDir = Environment.GetEnvironmentVariable("WAVELENGTH_DATADIR")
            ?? Path.Combine(AppContext.BaseDirectory, "wavelength-data");
        Host = Environment.GetEnvironmentVariable("WAVELENGTH_HOST") ?? "127.0.0.1";
        BasePort = int.TryParse(Environment.GetEnvironmentVariable("WAVELENGTH_BASE_PORT"), out var p) ? p : 10029;
        Network = Environment.GetEnvironmentVariable("WAVELENGTH_NETWORK") ?? "mainnet";
    }

    public string GetStoreDataDir(string storeId) => Path.Combine(DataDir, "stores", storeId);
}
