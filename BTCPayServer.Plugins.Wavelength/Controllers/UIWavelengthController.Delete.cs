using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Wavewalletrpc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    private const string DeleteConfirmationPhrase = "DELETE";

    [HttpGet("delete")]
    public async Task<IActionResult> Delete(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        return View(await BuildDeleteViewModelAsync(storeId, cancellationToken));
    }

    // No typed confirmation check on the server would mean a crafted/replayed POST could delete
    // a wallet without ever seeing this warning - the check below is the real gate, not the JS.
    [HttpPost("delete")]
    public async Task<IActionResult> Delete(string storeId, string confirmation, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        if (!string.Equals(confirmation?.Trim(), DeleteConfirmationPhrase, StringComparison.Ordinal))
        {
            var vm = await BuildDeleteViewModelAsync(storeId, cancellationToken);
            vm.ErrorMessage = $"Type {DeleteConfirmationPhrase} exactly (case-sensitive) to confirm.";
            return View(vm);
        }

        await processManager.DeleteStoreDataAsync(storeId);
        TempData[WellKnownTempData.SuccessMessage] =
            "This store's Wavelength wallet data has been deleted. A new wallet will be created the next time it's used.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    private async Task<WavelengthDeleteViewModel> BuildDeleteViewModelAsync(string storeId, CancellationToken cancellationToken)
    {
        var vm = new WavelengthDeleteViewModel { StoreId = storeId, IsRunning = processManager.IsRunning(storeId) };

        var wallet = processManager.GetWalletClient(storeId);
        if (wallet is not null)
        {
            try
            {
                var balance = await wallet.BalanceAsync(new BalanceRequest(), cancellationToken: cancellationToken);
                vm.ConfirmedSat = balance.ConfirmedSat;
                vm.PendingInSat = balance.PendingInSat;
                vm.PendingOutSat = balance.PendingOutSat;
            }
            catch (RpcException)
            {
                // Leave balance fields blank - still show the confirmation page.
            }
        }

        return vm;
    }
}
