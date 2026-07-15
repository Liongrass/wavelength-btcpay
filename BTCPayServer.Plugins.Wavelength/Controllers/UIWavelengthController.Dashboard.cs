using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Wavewalletrpc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    [HttpGet]
    public async Task<IActionResult> Index(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        // Visiting the dashboard counts as "first use" for lazy start, same as any RPC through
        // WavelengthLightningClient - see WavedProcessManager.EnsureStartedAsync. A failure to
        // start (bad flags, waved crashed, etc.) must never bubble up past this action - it would
        // 500 the whole request instead of showing the user what actually went wrong.
        try
        {
            await processManager.EnsureStartedAsync(storeId, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = false, StartupError = ex.Message });
        }

        var wallet = processManager.GetWalletClient(storeId);
        if (wallet is null)
        {
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = false });
        }

        try
        {
            var balance = await wallet.BalanceAsync(new BalanceRequest(), cancellationToken: cancellationToken);
            var activity = await wallet.ListAsync(
                new ListRequest { View = ListView.Activity, Limit = 25 }, cancellationToken: cancellationToken);

            return View(new WavelengthWalletViewModel
            {
                StoreId = storeId,
                IsRunning = true,
                ConfirmedSat = balance.ConfirmedSat,
                PendingInSat = balance.PendingInSat,
                PendingOutSat = balance.PendingOutSat,
                CreditAvailableSat = balance.CreditAvailableSat,
                Activity = (activity.Activity?.Entries ?? []).Select(ToRow).ToList()
            });
        }
        catch (RpcException ex)
        {
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = true, StartupError = ex.Status.Detail });
        }
    }

    private static WavelengthActivityRowViewModel ToRow(WalletEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind.ToString(),
        Status = entry.Status.ToString(),
        AmountSat = entry.AmountSat,
        Counterparty = entry.Counterparty,
        UpdatedAt = DateTimeOffset.FromUnixTimeSeconds(entry.UpdatedAtUnix),
        Note = string.IsNullOrEmpty(entry.Note) ? null : entry.Note
    };
}
