namespace BTCPayServer.Plugins.Wavelength.ViewModels;

/// <summary>
/// The "wavecli exit plan --outpoint ..." preview (wavewalletrpc.WalletService/GetExitPlan) -
/// read-only, no side effects. Shows what funding a unilateral exit of the selected VTXOs would
/// need before anyone actually starts one via WalletService.Exit.
/// </summary>
public sealed class WavelengthExitPlanViewModel
{
    public string StoreId { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public List<WavelengthExitPlanEntryViewModel> Plans { get; set; } = [];

    public long FeeRateSatPerVbyte { get; set; }
    public bool CanStart { get; set; }
    public long TotalFundingShortfallSat { get; set; }
    public long TotalRecommendedFundingSat { get; set; }
}

public sealed class WavelengthExitPlanEntryViewModel
{
    public string Outpoint { get; set; } = "";

    /// <summary>Backing-wallet address to fund - null when there's no shortfall (CanStart is true).</summary>
    public string? FundingAddress { get; set; }
    public uint RequiredConfirmations { get; set; }
    public uint RequiredFeeUtxoCount { get; set; }
    public uint UsableFeeUtxoCount { get; set; }
    public long RecommendedUtxoAmountSat { get; set; }
    public long RecommendedTotalFundingSat { get; set; }
    public long FundingShortfallSat { get; set; }
    public bool CanStart { get; set; }

    /// <summary>Why CanStart is false - null when CanStart is true.</summary>
    public string? InfeasibilityReason { get; set; }

    /// <summary>Per-outpoint failure (e.g. VTXO not found) - null on success.</summary>
    public string? Error { get; set; }

    public bool ExitJobFound { get; set; }
    public string? ExitStatus { get; set; }
}
