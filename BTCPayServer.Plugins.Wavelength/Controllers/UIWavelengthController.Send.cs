using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Wavewalletrpc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    [HttpGet("send")]
    public IActionResult Send(string storeId)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        return View(new WavelengthSendViewModel { StoreId = storeId });
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send(
        string storeId, WavelengthSendViewModel model, string command, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        model.StoreId = storeId;

        try
        {
            await processManager.EnsureStartedAsync(storeId, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or RpcException)
        {
            model.ErrorMessage = ex is RpcException rpcEx ? rpcEx.Status.Detail : ex.Message;
            return View(model);
        }

        var wallet = processManager.GetWalletClient(storeId);
        if (wallet is null)
        {
            model.ErrorMessage = "This store's waved instance is not running.";
            return View(model);
        }

        try
        {
            switch (command)
            {
                case "preview":
                    if (string.IsNullOrWhiteSpace(model.Destination))
                    {
                        model.ErrorMessage = "Enter a Lightning invoice to pay.";
                        return View(model);
                    }

                    var prepareRequest = new PrepareSendRequest { Invoice = model.Destination.Trim() };
                    if (model.AmountSat is > 0)
                        prepareRequest.AmtSat = (ulong)model.AmountSat.Value;

                    var prepared = await wallet.PrepareSendAsync(prepareRequest, cancellationToken: cancellationToken);
                    model.SendIntentId = prepared.SendIntentId;
                    model.PreviewAmountSat = prepared.AmountSat;
                    model.PreviewFeeSat = prepared.FeeKnown ? prepared.ExpectedFeeSat : null;
                    model.PreviewFeeKnown = prepared.FeeKnown;
                    model.PreviewRail = prepared.Rail.ToString();
                    model.PreviewDestinationSummary = prepared.DestinationSummary;
                    break;

                case "confirm":
                    if (string.IsNullOrEmpty(model.SendIntentId))
                    {
                        model.ErrorMessage = "This send preview has expired - start again.";
                        return View(new WavelengthSendViewModel { StoreId = storeId });
                    }

                    var sent = await wallet.SendAsync(
                        new SendRequest { SendIntentId = model.SendIntentId }, cancellationToken: cancellationToken);
                    TempData[WellKnownTempData.SuccessMessage] = $"Sent {sent.ActualAmountSat:N0} sats.";
                    return RedirectToAction(nameof(Index), new { storeId });

                default:
                    model.ErrorMessage = "Unknown command.";
                    break;
            }
        }
        catch (RpcException ex)
        {
            model.ErrorMessage = ex.Status.Detail;
        }

        return View(model);
    }
}
