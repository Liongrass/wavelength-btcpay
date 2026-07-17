namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthReceiveViewModel
{
    public string StoreId { get; set; } = "";

    /// <summary>The amount, in whatever unit <see cref="Currency"/> currently selects.</summary>
    public decimal? Amount { get; set; }

    /// <summary>"sats" (the literal amount, no conversion) or any fiat code BTCPay can rate BTC against.</summary>
    public string Currency { get; set; } = "sats";
    public string? Memo { get; set; }

    // Display-only reference rate for the grey "1 BTC ≈ ..." line and the live client-side
    // conversion - always for the store's default currency when Currency is "sats", or for
    // Currency itself otherwise. Never trusted for the actual invoice amount: the server
    // re-fetches/re-derives the real sats amount itself at submit time (see
    // UIWavelengthController.Receive.cs) rather than relying on anything round-tripped from here.
    public decimal? Rate { get; set; }
    public string? RateCurrency { get; set; }
    public int RateDivisibility { get; set; } = 2;
    public string? RateErrorMessage { get; set; }

    public string? Invoice { get; set; }
    public string? ErrorMessage { get; set; }
}
