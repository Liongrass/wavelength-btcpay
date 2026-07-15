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
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.9" }
    ];

    public override void Execute(IServiceCollection services)
    {
        // waved is started with --no-tls --no-macaroons (loopback-only, plugin-owned - see
        // WavedProcessManager), so the gRPC client needs unencrypted HTTP/2 (h2c) enabled.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        services.AddSingleton<WavedConfiguration>();

        services.AddSingleton<WavedProcessManager>();
        services.AddHostedService(sp => sp.GetRequiredService<WavedProcessManager>());

        services.AddSingleton<ILightningConnectionStringHandler, WavelengthLightningConnectionStringHandler>();

        services.AddUIExtension("ln-payment-method-setup-tab", "/Views/Lightning/LNPaymentMethodSetupTab.cshtml");

        base.Execute(services);
    }
}
