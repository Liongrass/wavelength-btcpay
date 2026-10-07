using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Wavelength.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

// CanModifyStoreSettings, not CanViewStoreSettings, on every action here - including the
// read-only ones (dashboard, VTXO list, mnemonic peek). This matches core's own Lightning
// controller (UIStoresController.LightningLike.cs), which gates all of its actions, GET and
// POST alike, behind CanModifyStoreSettings with no separate view-only tier - viewing a
// Lightning node's state is treated as sensitive as changing it. The built-in Manager role
// has CanViewStoreSettings but not CanModifyStoreSettings, so this also fixes a real gap:
// without it, a Manager could reach Send/Delete (fund movement, wallet deletion) through
// this plugin despite core denying Managers equivalent access to the real Lightning backend.
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
[AutoValidateAntiforgeryToken]
[Route("stores/{storeId}/plugins/wavelength")]
public partial class UIWavelengthController(
    WavedProcessManager processManager,
    WavedConfiguration config,
    WavedMnemonicPendingCache mnemonicCache,
    StoreRepository storeRepository,
    WavedStoreTokenProtector tokenProtector,
    WavedStoreSettingsStore storeSettings,
    IWavelengthServerSettingsSource serverSettings,
    PaymentMethodHandlerDictionary handlers,
    CurrencyNameTable currencyTable,
    RateFetcher rateFetcher,
    DefaultRulesCollection defaultRules) : Controller
{
    // BTC is the only crypto code wavelength-btcpay's connection string handler is registered
    // against - see WavelengthLightningConnectionStringHandler.
    private static readonly PaymentMethodId LightningPaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");

    /// <summary>
    /// The longest piece of an upstream error this plugin will put in front of a user. See
    /// TruncateErrorText for why there is a limit at all.
    /// </summary>
    private const int MaxErrorTextLength = 500;

    /// <summary>
    /// What both a refused save and a refused button press say. Kept in one place so the dashboard
    /// and the Lightning setup page cannot describe the same refusal two different ways, and so the
    /// fix the user needs is always in the text.
    /// </summary>
    private const string NotApprovedMessage =
        "Using Wavelength requires server admin approval - ask the server administrator to enable " +
        "it in the server settings.";

    /// <summary>
    /// Caps how much of an error's own text reaches the dashboard. waved's startup stderr and any
    /// gRPC status detail are shown to the store owner verbatim otherwise, and those are exactly the
    /// surfaces that echo back what a connection attempt found - the body of a fetched URL, a
    /// service banner, the shape of whatever answered. Combined with a flag that lets a store owner
    /// aim the server's waved at an address of their choosing (see WavedFlagValues), an unbounded
    /// error message is the response half of a request-forwarding primitive. A cap is not a fix for
    /// that - the value check is - but it bounds how much of an internal response one request can
    /// read, and leaves enough of the real message for a genuine misconfiguration to be diagnosed.
    /// </summary>
    private static string? TruncateErrorText(string? text)
        => text is { Length: > MaxErrorTextLength } ? text[..MaxErrorTextLength] + "\u2026" : text;

    /// <summary>
    /// Whether this store may be run on the server's behalf at all - the same question
    /// WavelengthLightningConnectionStringHandler asks when a connection string is saved, asked
    /// again here because the dashboard's buttons act directly rather than through a connection
    /// string. An administrator, or a server that has said every store may have Wavelength, passes;
    /// a store already approved passes; everyone else is refused with the same message a rejected
    /// save produces, so the two halves of the plugin tell one story.
    /// </summary>
    private async Task<bool> IsWavelengthAllowedForCurrentUserAsync(string storeId, CancellationToken cancellationToken)
    {
        if (User.IsInRole(Roles.ServerAdmin))
            return true;

        if ((await storeSettings.GetAsync(storeId, cancellationToken)).ServerAdminApproved)
            return true;

        return (await serverSettings.GetAsync(cancellationToken)).AllowForAllStores;
    }

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
    private async Task<IActionResult?> RedirectIfNoWalletAsync(
        string storeId, LightningPaymentMethodConfig wavelengthConfig, CancellationToken cancellationToken)
    {
        // The approval boundary applies to this path too, since it starts the process: every
        // page that would otherwise fall through to here (Send, Receive, VTXOs, and the RPCs
        // behind them) reaches EnsureStartedAsync through this one method. See
        // IsWavelengthAllowedForCurrentUserAsync.
        if (!await IsWavelengthAllowedForCurrentUserAsync(storeId, cancellationToken))
        {
            TempData[WellKnownTempData.ErrorMessage] = NotApprovedMessage;
            return RedirectToAction(nameof(Index), new { storeId });
        }

        // Re-parses the store's CURRENT connection string on every call, the same way the
        // Advanced page's "Restart waved" button does - a first-ever start (or a restart after a
        // crash/stop/delete) must pick up flags that were just saved, not stale ones from
        // whenever EnsureStartedAsync last happened to be called with fresh flags. Before this,
        // only an actual Lightning RPC (WavelengthLightningClient.EnsureReadyAsync) or that
        // Restart button ever threaded live flags through - simply browsing this store's own
        // Wavelength pages silently started/kept the process on stale or empty flags instead.
        if (!WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
                wavelengthConfig.ConnectionString!, out var extraFlags, out var parseError,
                allowLocal: (await storeSettings.GetAsync(storeId, cancellationToken)).AllowLocalEndpoints))
        {
            TempData[WellKnownTempData.ErrorMessage] = TruncateErrorText(parseError);
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            await processManager.EnsureStartedAsync(storeId, extraFlags, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or RpcException)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                TruncateErrorText(ex is RpcException rpcEx ? rpcEx.Status.Detail : ex.Message);
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (!await processManager.WalletExistsAsync(storeId, cancellationToken))
        {
            TempData[WellKnownTempData.ErrorMessage] = "This store doesn't have a Wavelength wallet yet - create one first.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        return null;
    }

    // Deliberately a GET, not a POST: CreateWallet redirects here (a redirect is always a GET).
    // Peek is non-destructive - revisiting this page (refresh, back button, coming back later)
    // keeps showing the same phrase until AcknowledgeMnemonic is called, rather than losing it
    // the moment it's shown once.
    [HttpGet("mnemonic")]
    public IActionResult Mnemonic(string storeId)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();

        var mnemonic = mnemonicCache.Peek(storeId);
        return View(new WavelengthMnemonicViewModel { StoreId = storeId, StoreName = store.StoreName, Mnemonic = mnemonic });
    }

    // The only place a pending mnemonic is ever cleared - see WavedMnemonicPendingCache.
    [HttpPost("mnemonic/acknowledge")]
    public IActionResult AcknowledgeMnemonic(string storeId)
    {
        mnemonicCache.Acknowledge(storeId);
        return RedirectToAction(nameof(Index), new { storeId });
    }
}
