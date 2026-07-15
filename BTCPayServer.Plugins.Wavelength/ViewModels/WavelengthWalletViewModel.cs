namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthWalletViewModel
{
    public string StoreId { get; set; } = "";
    public bool IsRunning { get; set; }
    public bool WalletExists { get; set; } = true;
    public string? StartupError { get; set; }
    public long ConfirmedSat { get; set; }
    public long PendingInSat { get; set; }
    public long PendingOutSat { get; set; }
    public ulong CreditAvailableSat { get; set; }
    public List<WavelengthActivityRowViewModel> Activity { get; set; } = [];
}

public sealed class WavelengthActivityRowViewModel
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public long AmountSat { get; set; }
    public string Counterparty { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Note { get; set; }
}
