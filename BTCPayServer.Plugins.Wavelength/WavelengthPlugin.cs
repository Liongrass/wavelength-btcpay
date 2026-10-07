using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Wavelength.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Wavelength;

public class WavelengthPlugin : BaseBTCPayServerPlugin
{
    public override string Identifier => "BTCPayServer.Plugins.Wavelength";
    public override string Name => "Wavelength";
    public override string Description =>
        "Adds wavelength, a self-custodial Ark/Lightning/on-chain wallet, to BTCPay Server as a Lightning wallet backend.";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.2" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<WavedConfiguration>();
        services.AddSingleton<WavedWalletCredentialStore>();
        services.AddSingleton<WavedMnemonicPendingCache>();
        services.AddSingleton<WavedStoreSettingsStore>();
        services.AddSingleton<IWavedStoreApprovals>(sp => sp.GetRequiredService<WavedStoreSettingsStore>());
        services.AddSingleton<IWavelengthServerSettingsSource, WavelengthServerSettingsProvider>();
        services.AddSingleton<IWavedTokenSeeds>(sp => sp.GetRequiredService<WavedStoreSettingsStore>());
        // WavedStoreTokenProtector reads the store's settings row through the cache above, and the
        // connection-string handler reads the seed through the protector on every Lightning call
        // core makes - see those types for why the settings read is cached rather than repeated.
        services.AddSingleton<WavedStoreTokenProtector>();
        // Approval facts and the server-wide settings, both read at save time and on the paths that
        // start a process - see those types for what each one is deciding.

        services.AddSingleton<WavedProcessManager>();
        services.AddHostedService(sp => sp.GetRequiredService<WavedProcessManager>());

        services.AddSingleton<ILightningConnectionStringHandler, WavelengthLightningConnectionStringHandler>();

        services.AddUIExtension("ln-payment-method-setup-tab", "/Views/Lightning/Wavelength/LNPaymentMethodSetupTab.cshtml");
        services.AddUIExtension("store-wallets-nav", "Wavelength/NavExtension");

        base.Execute(services);
    }
}
