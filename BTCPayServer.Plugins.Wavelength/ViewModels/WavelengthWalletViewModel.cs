namespace BTCPayServer.Plugins.Wavelength.ViewModels;

public sealed class WavelengthWalletViewModel
{
    public string StoreId { get; set; } = "";
    public bool IsRunning { get; set; }
    public bool WalletExists { get; set; } = true;
    public bool IsCreatingWallet { get; set; }

    // Shown alongside the "creating your wallet" spinner - lets a chain-backend sync in
    // progress (the most common reason creation takes a long time) read as visible progress
    // instead of a plain, indistinguishable-from-hung spinner. Null if unavailable (e.g. waved's
    // GetInfo call itself failed).
    public uint? SyncBlockHeight { get; set; }
    public string? SyncWalletState { get; set; }

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
