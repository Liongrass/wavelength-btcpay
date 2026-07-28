using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Wavelength.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Waverpc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    // The "wavecli getinfo" equivalent, plus how this store's waved is configured on the
    // plugin side (datadir, port, extra flags) - see WavelengthAdvancedViewModel.
    [HttpGet("advanced")]
    public async Task<IActionResult> Advanced(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        var isRunning = processManager.IsRunning(storeId);
        var uri = processManager.GetStoreUri(storeId);
        var flags = processManager.GetRunningFlags(storeId);
        if (flags is null)
        {
            var settings = await storeRepository.GetSettingAsync<WavedStoreSettings>(storeId, WavedStoreSettings.SettingsKey);
            flags = settings?.ExtraWavedFlags ?? new Dictionary<string, string>();
        }

        var vm = new WavelengthAdvancedViewModel
        {
            StoreId = storeId,
            IsRunning = isRunning,
            DataDir = config.GetStoreDataDir(storeId),
            Port = uri?.Port,
            Flags = new Dictionary<string, string>(flags)
        };

        var daemon = processManager.GetDaemonClient(storeId);
        if (daemon is not null)
        {
            try
            {
                var info = await daemon.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
                vm.Version = info.Version;
                vm.Commit = info.Commit;
                vm.Network = info.Network;
                vm.BlockHeight = info.BlockHeight;
                vm.ServerConnected = info.ServerConnected;
                vm.WalletType = info.WalletType;
                vm.WalletState = info.WalletState.ToString();
                vm.IdentityPubkey = info.IdentityPubkey;
            }
            catch (RpcException)
            {
                // Leave the GetInfo fields blank - the plugin-side config below is still useful.
            }
        }

        return View(vm);
    }

    // Stops and restarts this store's waved with flags freshly re-parsed from the store's
    // CURRENT live connection string, not whatever was last persisted - see
    // WavedProcessManager.RestartAsync. Deliberately does not touch datadir/network/wallet
    // type itself; if those changed, the operator needs to delete the wallet first (see the
    // disclaimer on the Advanced page) since waved can't switch backend/network on an existing
    // wallet in place.
    [HttpPost("advanced/restart")]
    public async Task<IActionResult> Restart(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        var wavelengthConfig = GetWavelengthConfig(store);
        if (wavelengthConfig?.ConnectionString is null) return RedirectToLightningSetup(storeId);

        if (!WavelengthLightningConnectionStringHandler.TryParseExtraFlags(
                wavelengthConfig.ConnectionString, out var extraFlags, out var parseError))
        {
            TempData[WellKnownTempData.ErrorMessage] = parseError;
            return RedirectToAction(nameof(Advanced), new { storeId });
        }

        try
        {
            await processManager.RestartAsync(storeId, extraFlags, cancellationToken);
            TempData[WellKnownTempData.SuccessMessage] = "waved restarted with the current connection string's flags.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or RpcException)
        {
            TempData[WellKnownTempData.ErrorMessage] = ex is RpcException rpcEx ? rpcEx.Status.Detail : ex.Message;
        }

        return RedirectToAction(nameof(Advanced), new { storeId });
    }
}
