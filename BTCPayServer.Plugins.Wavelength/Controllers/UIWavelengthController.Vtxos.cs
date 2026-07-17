using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Waverpc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

public partial class UIWavelengthController
{
    // The "wavecli ark vtxos list" equivalent. ListVTXOs already returns every field of each
    // VTXO (round id, expiry info, settlement, scripts, ...), so the "click a row for full
    // details" UI is a client-side expand of data already fetched here - no second RPC per VTXO.
    [HttpGet("vtxos")]
    public async Task<IActionResult> Vtxos(string storeId, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        if (await RedirectIfNoWalletAsync(storeId, cancellationToken) is { } redirect)
            return redirect;

        var vm = new WavelengthVtxosViewModel { StoreId = storeId, IsRunning = true };

        var daemon = processManager.GetDaemonClient(storeId);
        if (daemon is null)
        {
            vm.IsRunning = false;
            return View(vm);
        }

        try
        {
            var response = await daemon.ListVTXOsAsync(new ListVTXOsRequest(), cancellationToken: cancellationToken);
            vm.Vtxos = response.Vtxos.Select(ToVtxoRow).ToList();
        }
        catch (RpcException ex)
        {
            vm.ErrorMessage = ex.Status.Detail;
        }

        return View(vm);
    }

    private static WavelengthVtxoRowViewModel ToVtxoRow(VTXO vtxo)
    {
        var row = new WavelengthVtxoRowViewModel
        {
            Outpoint = vtxo.Outpoint,
            AmountSat = vtxo.AmountSat,
            Status = vtxo.Status.ToString(),
            BatchExpiry = vtxo.BatchExpiry,
            RoundId = vtxo.RoundId,
            CreatedHeight = vtxo.CreatedHeight,
            RelativeExpiry = vtxo.RelativeExpiry,
            PkScript = vtxo.PkScript,
            CommitmentTxid = vtxo.CommitmentTxid,
            ChainDepth = vtxo.ChainDepth,
            SpentByTxid = vtxo.SpentByTxid
        };

        if (vtxo.ExpiryInfo is not null)
        {
            row.HasExpiryInfo = true;
            row.ExpiryStatus = vtxo.ExpiryInfo.Status.ToString();
            row.CurrentHeight = vtxo.ExpiryInfo.CurrentHeight;
            row.BlocksRemaining = vtxo.ExpiryInfo.BlocksRemaining;
            row.RefreshThresholdBlocks = vtxo.ExpiryInfo.RefreshThresholdBlocks;
            row.CriticalThresholdBlocks = vtxo.ExpiryInfo.CriticalThresholdBlocks;
        }

        if (vtxo.Settlement is not null)
        {
            row.HasSettlement = true;
            row.SettlementTxid = vtxo.Settlement.Txid;
            row.SettlementHeight = vtxo.Settlement.Height;
        }

        return row;
    }
}
