using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using Grpc.Core;
using NBitcoin;
using Wavewalletrpc;
using WalletServiceClient = Wavewalletrpc.WalletService.WalletServiceClient;

namespace BTCPayServer.Plugins.Wavelength.Lightning;

/// <summary>
/// ILightningClient backed by a per-store waved instance, talking to it over the vendored
/// wavewalletrpc gRPC client (see Protos/ and scripts/check-proto-drift.sh). Scope mirrors
/// bark-btcpay's BarkLightningClient: CreateInvoice/GetInvoice/Pay/GetBalance/Listen are
/// implemented; wavelength is an Ark/Lightning-swap wallet with no channels, so
/// OpenChannel/GetDepositAddress/ConnectTo/ListChannels stay NotSupported.
/// </summary>
public sealed class WavelengthLightningClient(
    WavedProcessManager processManager,
    Network network,
    string storeId) : ILightningClient
{
    private async Task<WalletServiceClient> EnsureReadyAsync(CancellationToken cancellation)
    {
        await processManager.EnsureStartedAsync(storeId, cancellation);
        return processManager.GetWalletClient(storeId)
            ?? throw new InvalidOperationException($"waved for store {storeId} is not running");
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
        => await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest,
        CancellationToken cancellation = default)
    {
        var wallet = await EnsureReadyAsync(cancellation);
        var amountSats = (ulong)createInvoiceRequest.Amount.ToUnit(LightMoneyUnit.Satoshi);

        var response = await wallet.RecvAsync(new RecvRequest
        {
            AmtSat = amountSats,
            Memo = createInvoiceRequest.Description ?? string.Empty
        }, cancellationToken: cancellation);

        // entry.Id is the Lightning payment hash for swap-backed receive rows (see
        // WalletEntry.id doc comment in wallet.proto) - no BOLT11 parsing needed to recover it.
        return new LightningInvoice
        {
            Id = response.Entry.Id,
            Amount = createInvoiceRequest.Amount,
            BOLT11 = response.Invoice,
            PaymentHash = response.Entry.Id,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = DateTimeOffset.UtcNow + createInvoiceRequest.Expiry
        };
    }

    public async Task<LightningInvoice?> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var wallet = await EnsureReadyAsync(cancellation);
        _ = wallet;
        var inspection = processManager.GetWalletInspectionClient(storeId)
            ?? throw new InvalidOperationException($"waved for store {storeId} is not running");

        try
        {
            var response = await inspection.InspectActivityAsync(
                new InspectActivityRequest { Id = invoiceId }, cancellationToken: cancellation);
            return ToLightningInvoice(response.Entry);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<LightningInvoice?> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
        => GetInvoice(paymentHash.ToString(), cancellation);

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
        => Pay(bolt11, new PayInvoiceParams(), cancellation);

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(bolt11))
            return new PayResponse(PayResult.Error, "BOLT11 invoice is required");

        try
        {
            var wallet = await EnsureReadyAsync(cancellation);

            var prepareRequest = new PrepareSendRequest { Invoice = bolt11 };
            if (payParams.Amount is { } amount)
                prepareRequest.AmtSat = (ulong)amount.ToUnit(LightMoneyUnit.Satoshi);

            var prepared = await wallet.PrepareSendAsync(prepareRequest, cancellationToken: cancellation);
            var sent = await wallet.SendAsync(
                new SendRequest { SendIntentId = prepared.SendIntentId }, cancellationToken: cancellation);

            var entry = await PollUntilTerminalAsync(sent.Entry.Id, cancellation);

            return entry.Status switch
            {
                EntryStatus.Complete => new PayResponse(PayResult.Ok)
                {
                    Details = new PayDetails
                    {
                        PaymentHash = uint256.Parse(entry.Id),
                        Status = LightningPaymentStatus.Complete,
                        TotalAmount = LightMoney.Satoshis(sent.ActualAmountSat)
                    }
                },
                EntryStatus.Failed => new PayResponse(PayResult.Error,
                    string.IsNullOrEmpty(entry.FailureReason) ? "Payment failed" : entry.FailureReason),
                _ => new PayResponse(PayResult.Error,
                    "Payment is still pending after 30s; check the store's activity history for its final status")
            };
        }
        catch (RpcException ex)
        {
            return new PayResponse(PayResult.Error, ex.Status.Detail);
        }
    }

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
        => throw new NotSupportedException("BOLT11 is required");

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var wallet = await EnsureReadyAsync(cancellation);
        var response = await wallet.BalanceAsync(new BalanceRequest(), cancellationToken: cancellation);

        return new LightningNodeBalance
        {
            OffchainBalance = new OffchainBalance
            {
                Local = LightMoney.Satoshis(response.ConfirmedSat)
            }
        };
    }

    // wavelength is an Ark/Lightning-swap wallet, not an LN node with its own getinfo-shaped
    // identity/channel graph - matches bark-btcpay's own choice to leave this unsupported.
    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        var wallet = await EnsureReadyAsync(cancellation);
        return new WavelengthLightningInvoiceListener(wallet, cancellation);
    }

    // --- Not yet implemented: no wavewalletrpc-side blocker, just not wired up this pass. ---

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    // --- Unsupported: wavelength is an Ark/Lightning-swap wallet, not an LN node - no channels. ---

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
        => throw new NotSupportedException();

    public override string ToString() => $"type=wavelength;store-id={storeId}";

    private static LightningInvoice ToLightningInvoice(WalletEntry entry) => new()
    {
        Id = entry.Id,
        Amount = LightMoney.Satoshis(Math.Abs(entry.AmountSat)),
        PaymentHash = entry.Id,
        Status = entry.Status switch
        {
            EntryStatus.Complete => LightningInvoiceStatus.Paid,
            EntryStatus.Failed => LightningInvoiceStatus.Expired,
            _ => LightningInvoiceStatus.Unpaid
        },
        PaidAt = entry.Status == EntryStatus.Complete
            ? DateTimeOffset.FromUnixTimeSeconds(entry.UpdatedAtUnix)
            : null
    };

    private async Task<WalletEntry> PollUntilTerminalAsync(string entryId, CancellationToken cancellation)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var delayMs = 200;

        while (!timeoutCts.IsCancellationRequested)
        {
            var inspection = processManager.GetWalletInspectionClient(storeId);
            if (inspection is not null)
            {
                var response = await inspection.InspectActivityAsync(
                    new InspectActivityRequest { Id = entryId }, cancellationToken: timeoutCts.Token);
                if (response.Entry.Status != EntryStatus.Pending)
                    return response.Entry;
            }

            try { await Task.Delay(delayMs, timeoutCts.Token); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellation.IsCancellationRequested) { break; }
            delayMs = Math.Min(delayMs * 2, 2000);
        }

        cancellation.ThrowIfCancellationRequested();
        // Timed out waiting for a terminal state - report "still pending" rather than throwing.
        return new WalletEntry { Id = entryId, Status = EntryStatus.Pending };
    }
}
