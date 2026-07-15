namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthDeleteViewModel
{
    public string StoreId { get; set; } = "";
    public bool IsRunning { get; set; }
    public long? ConfirmedSat { get; set; }
    public long PendingInSat { get; set; }
    public long PendingOutSat { get; set; }
    public string? ErrorMessage { get; set; }
}
