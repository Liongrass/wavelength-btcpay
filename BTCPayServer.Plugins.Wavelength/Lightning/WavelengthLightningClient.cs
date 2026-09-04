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
/// bark-btcpay's BarkLightningClient: CreateInvoice/GetInvoice/Pay/GetPayment/GetBalance/Listen
/// are implemented; wavelength is an Ark/Lightning-swap wallet with no channels, so
/// OpenChannel/GetDepositAddress/ConnectTo/ListChannels stay NotSupported.
/// </summary>
public sealed class WavelengthLightningClient(
    WavedProcessManager processManager,
    Network network,
    string storeId,
    string token,
    IReadOnlyDictionary<string, string> extraFlags) : ILightningClient
{
    private async Task<WalletServiceClient> EnsureReadyAsync(CancellationToken cancellation)
    {
        await processManager.EnsureStartedAsync(storeId, extraFlags, cancellation);
        var wallet = processManager.GetWalletClient(storeId)
            ?? throw new InvalidOperationException($"waved for store {storeId} is not running");

        // Wallet creation is a deliberate, human-initiated action (see
        // WavedProcessManager.CreateWalletAsync's doc comment) - it must never happen as a side
        // effect of an inbound payment attempt on a store nobody finished setting up. Fail with a
        // clear message instead of letting a raw "wallet is not ready (create first)" gRPC error
        // surface from whichever RPC below happens to be first to touch the wallet.
        if (!await processManager.WalletExistsAsync(storeId, cancellation))
        {
            throw new InvalidOperationException(
                $"This store's Wavelength wallet has not been created yet. Open the store's " +
                "Wavelength dashboard in BTCPay and click \"Create wallet\" first.");
        }

        return wallet;
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

        // PrepareSend is a local, no-funds-movement quote/intent step - wallet.proto's own doc
        // comment on the Send RPC spells this out: "intentionally intent-only... funds move" only
        // once Send is actually called. So a failure anywhere up to and including this call is
        // always safe to report as a definitive, retry-safe Error - nothing has been dispatched
        // yet, unlike everything from here on.
        WalletServiceClient wallet;
        PrepareSendResponse prepared;
        try
        {
            wallet = await EnsureReadyAsync(cancellation);

            var prepareRequest = new PrepareSendRequest { Invoice = bolt11 };
            if (payParams.Amount is { } amount)
                prepareRequest.AmtSat = (ulong)amount.ToUnit(LightMoneyUnit.Satoshi);

            prepared = await wallet.PrepareSendAsync(prepareRequest, cancellationToken: cancellation);
        }
        catch (RpcException ex)
        {
            return new PayResponse(PayResult.Error, ex.Status.Detail);
        }

        // From here on, a failure no longer means "nothing happened": Send actually dispatches
        // the payment, so an exception now - including one from the terminal-state poll below,
        // e.g. waved becoming Unavailable mid-restart - leaves the true outcome unknown; the send
        // may already have gone through on waved's side even though this call never got to see
        // it. Reporting Unknown (never Error) for every one of these ambiguous cases is what
        // makes BTCPay core hand the payout to LightningPendingPayoutListener (which settles it
        // later via GetPayment) instead of letting the automated payout processor retry - and
        // possibly double-pay - a send that might already have succeeded.
        try
        {
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
                // waved itself reports this as definitively, terminally failed - the only other
                // case (besides the PrepareSend failure above) where Error is correct here.
                EntryStatus.Failed => new PayResponse(PayResult.Error,
                    string.IsNullOrEmpty(entry.FailureReason) ? "Payment failed" : entry.FailureReason),
                // Still pending after the poll window - genuinely unknown, not failed.
                // GetPayment (keyed by this same entry.Id / payment hash) is how
                // LightningPendingPayoutListener settles it once a terminal state exists.
                _ => new PayResponse(PayResult.Unknown,
                    "Payment is still pending after 30s; check the store's activity history for its final status")
            };
        }
        catch (RpcException ex)
        {
            return new PayResponse(PayResult.Unknown, ex.Status.Detail);
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

    // The other half of the Pay()/PayResult.Unknown story: once a send's outcome is unknown,
    // BTCPayServer.Payments.Lightning.LightningPendingPayoutListener polls this on a timer (keyed
    // by the BOLT11's own payment hash) until it sees a terminal state. entry.Id IS that payment
    // hash for every send this client ever creates - Pay() only ever dispatches invoice sends,
    // and wallet.proto's WalletEntry.id doc comment says swap-backed SEND rows use the payment
    // hash as their id - so this is the exact same InspectActivity call GetInvoice already makes,
    // just returning the payment-shaped view of the entry instead of the invoice-shaped one.
    public async Task<LightningPayment?> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        var inspection = processManager.GetWalletInspectionClient(storeId)
            ?? throw new InvalidOperationException($"waved for store {storeId} is not running");

        try
        {
            var response = await inspection.InspectActivityAsync(
                new InspectActivityRequest { Id = paymentHash }, cancellationToken: cancellation);
            return ToLightningPayment(response.Entry);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

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

    public override string ToString()
    {
        // Echoes the exact token this client was actually constructed with, not a freshly
        // re-encrypted one - Data Protection's Protect() isn't guaranteed to produce the same
        // output twice for the same input, so re-deriving here would show a different-looking
        // (though equally valid) string than what's actually saved.
        var extra = string.Concat(extraFlags.Select(kv => $";{kv.Key}={kv.Value}"));
        return $"type=wavelength;token={token}{extra}";
    }

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

    private static LightningPayment ToLightningPayment(WalletEntry entry) => new()
    {
        Id = entry.Id,
        PaymentHash = entry.Id,
        Status = entry.Status switch
        {
            EntryStatus.Complete => LightningPaymentStatus.Complete,
            EntryStatus.Failed => LightningPaymentStatus.Failed,
            _ => LightningPaymentStatus.Pending
        },
        AmountSent = LightMoney.Satoshis(Math.Abs(entry.AmountSat)),
        Fee = LightMoney.Satoshis(entry.FeeSat),
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(entry.CreatedAtUnix),
        BOLT11 = entry.Request?.LightningInvoice?.Invoice,
        // Populated only once the swap durably reveals it (see WalletEntryProgress.preimage's own
        // doc comment) - empty/absent is the normal, expected state for anything not yet Complete.
        Preimage = string.IsNullOrEmpty(entry.Progress?.Preimage) ? null : entry.Progress.Preimage
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
