using System.Security.Cryptography;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Generates and persists each store's waved wallet-unlock password. Random per store (not
/// derived, not shared across the instance - a leaked password only ever compromises one
/// store's wallet) and encrypted at rest via BTCPay's own IDataProtectionProvider, the same
/// mechanism BTCPay core uses for comparable secrets (see UIStoreOnChainWalletsController's
/// "ConfigProtector"). The plaintext value only ever exists transiently: in memory here, and
/// briefly on disk in the store's --wallet.password_file (see WavedProcessManager).
/// </summary>
public sealed class WavedWalletCredentialStore(
    StoreRepository storeRepository,
    IDataProtectionProvider dataProtectionProvider)
{
    private const string SettingsKey = "Wavelength_Daemon";
    private const int PasswordEntropyBytes = 32;

    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("BTCPayServer.Plugins.Wavelength.WalletPassword");

    public async Task<string> GetOrCreatePasswordAsync(string storeId, CancellationToken cancellation = default)
    {
        var settings = await storeRepository.GetSettingAsync<WavedStoreSettings>(storeId, SettingsKey)
            ?? new WavedStoreSettings();

        if (!string.IsNullOrEmpty(settings.EncryptedWalletPassword))
            return _protector.Unprotect(settings.EncryptedWalletPassword);

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PasswordEntropyBytes));
        await storeRepository.UpdateSetting(storeId, SettingsKey, settings with
        {
            EncryptedWalletPassword = _protector.Protect(password)
        });
        return password;
    }
}
