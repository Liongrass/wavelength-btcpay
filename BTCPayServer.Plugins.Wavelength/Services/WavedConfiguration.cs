using BTCPayServer.Configuration;
using Microsoft.Extensions.Options;

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

    public WavedConfiguration(IOptions<DataDirectories> dataDirectories, BTCPayNetworkProvider networkProvider)
    {
        // DataDirectories.DataDir is BTCPay's own persistent data directory (the one operators
        // back up and that survives container/binary upgrades) - not AppContext.BaseDirectory,
        // which is wherever BTCPay's own binaries happen to be unpacked and can be wiped on
        // every redeploy. Getting this wrong would put real wallet seeds at risk.
        DataDir = Environment.GetEnvironmentVariable("WAVELENGTH_DATADIR")
            ?? Path.Combine(dataDirectories.Value.DataDir, "Plugins", "Wavelength");
        Host = Environment.GetEnvironmentVariable("WAVELENGTH_HOST") ?? "127.0.0.1";
        BasePort = int.TryParse(Environment.GetEnvironmentVariable("WAVELENGTH_BASE_PORT"), out var p) ? p : 10029;

        // WAVELENGTH_NETWORK, when set, is an explicit override - for the two networks waved
        // supports that BTCPay has no concept of at all (testnet4, simnet), or for an operator who
        // deliberately wants this store's wallet on a different network than the rest of the
        // instance. Left unset, the default now tracks BTCPay's own ambient network instead of a
        // second, easy-to-forget variable that could silently diverge from it - running a wallet
        // against the wrong network because nobody remembered to set a redundant setting is
        // exactly the mistake this removes.
        Network = Environment.GetEnvironmentVariable("WAVELENGTH_NETWORK") ?? DefaultNetworkFrom(networkProvider);
    }

    public string GetStoreDataDir(string storeId) => Path.Combine(DataDir, "stores", storeId);

    private static string DefaultNetworkFrom(BTCPayNetworkProvider networkProvider)
    {
        try
        {
            // ChainName.ToString() lowercases to exactly "mainnet"/"testnet"/"regtest"/"signet" -
            // the same strings waved's own --network flag expects, for every network BTCPay itself
            // can run on (verified against the actual NBitcoin values, not assumed).
            return networkProvider.BTC.NBitcoinNetwork.ChainName.ToString().ToLowerInvariant();
        }
        catch (InvalidOperationException)
        {
            // BTCPayNetworkProvider.BTC throws if BTC isn't configured - which should never happen
            // (this plugin is meaningless without it), but falling back here is cheap insurance
            // against that edge case taking the whole plugin down at DI-resolution time instead.
            return "mainnet";
        }
    }
}
