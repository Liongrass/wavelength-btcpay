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
    public async Task<IActionResult> Receive(string storeId, string? currency, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        if (await RedirectIfNoWalletAsync(storeId, cancellationToken) is { } redirect)
            return redirect;

        var model = new WavelengthReceiveViewModel
        {
            StoreId = storeId,
            Currency = string.IsNullOrWhiteSpace(currency) ? store.GetStoreBlob().DefaultCurrency : currency.Trim()
        };
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

        if (model.AmountSat <= 0)
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
                AmtSat = (ulong)model.AmountSat,
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

    // Fiat conversion is display-only: the rate/divisibility populated here only drive the
    // client-side JS that keeps the sats and fiat inputs in sync (see Receive.cshtml) - the
    // backend never uses anything but the resulting AmountSat, so a stale or tampered Rate field
    // can at most show the wrong number on the visitor's own screen, never affect what invoice
    // actually gets created. Uses this store's own configured rate rules (RateFetcher), the same
    // machinery BTCPay core prices invoices/POS with - not a hardcoded exchange.
    private async Task PopulateRateAsync(WavelengthReceiveViewModel model, StoreData store, CancellationToken cancellationToken)
    {
        var rateRules = store.GetStoreBlob().GetRateRules(defaultRules);
        var currencyPair = new CurrencyPair("BTC", model.Currency);

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
            model.FiatDivisibility = currencyTable.GetNumberFormatInfo(currencyPair.Right, true).CurrencyDecimalDigits;
        }
        catch (OperationCanceledException)
        {
            model.RateErrorMessage = $"Timed out fetching a {currencyPair.Right} rate.";
        }
    }
}
