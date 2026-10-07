using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Server-wide switchboard for this plugin, persisted through BTCPay's own
/// <see cref="ISettingsRepository"/> (one row per type, no migration needed) and read wherever the
/// plugin has to decide whether the server is willing to host a store's wallet.
///
/// Why this exists at all: a store owner with nothing more than CanModifyStoreSettings on their
/// own store can otherwise save a wavelength connection string, and doing so makes the server
/// spawn a long-lived waved process and - through the dashboard's "Create wallet" button - mint a
/// wallet whose seed lives on the server. That is a decision about the *server's* resources and
/// the *server's* custody of a seed, not about one store, so BTCPay core gates the comparable
/// cases behind an administrator: see PoliciesSettings.AllowLightningInternalNodeForAll and
/// AllowHotWalletForAll, which the two members below deliberately mirror. A store owner should not
/// be able to make that decision for a server they do not administer, and should not be able to
/// exhaust it either, which is what <see cref="MaxStoreProcesses"/> bounds.
/// </summary>
public sealed class WavelengthServerSettings
{
    /// <summary>
    /// The default ceiling on concurrently running waved processes when nothing has been
    /// persisted and no environment override is set. Deliberately nonzero rather than
    /// unlimited: one store per wallet is unavoidable (waved is single-wallet-per-process), but
    /// an unbounded number of attacker-created stores must not be able to fork an unbounded
    /// number of daemons.
    /// </summary>
    public const int DefaultMaxStoreProcesses = 100;

    /// <summary>
    /// Lets every store use Wavelength without an administrator approving each one. False by
    /// default - the same default core uses for its own internal-node and hot-wallet equivalents -
    /// so a fresh install does not hand store owners this capability until an administrator
    /// deliberately grants it (by flipping this through the server settings, or by setting the
    /// WAVELENGTH_ALLOW_ALL_STORES environment variable, which needs no UI).
    /// </summary>
    public bool AllowForAllStores { get; set; }

    /// <summary>
    /// How many stores may have a waved process running at once. See
    /// <see cref="DefaultMaxStoreProcesses"/> for the default and WavedProcessManager for where
    /// this is enforced - including on the startup pre-warm and crash-restart paths, which reach
    /// the same gate rather than bypassing it.
    /// </summary>
    public int MaxStoreProcesses { get; set; } = DefaultMaxStoreProcesses;
}

/// <summary>
/// The one server-settings read the rest of the plugin makes, separated from
/// <see cref="ISettingsRepository"/> so a caller's save-time checks can be exercised without a
/// settings store. The implementation below reads the persisted row and applies the environment
/// overrides.
/// </summary>
public interface IWavelengthServerSettingsSource
{
    /// <summary>The effective settings: what is persisted, with the environment applied on top.</summary>
    Task<WavelengthServerSettings> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The real <see cref="IWavelengthServerSettingsSource"/>: reads the row an administrator saved (or
/// the defaults when nothing has been saved yet) and then applies the WAVELENGTH_* environment
/// overrides on top. The override exists because this plugin deliberately ships no server-settings
/// page - an administrator flips these far more easily through the environment of whatever already
/// runs BTCPay - and because the safe default is off, which without an out-of-band way to turn it on
/// would leave a server whose operator has decided every store should have Wavelength unable to say
/// so. The override is applied *after* the read rather than written into it: an operator's
/// environment is a statement about this deployment, and it should win without the plugin writing a
/// row nobody asked it to write. Mirrors WavedConfiguration's use of WAVELENGTH_* variables, so the
/// plugin has one story for "configuration an operator sets on the container".
/// </summary>
public sealed class WavelengthServerSettingsProvider(ISettingsRepository settingsRepository)
    : IWavelengthServerSettingsSource
{
    /// <summary>Turns <see cref="WavelengthServerSettings.AllowForAllStores"/> on regardless of what is persisted.</summary>
    public const string AllowAllStoresEnvVar = "WAVELENGTH_ALLOW_ALL_STORES";

    /// <summary>Overrides <see cref="WavelengthServerSettings.MaxStoreProcesses"/> regardless of what is persisted.</summary>
    public const string MaxStoreProcessesEnvVar = "WAVELENGTH_MAX_STORE_PROCESSES";

    public async Task<WavelengthServerSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var settings = await settingsRepository.GetSettingAsync<WavelengthServerSettings>()
            ?? new WavelengthServerSettings();

        if (ReadBoolEnvironment(AllowAllStoresEnvVar) is { } allowAll)
            settings.AllowForAllStores = allowAll;

        if (int.TryParse(Environment.GetEnvironmentVariable(MaxStoreProcessesEnvVar), out var max) && max > 0)
            settings.MaxStoreProcesses = max;

        return settings;
    }

    private static bool? ReadBoolEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => null
        };
}

