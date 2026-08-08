using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Converts between a store's real BTCPay ID and the opaque token used in its wavelength
/// connection string instead, via BTCPay's own IDataProtectionProvider - the same mechanism this
/// plugin already uses for the per-store wallet password (see WavedWalletCredentialStore).
///
/// Why: a store's real ID is often visible to anyone with even minor access to that store (it's
/// in the URL), so using it as the sole "authorization" for which waved instance a connection
/// string reaches meant knowing a low-sensitivity value was enough to point a store at someone
/// else's wallet. The token requires actually possessing that store's generated connection
/// string instead - the same trust model BTCPay already uses for e.g. LND macaroons - and
/// complements (does not replace) the per-store TLS+macaroon boundary waved itself now enforces
/// (see WavedProcessManager.BuildSecureChannel): that one stops a request from reaching the wrong
/// waved process; this one stops a connection string from being told to reach the wrong one on
/// purpose in the first place.
///
/// Deliberately stateless: the token IS the encrypted storeId, authenticated by Data Protection's
/// own key ring. There is nothing to persist or look up here, which means resolving a token back
/// to a storeId is synchronous and local - no database round-trip, no cache-warming race at
/// BTCPay startup, and no dependency on WavedProcessManager's own state.
/// </summary>
public sealed class WavedStoreTokenProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("BTCPayServer.Plugins.Wavelength.StoreToken");

    public string Protect(string storeId) => _protector.Protect(storeId);

    /// <summary>False for any input that isn't a token this instance's key ring actually issued -
    /// malformed, tampered, or genuinely foreign input, all treated the same as "invalid".</summary>
    public bool TryUnprotect(string token, out string storeId)
    {
        try
        {
            storeId = _protector.Unprotect(token);
            return true;
        }
        catch (Exception)
        {
            storeId = "";
            return false;
        }
    }
}
