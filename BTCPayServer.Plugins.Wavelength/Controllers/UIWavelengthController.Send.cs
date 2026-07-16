using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Wavewalletrpc;
using WalletServiceClient = Wavewalletrpc.WalletService.WalletServiceClient;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    [HttpGet("send")]
    public async Task<IActionResult> Send(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        if (await RedirectIfNoWalletAsync(storeId, cancellationToken) is { } redirect)
            return redirect;

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
            switch (command)
            {
                // Step 1: classify whatever was pasted. waved's PrepareSend has no server-side
                // "sniff this string" RPC - the oneof destination (invoice vs. onchain_address)
                // has to be picked by the caller. Every BOLT-11 invoice starts with "ln"
                // (lnbc/lntb/lntbs/lnbcrt...) and no valid on-chain address does, so that prefix
                // alone is enough to tell them apart.
                case "detect":
                    if (string.IsNullOrWhiteSpace(model.Destination))
                    {
                        model.ErrorMessage = "Paste a Lightning invoice or an on-chain address.";
                        return View(model);
                    }

                    var destination = StripUriPrefix(model.Destination.Trim());
                    model.Destination = destination;
                    model.IsOnchain = !LooksLikeLightningInvoice(destination);

                    if (model.IsOnchain)
                    {
                        // No PrepareSend yet - an on-chain send needs an amount first, which is
                        // step 2. A Lightning invoice already carries its own amount (v1 rejects
                        // amountless invoices at the wallet layer), so it skips straight to the
                        // same confirm preview an on-chain send reaches after step 2.
                        return View(model);
                    }

                    await PreviewAsync(wallet, model, new PrepareSendRequest { Invoice = destination }, cancellationToken);
                    break;

                // Step 2, on-chain only: how much (or sweep the whole wallet), then preview.
                case "preview":
                    if (string.IsNullOrWhiteSpace(model.Destination))
                    {
                        model.ErrorMessage = "Paste a Lightning invoice or an on-chain address.";
                        return View(new WavelengthSendViewModel { StoreId = storeId });
                    }

                    var prepareRequest = new PrepareSendRequest { OnchainAddress = model.Destination };
                    if (model.SweepAll)
                    {
                        prepareRequest.SweepAll = true;
                    }
                    else
                    {
                        if (model.AmountSat is not > 0)
                        {
                            model.ErrorMessage = "Enter an amount greater than zero, or choose \"Send all\".";
                            return View(model);
                        }
                        prepareRequest.AmtSat = (ulong)model.AmountSat.Value;
                    }

                    await PreviewAsync(wallet, model, prepareRequest, cancellationToken);
                    break;

                // Step 3 (2 for Lightning): dispatch the previously previewed send.
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

    private static async Task PreviewAsync(
        WalletServiceClient wallet, WavelengthSendViewModel model, PrepareSendRequest request, CancellationToken cancellationToken)
    {
        var prepared = await wallet.PrepareSendAsync(request, cancellationToken: cancellationToken);
        model.SendIntentId = prepared.SendIntentId;
        model.PreviewAmountSat = prepared.AmountSat;
        model.PreviewFeeSat = prepared.FeeKnown ? prepared.ExpectedFeeSat : null;
        model.PreviewFeeKnown = prepared.FeeKnown;
        model.PreviewRail = prepared.Rail.ToString();
        model.PreviewDestinationSummary = prepared.DestinationSummary;
    }

    private static bool LooksLikeLightningInvoice(string destination)
        => destination.StartsWith("ln", StringComparison.OrdinalIgnoreCase);

    private static string StripUriPrefix(string destination)
    {
        foreach (var scheme in new[] { "lightning:", "bitcoin:" })
        {
            if (destination.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                destination = destination[scheme.Length..];
                break;
            }
        }

        // Drop BIP21-style query parameters (?amount=...&label=...) - only the bare
        // invoice/address is used; the amount is always collected or derived separately.
        var queryIndex = destination.IndexOf('?');
        return queryIndex >= 0 ? destination[..queryIndex] : destination;
    }
}
