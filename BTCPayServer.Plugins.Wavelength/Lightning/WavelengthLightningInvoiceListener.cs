using System.Threading.Channels;
using BTCPayServer.Lightning;
using Grpc.Core;
using Wavewalletrpc;
using WalletServiceClient = Wavewalletrpc.WalletService.WalletServiceClient;

namespace BTCPayServer.Plugins.Wavelength.Lightning;

/// <summary>
/// Surfaces completed receives to BTCPay's checkout polling loop by reading waved's
/// WalletService.SubscribeWallet stream, rather than polling List repeatedly.
/// </summary>
public sealed class WavelengthLightningInvoiceListener : ILightningInvoiceListener
{
    private readonly Channel<LightningInvoice> _channel = Channel.CreateUnbounded<LightningInvoice>();
    private readonly CancellationTokenSource _cts;
    private readonly AsyncServerStreamingCall<SubscribeWalletResponse> _call;
    private readonly Task _readTask;

    public WavelengthLightningInvoiceListener(WalletServiceClient client, CancellationToken externalToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var request = new SubscribeWalletRequest();
        request.Kinds.Add(EntryKind.Recv);
        _call = client.SubscribeWallet(request, cancellationToken: _cts.Token);
        _readTask = ReadAsync(_cts.Token);
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var response in _call.ResponseStream.ReadAllAsync(ct))
            {
                // TODO: a Gap update means the daemon dropped us from the live buffer; a
                // correct client reconciles via List(view=ACTIVITY) from here. Ignored for now -
                // worst case is a missed real-time notification, not an incorrect one, since
                // BTCPay's checkout page also polls GetInvoice as a fallback.
                if (response.UpdateCase != SubscribeWalletResponse.UpdateOneofCase.Entry)
                    continue;

                var entry = response.Entry;
                if (entry.Status != EntryStatus.Complete)
                    continue;

                await _channel.Writer.WriteAsync(new LightningInvoice
                {
                    Id = entry.Id,
                    Amount = LightMoney.Satoshis(entry.AmountSat),
                    PaymentHash = entry.Id,
                    Status = LightningInvoiceStatus.Paid,
                    PaidAt = DateTimeOffset.FromUnixTimeSeconds(entry.UpdatedAtUnix)
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException)
        {
            // The store's waved instance may have been stopped/restarted; the listener just
            // stops emitting rather than surfacing a transport error to BTCPay's checkout page.
        }
    }

    public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
    {
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellation);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(combined.Token))
            {
                if (_channel.Reader.TryRead(out var invoice))
                    return invoice;
            }
        }
        catch (OperationCanceledException) { }

        return new LightningInvoice();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        _call.Dispose();
        _cts.Dispose();
    }
}
