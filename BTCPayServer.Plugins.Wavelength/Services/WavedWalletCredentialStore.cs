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

    /// <summary>
    /// Filename (under a store's own datadir) the plaintext password is briefly written to for
    /// waved's --wallet.password_file auto-unlock - see WavedProcessManager.WritePasswordFile,
    /// the only writer. Defined here, not there, since GetOrCreatePasswordAsync below needs the
    /// same path as a recovery fallback and this is the one place that should own the filename.
    /// </summary>
    public const string PasswordFileName = "wallet_password";

    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("BTCPayServer.Plugins.Wavelength.WalletPassword");

    public async Task<string> GetOrCreatePasswordAsync(string storeId, string dataDir, CancellationToken cancellation = default)
    {
        var settings = await storeRepository.GetSettingAsync<WavedStoreSettings>(storeId, SettingsKey)
            ?? new WavedStoreSettings();

        if (!string.IsNullOrEmpty(settings.EncryptedWalletPassword))
        {
            try
            {
                return _protector.Unprotect(settings.EncryptedWalletPassword);
            }
            catch (CryptographicException) when (File.Exists(Path.Combine(dataDir, PasswordFileName)))
            {
                // Data Protection's key ring can't decrypt the persisted value - most likely it
                // was rotated or lost (e.g. a migration that carried over the database but not
                // BTCPay's DataProtection-Keys directory alongside it). The password itself is
                // still sitting in plaintext exactly where waved itself reads it from - that file
                // was never protected by this key ring at all, so recovering from it here doesn't
                // weaken anything: anyone who could read it to exploit this fallback already has
                // the filesystem access needed to read it directly regardless. Re-encrypting and
                // persisting it below is what makes this self-healing - the next call goes
                // through the normal path above without hitting this fallback again, as long as
                // whatever key ring is active now stays active afterward.
                var recovered = (await File.ReadAllTextAsync(Path.Combine(dataDir, PasswordFileName), cancellation)).Trim();
                await storeRepository.UpdateSetting(storeId, SettingsKey, settings with
                {
                    EncryptedWalletPassword = _protector.Protect(recovered)
                });
                return recovered;
            }
        }

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(PasswordEntropyBytes));
        await storeRepository.UpdateSetting(storeId, SettingsKey, settings with
        {
            EncryptedWalletPassword = _protector.Protect(password)
        });
        return password;
    }
}
