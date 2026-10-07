using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Wavelength.Lightning;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Waverpc;
using Wavewalletrpc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    [HttpGet]
    public async Task<IActionResult> Index(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        var wavelengthConfig = GetWavelengthConfig(store);
        if (wavelengthConfig is null) return RedirectToLightningSetup(storeId);

        // The approval boundary applies here first: this page starts the process, so a store that
        // has not been approved for Wavelength must not be started by simply visiting it. Shown as
        // an error on the page rather than a redirect, since the reason belongs in front of the
        // person who is looking at this store. See IsWavelengthAllowedForCurrentUserAsync.
        if (!await IsWavelengthAllowedForCurrentUserAsync(storeId, cancellationToken))
        {
            return View(new WavelengthWalletViewModel
            {
                StoreId = storeId, IsRunning = false, StartupError = NotApprovedMessage
            });
        }

        // Visiting the dashboard counts as "first use" for lazy process start, same as any RPC
        // through WavelengthLightningClient - see WavedProcessManager.EnsureStartedAsync. A
        // failure to start (bad flags, waved crashed, etc.) must never bubble up past this
        // action - it would 500 the whole request instead of showing what actually went wrong.
        // Starting the process is NOT the same as creating a wallet - see the check below.
        //
        // Flags are re-parsed from the CURRENT connection string here (not passed as null) so a
        // first-ever start (or a restart after a crash/stop/delete) picks up whatever was just
        // saved on the Lightning setup page - see RedirectIfNoWalletAsync's doc comment for why
        // that matters.
        if (!WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
                wavelengthConfig.ConnectionString!, out var extraFlags, out var parseError,
                allowLocal: (await storeSettings.GetAsync(storeId, cancellationToken)).AllowLocalEndpoints))
        {
            return View(new WavelengthWalletViewModel
            {
                StoreId = storeId, IsRunning = false, StartupError = TruncateErrorText(parseError)
            });
        }

        try
        {
            await processManager.EnsureStartedAsync(storeId, extraFlags, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or RpcException)
        {
            var detail = TruncateErrorText(ex is RpcException rpcEx ? rpcEx.Status.Detail : ex.Message);
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = false, StartupError = detail });
        }

        var wallet = processManager.GetWalletClient(storeId);
        if (wallet is null)
        {
            return View(new WavelengthWalletViewModel { StoreId = storeId, IsRunning = false });
        }

        if (!await processManager.WalletExistsAsync(storeId, cancellationToken))
        {
            if (processManager.TryGetCreationError(storeId, out var creationError))
                TempData[WellKnownTempData.ErrorMessage] = TruncateErrorText(creationError);

            var isCreating = processManager.IsCreatingWallet(storeId);
            var vm = new WavelengthWalletViewModel
            {
                StoreId = storeId,
                IsRunning = true,
                WalletExists = false,
                IsCreatingWallet = isCreating
            };

            // Best-effort only - a failed GetInfo here just means the spinner shows without a
            // sync-progress hint, not an error worth surfacing on top of "still creating".
            if (isCreating && processManager.GetDaemonClient(storeId) is { } daemon)
            {
                try
                {
                    var info = await daemon.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
                    vm.SyncBlockHeight = info.BlockHeight;
                    vm.SyncWalletState = info.WalletState.ToString();
                }
                catch (RpcException) { }
            }

            return View(vm);
        }

        // Creation may have finished in the background (see CreateWallet/StartCreateWalletAsync)
        // while nobody was waiting on the request that started it - if its mnemonic is still
        // sitting unclaimed, this visit is what shows it, instead of silently landing on the
        // normal dashboard and losing it for good.
        if (mnemonicCache.HasPending(storeId))
            return RedirectToAction(nameof(Mnemonic), new { storeId });

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
                StoreId = storeId, IsRunning = true, WalletExists = true,
                StartupError = TruncateErrorText(ex.Status.Detail)
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
        var wavelengthConfig = GetWavelengthConfig(store);
        if (wavelengthConfig is null) return RedirectToLightningSetup(storeId);

        // The other half of the approval boundary - see IsWavelengthAllowedForCurrentUserAsync.
        // Gated here as well as at connection-string save time because this button reaches
        // StartCreateWalletAsync, which mints a seed, without any connection string being
        // re-saved: a store that was approved once and whose owner has since been demoted (or a
        // server that turned AllowForAllStores back off) is still stopped from creating one.
        if (!await IsWavelengthAllowedForCurrentUserAsync(storeId, cancellationToken))
        {
            TempData[WellKnownTempData.ErrorMessage] = NotApprovedMessage;
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (!WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
                wavelengthConfig.ConnectionString!, out var extraFlags, out var parseError,
                allowLocal: (await storeSettings.GetAsync(storeId, cancellationToken)).AllowLocalEndpoints))
        {
            TempData[WellKnownTempData.ErrorMessage] = parseError;
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

        // Deliberately not awaited - waved's InitWallet can take long enough that waiting for it
        // inline here risks a reverse proxy's own gateway timeout firing before waved responds
        // (see StartCreateWalletAsync's doc comment). The dashboard we redirect to instead shows a
        // "creating" state and auto-refreshes until WalletExistsAsync/HasPending picks it up.
        _ = processManager.StartCreateWalletAsync(storeId);
        return RedirectToAction(nameof(Index), new { storeId });
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
