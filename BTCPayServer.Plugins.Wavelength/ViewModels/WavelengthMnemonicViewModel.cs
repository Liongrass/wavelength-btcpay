namespace BTCPayServer.Plugins.Wavelength.ViewModels;

// Wraps the mnemonic in a real type rather than passing the bare string to View(...) - a raw
// string there resolves to Controller.View(string viewName), not View(object model), which is
// exactly the bug that leaked a mnemonic into the BTCPay log as a "view not found" error message
// (the mnemonic became the search path). A dedicated model type makes that overload impossible
// to hit by accident again, here or anywhere else this pattern gets copied.
public sealed class WavelengthMnemonicViewModel
{
    public string? Mnemonic { get; set; }
}
