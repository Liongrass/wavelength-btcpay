using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Holds a just-created wallet's mnemonic in memory only, until the store owner explicitly
/// acknowledges having written it down (see Acknowledge). Never persisted - if the store owner
/// never acknowledges it and the BTCPay Server process restarts, it's gone, same as if they'd
/// never been shown it. Re-visiting the dashboard or the mnemonic page in the meantime re-shows
/// the same phrase rather than losing it - see WavedProcessManager.CreateWalletAsync and
/// UIWavelengthController's CreateWallet/Mnemonic/AcknowledgeMnemonic actions.
/// </summary>
public sealed class WavedMnemonicPendingCache
{
    private readonly ConcurrentDictionary<string, string> _mnemonics = new();

    public void Store(string storeId, string mnemonic) => _mnemonics[storeId] = mnemonic;

    /// <summary>
    /// True if this store has a mnemonic waiting to be acknowledged - used to redirect a
    /// dashboard visit straight to it (see UIWavelengthController.Index) instead of rendering the
    /// normal wallet view and letting it go unseen, which is exactly what happens if creation
    /// finishes while nobody's waiting on the request that started it.
    /// </summary>
    public bool HasPending(string storeId) => _mnemonics.ContainsKey(storeId);

    /// <summary>Returns the store's mnemonic without clearing it, or null if none is pending.</summary>
    public string? Peek(string storeId)
        => _mnemonics.TryGetValue(storeId, out var mnemonic) ? mnemonic : null;

    /// <summary>The store owner has confirmed they wrote it down - clears it for good.</summary>
    public void Acknowledge(string storeId) => _mnemonics.TryRemove(storeId, out _);
}
