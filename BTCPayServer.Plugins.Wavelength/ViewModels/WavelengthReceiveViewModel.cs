namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthReceiveViewModel
{
    public string StoreId { get; set; } = "";
    public long AmountSat { get; set; }
    public string? Memo { get; set; }

    // Fiat conversion, via BTCPay's own store-configured rate rules (RateFetcher) - purely a
    // client-side input convenience. AmountSat above remains the only value actually sent to
    // waved; JS keeps this in sync with it using Rate/FiatDivisibility, the same way
    // BTCPayServer.Plugins.Wallets' WalletSend.cshtml links its own sats/fiat inputs.
    public string Currency { get; set; } = "USD";
    public decimal? AmountFiat { get; set; }
    public decimal? Rate { get; set; }
    public int FiatDivisibility { get; set; } = 2;
    public string? RateErrorMessage { get; set; }

    public string? Invoice { get; set; }
    public string? ErrorMessage { get; set; }
}
