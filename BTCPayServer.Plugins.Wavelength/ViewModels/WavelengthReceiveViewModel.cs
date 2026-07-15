namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthReceiveViewModel
{
    public string StoreId { get; set; } = "";
    public long AmountSat { get; set; }
    public string? Memo { get; set; }

    public string? Invoice { get; set; }
    public string? ErrorMessage { get; set; }
}
