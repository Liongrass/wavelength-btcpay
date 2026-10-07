namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Per-store overrides persisted via StoreRepository.GetSettingAsync/UpdateSetting under
/// <see cref="SettingsKey"/>.
///
/// Read through WavedStoreSettingsStore rather than directly: the token-verification path
/// (see WavedStoreTokenProtector.TryResolve) runs synchronously inside
/// WavelengthLightningConnectionStringHandler.Create, which BTCPay core calls on every
/// Lightning operation a store performs - an invoice creation included - so this row is cached
/// per store and invalidated on write instead of being re-read each time.
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
    /// The store's token seed: a random 32-byte value (Base64), generated on first use and the
    /// HMAC key inside every connection string token this store is issued. It exists to make a
    /// token <em>revocable</em>. A token only verifies while the HMAC it carries matches the one
    /// this seed produces, so without a persisted seed to compare against, the only way to
    /// invalidate a token would be rotating the instance's whole key ring - which would take
    /// every other store's token and password with it. Persisting the seed per store is what lets
    /// WavedStoreTokenProtector.RegenerateTokenAsync kill exactly one store's string and nothing
    /// else. See WavedStoreTokenProtector for the format and the checks that use it.
    /// </summary>
    public string? TokenSeed { get; init; }

    /// <summary>
    /// Set once this store has been adopted for Wavelength by a server administrator (or while
    /// the server-wide AllowForAllStores setting was on). It records that the decision to let
    /// this store run a server-hosted wallet was made by someone entitled to make it, so later
    /// ordinary use of the store by a non-admin does not have to re-ask. Only ever set by
    /// WavelengthLightningConnectionStringHandler, which is the only place that can see who is
    /// saving - see WavedProcessManager.EnsureStartedAsync for how it is consulted.
    /// </summary>
    public bool ServerAdminApproved { get; init; }

    /// <summary>
    /// Set when a server administrator saved this store's Wavelength connection string with a
    /// local/private esplora, fee, or peer address. Without it those values would be rejected
    /// again at process start, since a restart re-reads flags that no request context is
    /// attached to and so has no user to ask - which would lock an admin out of a store they had
    /// deliberately pointed at a LAN endpoint. See WavedFlagValues.
    /// </summary>
    public bool AllowLocalEndpoints { get; init; }

    /// <summary>
    /// Extra waved CLI flags parsed from the store's connection string (e.g. "network",
    /// "wallet.esploraurl") - everything except type/token that WavedAllowedFlags.IsAllowed
    /// accepts. Refreshed every time WavedProcessManager.EnsureStartedAsync sees a live
    /// connection string; used as-is on later restarts (crash recovery, BTCPay Server restart)
    /// when no fresh connection string is available. A key WavedAllowedFlags doesn't accept is
    /// never persisted here - see WavelengthLightningConnectionStringHandler - and even a value
    /// persisted before an allowlist tightening is filtered out again by
    /// WavedProcessManager.StartStoreAsync before ever reaching waved, as is any value
    /// WavedFlagValues rejects.
    /// </summary>
    public Dictionary<string, string>? ExtraWavedFlags { get; init; }
}
