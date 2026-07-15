using BTCPayServer.Abstractions.Constants;
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

        // Visiting the dashboard counts as "first use" for lazy process start, same as any RPC
        // through WavelengthLightningClient - see WavedProcessManager.EnsureStartedAsync. A
        // failure to start (bad flags, waved crashed, etc.) must never bubble up past this
        // action - it would 500 the whole request instead of showing what actually went wrong.
        // Starting the process is NOT the same as creating a wallet - see the check below.
        try
        {
            await processManager.EnsureStartedAsync(storeId, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or RpcException)
        {
            var detail = ex is RpcException rpcEx ? rpcEx.Status.Detail : ex.Message;
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = false, StartupError = detail });
        }

        var wallet = processManager.GetWalletClient(storeId);
        if (wallet is null)
        {
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = false });
        }

        if (!await processManager.WalletExistsAsync(storeId, cancellationToken))
        {
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = true, WalletExists = false });
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
                WalletExists = true,
                ConfirmedSat = balance.ConfirmedSat,
                PendingInSat = balance.PendingInSat,
                PendingOutSat = balance.PendingOutSat,
                CreditAvailableSat = balance.CreditAvailableSat,
                Activity = (activity.Activity?.Entries ?? []).Select(ToRow).ToList()
            });
        }
        catch (RpcException ex)
        {
            return View(new WavelengthWalletViewModel
            {
                StoreId = storeId, IsRunning = true, WalletExists = true, StartupError = ex.Status.Detail
            });
        }
    }

    // The only place a wallet is ever created - see CreateWalletAsync's doc comment for why
    // this must stay an explicit, human-initiated action rather than an automatic side effect.
    [HttpPost("create")]
    public async Task<IActionResult> CreateWallet(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        try
        {
            await processManager.EnsureStartedAsync(storeId, cancellationToken: cancellationToken);
            var mnemonic = await processManager.CreateWalletAsync(storeId);
            if (mnemonic is null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "A wallet already exists for this store.";
                return RedirectToAction(nameof(Index), new { storeId });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or RpcException)
        {
            TempData[WellKnownTempData.ErrorMessage] = ex is RpcException rpcEx ? rpcEx.Status.Detail : ex.Message;
            return RedirectToAction(nameof(Index), new { storeId });
        }

        // Mnemonic redirects here and shows it via WavedMnemonicOnceCache.TakeOnce - this is the
        // primary, reliable path now (the user is already looking at the page that triggered
        // creation); the notification CreateWalletAsync also sends is only a fallback in case
        // this response never renders.
        return RedirectToAction(nameof(Mnemonic), new { storeId });
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
