namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthSendViewModel
{
    public string StoreId { get; set; } = "";

    /// <summary>A BOLT-11 invoice - the only destination type wired up so far (see PrepareSend/Send in WalletServiceClient).</summary>
    public string? Destination { get; set; }
    public long? AmountSat { get; set; }

    // Populated after a successful "preview" (PrepareSend) step; round-tripped through the form
    // as hidden fields so "confirm" can consume the same intent without re-parsing the invoice.
    public string? SendIntentId { get; set; }
    public long? PreviewAmountSat { get; set; }
    public long? PreviewFeeSat { get; set; }
    public bool PreviewFeeKnown { get; set; }
    public string? PreviewRail { get; set; }
    public string? PreviewDestinationSummary { get; set; }

    public string? ErrorMessage { get; set; }
}
