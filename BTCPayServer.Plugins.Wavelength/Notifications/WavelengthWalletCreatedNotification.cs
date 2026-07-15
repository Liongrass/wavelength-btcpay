using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Wavelength.Controllers;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.Wavelength.Notifications;

/// <summary>
/// Alerts the store owner that a new wallet was just created and its recovery mnemonic is
/// waiting to be viewed exactly once (see WavedMnemonicOnceCache). The mnemonic itself never
/// goes into this notification's body - notifications are persisted to BTCPay's own DB, which
/// would defeat the whole point of not storing it durably.
/// </summary>
public class WavelengthWalletCreatedNotification : BaseNotification
{
    private const string Type = "wavelength-wallet-created";

    public override string Identifier => Type;
    public override string NotificationType => Type;

    public string StoreId { get; set; } = "";

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options)
        : NotificationHandler<WavelengthWalletCreatedNotification>
    {
        public override string NotificationType => Type;

        public override (string identifier, string name)[] Meta =>
        [
            (Type, "Wavelength wallet created"),
        ];

        protected override void FillViewModel(WavelengthWalletCreatedNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = "A new Wavelength wallet was created for this store. View and record its " +
                      "recovery phrase now - it can only be shown once and is not stored anywhere.";
            vm.ActionLink = linkGenerator.GetPathByAction(
                nameof(UIWavelengthController.Mnemonic),
                "UIWavelength",
                new { storeId = notification.StoreId },
                options.RootPath);
        }
    }
}
