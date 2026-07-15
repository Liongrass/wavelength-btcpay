using BTCPayServer.Plugins.Wavelength.ViewModels;
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

        return View(new WavelengthReceiveViewModel { StoreId = storeId });
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
}
