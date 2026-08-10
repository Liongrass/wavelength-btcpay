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
        services.AddSingleton<WavedStoreTokenProtector>();

        services.AddSingleton<WavedProcessManager>();
        services.AddHostedService(sp => sp.GetRequiredService<WavedProcessManager>());

        services.AddSingleton<ILightningConnectionStringHandler, WavelengthLightningConnectionStringHandler>();

        services.AddUIExtension("ln-payment-method-setup-tab", "/Views/Lightning/Wavelength/LNPaymentMethodSetupTab.cshtml");
        services.AddUIExtension("store-wallets-nav", "Wavelength/NavExtension");

        base.Execute(services);
    }
}
