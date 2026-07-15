namespace BTCPayServer.Plugins.Wavelength.ViewModels;

/// <summary>
/// The "wavecli getinfo" equivalent (waverpc.DaemonService/GetInfo fields) plus how this
/// store's waved instance is configured on the plugin side - not from waved itself.
/// </summary>
public sealed class WavelengthAdvancedViewModel
{
    public string StoreId { get; set; } = "";
    public bool IsRunning { get; set; }

    public string? Version { get; set; }
    public string? Commit { get; set; }
    public string? Network { get; set; }
    public uint BlockHeight { get; set; }
    public bool ServerConnected { get; set; }
    public string? WalletType { get; set; }
    public string? WalletState { get; set; }
    public string? IdentityPubkey { get; set; }

    public string DataDir { get; set; } = "";
    public int? Port { get; set; }
    public Dictionary<string, string> Flags { get; set; } = [];
}
