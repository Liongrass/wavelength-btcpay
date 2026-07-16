using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Holds a just-created wallet's mnemonic in memory only, for exactly one read. Never persisted
/// - if the store owner doesn't view it before the BTCPay Server process restarts (or before
/// they read it once), it's gone, same as if they'd never been shown it. This is the intentional
/// cost of not persisting the mnemonic anywhere durable; see WavedProcessManager.CreateWalletAsync
/// and UIWavelengthController's CreateWallet/Mnemonic actions.
/// </summary>
public sealed class WavedMnemonicOnceCache
{
    private readonly ConcurrentDictionary<string, string> _mnemonics = new();

    public void Store(string storeId, string mnemonic) => _mnemonics[storeId] = mnemonic;

    /// <summary>
    /// True if this store has a mnemonic waiting to be shown - used to redirect a dashboard visit
    /// straight to it (see UIWavelengthController.Index) instead of rendering the normal wallet
    /// view and losing it, which is exactly what happens if creation finishes while nobody's
    /// waiting on the request that started it.
    /// </summary>
    public bool HasPending(string storeId) => _mnemonics.ContainsKey(storeId);

    /// <summary>Returns and removes the store's mnemonic, or null if it was already taken (or never set).</summary>
    public string? TakeOnce(string storeId)
        => _mnemonics.TryRemove(storeId, out var mnemonic) ? mnemonic : null;
}
