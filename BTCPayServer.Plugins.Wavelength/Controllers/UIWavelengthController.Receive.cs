using BTCPayServer.Data;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Wavewalletrpc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    [HttpGet("receive")]
    public async Task<IActionResult> Receive(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        if (await RedirectIfNoWalletAsync(storeId, cancellationToken) is { } redirect)
            return redirect;

        var model = new WavelengthReceiveViewModel { StoreId = storeId };
        await PopulateRateAsync(model, store, cancellationToken);
        return View(model);
    }

    [HttpPost("receive")]
    public async Task<IActionResult> Receive(string storeId, WavelengthReceiveViewModel model, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        model.StoreId = storeId;
        model.Invoice = null;
        // Populated fresh before validation, not round-tripped from the form, so the exchange
        // rate actually used to derive AmountSat below is always this same request's own fetch -
        // never a stale or client-supplied value.
        await PopulateRateAsync(model, store, cancellationToken);

        if (model.Amount is not > 0)
        {
            model.ErrorMessage = "Enter an amount greater than zero.";
            return View(model);
        }

        long amountSat;
        if (IsSats(model.Currency))
        {
            amountSat = (long)model.Amount.Value;
        }
        else if (model.Rate is > 0)
        {
            amountSat = (long)Math.Round(model.Amount.Value / model.Rate.Value * 100_000_000m);
        }
        else
        {
            model.ErrorMessage = model.RateErrorMessage ?? $"Could not fetch a {model.Currency} rate.";
            return View(model);
        }

        if (amountSat <= 0)
        {
            model.ErrorMessage = "Enter an amount greater than zero.";
            return View(model);
        }

        if (await RedirectIfNoWalletAsync(storeId, cancellationToken) is { } redirect)
            return redirect;

        var wallet = processManager.GetWalletClient(storeId);
        if (wallet is null)
        {
            model.ErrorMessage = "This store's waved instance is not running.";
            return View(model);
        }

        try
        {
            var response = await wallet.RecvAsync(new RecvRequest
            {
                AmtSat = (ulong)amountSat,
                Memo = model.Memo ?? string.Empty
            }, cancellationToken: cancellationToken);
            model.Invoice = response.Invoice;
        }
        catch (RpcException ex)
        {
            model.ErrorMessage = ex.Status.Detail;
        }

        return View(model);
    }

    // Backs the Amount field's live client-side conversion display - see Receive.cshtml. Called
    // via fetch() whenever the currency box changes, since there's no live rate-streaming API to
    // subscribe to; a fresh one-shot fetch per change is what BTCPay core's own equivalents
    // (e.g. WalletSend) do too, just baked in at page load instead of on every currency switch.
    [HttpGet("receive/rate")]
    public async Task<IActionResult> ReceiveRate(string storeId, string currency, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return NotFound();

        var model = new WavelengthReceiveViewModel { StoreId = storeId, Currency = currency };
        await PopulateRateAsync(model, store, cancellationToken);
        return Json(new
        {
            rate = (double?)model.Rate,
            currency = model.RateCurrency,
            divisibility = model.RateDivisibility,
            error = model.RateErrorMessage
        });
    }

    private static bool IsSats(string currency) => string.Equals(currency, "sats", StringComparison.OrdinalIgnoreCase);

    // Uses this store's own configured rate rules (RateFetcher), the same machinery BTCPay core
    // prices invoices/POS with - not a hardcoded exchange. Rates the store's own default currency
    // when the amount unit is sats (a helpful reference even though no conversion is needed for
    // the amount itself), or the selected currency directly otherwise.
    private async Task PopulateRateAsync(WavelengthReceiveViewModel model, StoreData store, CancellationToken cancellationToken)
    {
        var referenceCurrency = IsSats(model.Currency) ? store.GetStoreBlob().DefaultCurrency : model.Currency;
        var rateRules = store.GetStoreBlob().GetRateRules(defaultRules);
        var currencyPair = new CurrencyPair("BTC", referenceCurrency);

        model.RateCurrency = currencyPair.Right;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await rateFetcher.FetchRate(currencyPair, rateRules, new StoreIdRateContext(store.Id), cts.Token);
            if (result.BidAsk is null)
            {
                model.RateErrorMessage = $"Could not fetch a {currencyPair.Right} rate.";
                return;
            }

            model.Rate = result.BidAsk.Center;
            model.RateDivisibility = currencyTable.GetNumberFormatInfo(currencyPair.Right, true).CurrencyDecimalDigits;
        }
        catch (OperationCanceledException)
        {
            model.RateErrorMessage = $"Timed out fetching a {currencyPair.Right} rate.";
        }
    }
}
