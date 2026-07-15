namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Per-store overrides persisted via StoreRepository.GetSettingAsync/UpdateSetting under the
/// "Wavelength_Daemon" settings key. Empty for now; extend as store-level configuration
/// (e.g. per-store network override, wallet backend choice) is needed.
/// </summary>
public sealed record WavedStoreSettings;
