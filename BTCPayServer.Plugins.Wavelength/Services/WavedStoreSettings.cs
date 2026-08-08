namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Per-store overrides persisted via StoreRepository.GetSettingAsync/UpdateSetting under
/// <see cref="SettingsKey"/>.
/// </summary>
public sealed record WavedStoreSettings
{
    public const string SettingsKey = "Wavelength_Daemon";

    /// <summary>
    /// The store's waved wallet-unlock password, encrypted via IDataProtectionProvider (see
    /// WavedWalletCredentialStore). Never stored in plaintext - only ever decrypted transiently
    /// to write the --wallet.password_file waved reads at its own startup.
    /// </summary>
    public string? EncryptedWalletPassword { get; init; }

    /// <summary>
    /// Extra waved CLI flags parsed from the store's connection string (e.g. "network",
    /// "wallet.esploraurl") - everything except type/token and WavedReservedFlags.Keys.
    /// Refreshed every time WavedProcessManager.EnsureStartedAsync sees a live connection
    /// string; used as-is on later restarts (crash recovery, BTCPay Server restart) when no
    /// fresh connection string is available. A key already covered by WavedReservedFlags is
    /// never persisted here - see WavelengthLightningConnectionStringHandler.
    /// </summary>
    public Dictionary<string, string>? ExtraWavedFlags { get; init; }
}
