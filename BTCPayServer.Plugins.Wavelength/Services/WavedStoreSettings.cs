namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Per-store overrides persisted via StoreRepository.GetSettingAsync/UpdateSetting under the
/// "Wavelength_Daemon" settings key. Extend as more store-level configuration (e.g. per-store
/// network override, wallet backend choice) is needed.
/// </summary>
public sealed record WavedStoreSettings
{
    /// <summary>
    /// The store's waved wallet-unlock password, encrypted via IDataProtectionProvider (see
    /// WavedWalletCredentialStore). Never stored in plaintext - only ever decrypted transiently
    /// to write the --wallet.password_file waved reads at its own startup.
    /// </summary>
    public string? EncryptedWalletPassword { get; init; }
}
