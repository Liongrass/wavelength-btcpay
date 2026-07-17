using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Wavelength.ViewModels;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Waverpc;
using Wavewalletrpc;

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

    // The "wavecli exit plan --outpoint ..." equivalent - a read-only preview of what funding a
    // unilateral exit of the selected VTXOs would need. Does not itself start an exit (that's
    // WalletService.Exit, not wired up here); this only ever previews.
    [HttpPost("vtxos/exit-plan")]
    public async Task<IActionResult> ExitPlan(string storeId, string[] outpoints, CancellationToken cancellationToken)
    {
        var store = HttpContext.GetStoreDataOrNull();
        if (store is null) return NotFound();
        if (GetWavelengthConfig(store) is null) return RedirectToLightningSetup(storeId);

        if (outpoints is null || outpoints.Length == 0)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Select at least one VTXO first.";
            return RedirectToAction(nameof(Vtxos), new { storeId });
        }

        if (await RedirectIfNoWalletAsync(storeId, cancellationToken) is { } redirect)
            return redirect;

        var vm = new WavelengthExitPlanViewModel { StoreId = storeId };

        var wallet = processManager.GetWalletClient(storeId);
        if (wallet is null)
        {
            vm.ErrorMessage = "This store's waved instance is not running.";
            return View(vm);
        }

        try
        {
            var request = new GetExitPlanRequest();
            request.Outpoints.AddRange(outpoints);
            var response = await wallet.GetExitPlanAsync(request, cancellationToken: cancellationToken);

            vm.FeeRateSatPerVbyte = response.FeeRateSatPerVbyte;
            vm.CanStart = response.CanStart;
            vm.TotalFundingShortfallSat = response.TotalFundingShortfallSat;
            vm.TotalRecommendedFundingSat = response.TotalRecommendedFundingSat;
            vm.Plans = response.Plans.Select(ToExitPlanEntry).ToList();
        }
        catch (RpcException ex)
        {
            vm.ErrorMessage = ex.Status.Detail;
        }

        return View(vm);
    }

    private static WavelengthExitPlanEntryViewModel ToExitPlanEntry(ExitPlanEntry entry) => new()
    {
        Outpoint = entry.Outpoint,
        FundingAddress = string.IsNullOrEmpty(entry.FundingAddress) ? null : entry.FundingAddress,
        RequiredConfirmations = entry.RequiredConfirmations,
        RequiredFeeUtxoCount = entry.RequiredFeeUtxoCount,
        UsableFeeUtxoCount = entry.UsableFeeUtxoCount,
        RecommendedUtxoAmountSat = entry.RecommendedUtxoAmountSat,
        RecommendedTotalFundingSat = entry.RecommendedTotalFundingSat,
        FundingShortfallSat = entry.FundingShortfallSat,
        CanStart = entry.CanStart,
        InfeasibilityReason = entry.InfeasibilityReason == ExitInfeasibilityReason.Unspecified
            ? null
            : entry.InfeasibilityReason.ToString(),
        Error = string.IsNullOrEmpty(entry.Error) ? null : entry.Error,
        ExitJobFound = entry.ExitJobFound,
        ExitStatus = entry.ExitJobFound ? entry.ExitStatus.ToString() : null
    };
}
