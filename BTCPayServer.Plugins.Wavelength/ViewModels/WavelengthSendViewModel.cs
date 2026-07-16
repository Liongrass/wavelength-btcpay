namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthSendViewModel
{
    public string StoreId { get; set; } = "";

    /// <summary>The raw Lightning invoice or on-chain address pasted in the first step.</summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Set once <see cref="Destination"/> has been classified - carried through a hidden field
    /// on the amount/confirm stages so the view knows which form to render without re-parsing.
    /// Lightning invoices carry their own amount, so they skip straight from paste to the confirm
    /// preview; on-chain addresses need an extra step to ask how much (or "send all") before
    /// PrepareSend can run at all.
    /// </summary>
    public bool IsOnchain { get; set; }

    /// <summary>On-chain only: how much to send, or <see cref="SweepAll"/> to empty the wallet.</summary>
    public long? AmountSat { get; set; }
    public bool SweepAll { get; set; }

    // Populated after a successful "preview" (PrepareSend) step; round-tripped through the form
    // as hidden fields so "confirm" can consume the same intent without re-parsing the destination.
    public string? SendIntentId { get; set; }
    public long? PreviewAmountSat { get; set; }
    public long? PreviewFeeSat { get; set; }
    public bool PreviewFeeKnown { get; set; }
    public string? PreviewRail { get; set; }
    public string? PreviewDestinationSummary { get; set; }

    public string? ErrorMessage { get; set; }
}
