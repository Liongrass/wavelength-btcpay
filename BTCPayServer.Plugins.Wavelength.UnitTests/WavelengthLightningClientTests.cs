using BTCPayServer.Plugins.Wavelength.Lightning;
using Wavewalletrpc;
using Xunit;

namespace BTCPayServer.Plugins.Wavelength.UnitTests;

/// <summary>
/// Regression coverage for the credit-backed-receive payment-hash bug: WalletEntry.id is the
/// Lightning payment hash for a normal receive, but the credit operation id for a sub-VTXO-floor
/// (credit-backed) one - see wallet.proto's own doc comment on WalletEntry.id, and
/// WavelengthLightningClient.ResolvePaymentHash's. Using entry.Id unconditionally as PaymentHash
/// made BTCPay core's own LightningExtensions.GetPaymentHash call uint256.Parse on a non-hash
/// string and throw "A uint256 must be 64 characters", silently failing to activate Lightning as a
/// payment method for every invoice under the operator's VTXO floor.
/// </summary>
public class WavelengthLightningClientTests
{
    [Fact]
    public void ResolvePaymentHashPrefersTheNestedHashOverEntryIdForACreditBackedReceive()
    {
        const string opId = "credit-op-7f3a9c21";
        const string realPaymentHash = "7f1d9e8c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b1a0f9e";

        var entry = new WalletEntry
        {
            Id = opId,
            Request = new WalletEntryRequest
            {
                LightningInvoice = new LightningInvoiceRequest
                {
                    Invoice = "lnbc1...",
                    PaymentHash = realPaymentHash
                }
            }
        };

        Assert.Equal(realPaymentHash, WavelengthLightningClient.ResolvePaymentHash(entry));
        // Not the opID - that is exactly the value that made uint256.Parse throw.
        Assert.NotEqual(opId, WavelengthLightningClient.ResolvePaymentHash(entry));
    }

    [Fact]
    public void ResolvePaymentHashFallsBackToEntryIdForAnOrdinaryReceive()
    {
        const string paymentHash = "7f1d9e8c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c8b7a6f5e4d3c2b1a0f9e";

        // A normal (non-credit) receive's Id IS the payment hash, and its nested
        // Request.LightningInvoice.PaymentHash carries the same value (see
        // requestFromSwapSummary in the wavelength repo) - not an empty/absent one.
        var entry = new WalletEntry
        {
            Id = paymentHash,
            Request = new WalletEntryRequest
            {
                LightningInvoice = new LightningInvoiceRequest
                {
                    Invoice = "lnbc1...",
                    PaymentHash = paymentHash
                }
            }
        };

        Assert.Equal(paymentHash, WavelengthLightningClient.ResolvePaymentHash(entry));
    }

    [Fact]
    public void ResolvePaymentHashFallsBackToEntryIdWhenNoRequestIsAttached()
    {
        var entry = new WalletEntry { Id = "some-id-with-no-request" };

        Assert.Equal("some-id-with-no-request", WavelengthLightningClient.ResolvePaymentHash(entry));
    }
}
