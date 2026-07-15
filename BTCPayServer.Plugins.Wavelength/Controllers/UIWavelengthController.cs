using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewStoreSettings)]
[AutoValidateAntiforgeryToken]
[Route("stores/{storeId}/plugins/wavelength")]
public partial class UIWavelengthController(
    WavedProcessManager processManager,
    WavedConfiguration config,
    WavedMnemonicOnceCache mnemonicCache,
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers) : Controller
{
    // BTC is the only crypto code wavelength-btcpay's connection string handler is registered
    // against - see WavelengthLightningConnectionStringHandler.
    private static readonly PaymentMethodId LightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");

    /// <summary>
    /// Null if this store's Lightning connection isn't currently pointed at Wavelength at all
    /// (never configured, or switched to a different node) - the dashboard/send/receive/advanced
    /// pages all redirect to the connection setup page in that case rather than showing anything.
    /// </summary>
    private LightningPaymentMethodConfig? GetWavelengthConfig(StoreData store)
    {
        var paymentConfig = store.GetPaymentMethodConfig<LightningPaymentMethodConfig>(LightningPaymentMethodId, handlers);
        return paymentConfig?.ConnectionString?.StartsWith("type=wavelength", StringComparison.OrdinalIgnoreCase) == true
            ? paymentConfig
            : null;
    }

    private IActionResult RedirectToLightningSetup(string storeId)
        => RedirectToAction("SetupLightningNode", "UIStores", new { storeId, cryptoCode = "BTC" });

    /// <summary>
    /// Starts this store's waved process if needed and, if no wallet has been explicitly created
    /// yet, redirects to the dashboard (where the "Create wallet" button lives) instead of
    /// letting the caller proceed into Send/Receive RPCs that would just fail. Returns null when
    /// it's safe to proceed.
    /// </summary>
    private async Task<IActionResult?> RedirectIfNoWalletAsync(string storeId, CancellationToken cancellationToken)
    {
        try
        {
            await processManager.EnsureStartedAsync(storeId, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or RpcException)
        {
            TempData[WellKnownTempData.ErrorMessage] = ex is RpcException rpcEx ? rpcEx.Status.Detail : ex.Message;
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (!await processManager.WalletExistsAsync(storeId, cancellationToken))
        {
            TempData[WellKnownTempData.ErrorMessage] = "This store doesn't have a Wavelength wallet yet - create one first.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        return null;
    }

    // Deliberately a GET, not a POST: the notification's ActionLink navigates here directly.
    // TakeOnce still only ever succeeds once - a second visit (refresh, back button, someone
    // else clicking an old link) finds nothing left to show.
    [HttpGet("mnemonic")]
    public IActionResult Mnemonic(string storeId)
    {
        var mnemonic = mnemonicCache.TakeOnce(storeId);
        return View(mnemonic);
    }
}
