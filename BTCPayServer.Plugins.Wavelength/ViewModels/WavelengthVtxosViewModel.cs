namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthVtxosViewModel
{
    public string StoreId { get; set; } = "";
    public bool IsRunning { get; set; }
    public string? ErrorMessage { get; set; }
    public List<WavelengthVtxoRowViewModel> Vtxos { get; set; } = [];
}

/// <summary>
/// One row of "wavecli ark vtxos list" (waverpc.DaemonService/ListVTXOs) - every field waved's
/// VTXO message carries, not just the outpoint/status/amount/batch-expiry shown in the closed
/// row, so a click can expand the same fetched data into full detail without a second RPC.
/// </summary>
public sealed class WavelengthVtxoRowViewModel
{
    public string Outpoint { get; set; } = "";
    public long AmountSat { get; set; }
    public string Status { get; set; } = "";
    public int BatchExpiry { get; set; }

    public string RoundId { get; set; } = "";
    public int CreatedHeight { get; set; }
    public uint RelativeExpiry { get; set; }
    public string PkScript { get; set; } = "";
    public string CommitmentTxid { get; set; } = "";
    public uint ChainDepth { get; set; }
    public string SpentByTxid { get; set; } = "";

    public bool HasExpiryInfo { get; set; }
    public string ExpiryStatus { get; set; } = "";
    public int CurrentHeight { get; set; }
    public int BlocksRemaining { get; set; }
    public int RefreshThresholdBlocks { get; set; }
    public int CriticalThresholdBlocks { get; set; }

    public bool HasSettlement { get; set; }
    public string SettlementTxid { get; set; } = "";
    public int SettlementHeight { get; set; }
}
