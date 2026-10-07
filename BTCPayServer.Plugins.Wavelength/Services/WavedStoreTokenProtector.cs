using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Mints and verifies the opaque token a store's wavelength connection string carries, in place of
/// the store's own BTCPay ID.
///
/// Why a token at all: a store's real ID is often visible to anyone with even minor access to that
/// store (it is in the URL), so using it as the sole "authorization" for which waved instance a
/// connection string reaches means knowing a low-sensitivity value is enough to point a store at
/// someone else's wallet. The token requires actually possessing that store's generated connection
/// string instead. This complements (does not replace) the per-store TLS+macaroon boundary waved
/// itself enforces (see WavedProcessManager.BuildSecureChannel): that one stops a request from
/// reaching the wrong waved process; this one stops a connection string from being told to reach
/// the wrong one on purpose in the first place.
///
/// What a token is: the store's own ID, a period, and an HMAC-SHA256 over that ID keyed by the
/// store's secret seed - "storeId.mac", where the seed is the random per-store value in
/// <see cref="WavedStoreSettings.TokenSeed"/>. Both halves matter, and neither alone is enough:
///  - The store ID is what binds the token to one store. Without checking it, possessing store B's
///    string would be enough to make store A's Lightning connection reach B's wallet - which is
///    precisely the substitution this class exists to stop, since it means reading and spending
///    another store's funds and overwriting its persisted waved flags.
///  - The seed is what makes a token *revocable*, and what makes it unforgeable. A token only
///    verifies while its MAC matches the store's current seed, so
///    <see cref="RegenerateTokenAsync"/> kills exactly one store's string, immediately, with no
///    effect on anyone else; and since the seed never appears in a token, someone holding a token
///    cannot mint another one from it.
///
/// Why an HMAC rather than encrypting "storeId|seed" with Data Protection, which the rest of this
/// plugin uses for secrets: Data Protection's Protect is deliberately non-deterministic - it mixes
/// in a fresh random nonce on every call - so a token minted from a seed on each page render is a
/// *different string every time*. That defeats the point of having the setup page show a token at
/// all: the operator cannot tell the string they saved from one that has since been superseded, and
/// every render mints another equally valid string. An HMAC over the same inputs is the same bytes
/// every time, which is what makes the displayed token the one that actually works. Its key material
/// is per-install and never leaves the server; a token from a different instance simply fails to
/// verify.
///
/// A consequence worth stating plainly, because it is deliberate: the store ID is *visible* in the
/// token, and knowing it is still not enough to use one. A store's real ID is already easy to come
/// by (it is in the store's own URL), which is why it was never the secret here - the seed keying
/// the MAC is, and possession of a store's token is what the MAC effectively proves. That also means
/// a leaked token grants nothing beyond using the store it already named, and dies the moment that
/// store's token is regenerated.
///
/// Tokens issued before this change - Data Protection ciphertext over a bare store ID - resolve as
/// invalid, deliberately rather than leniently: accepting them would mean accepting a token that
/// carries no store binding and cannot be revoked, which is the whole finding. They are refused with
/// an error naming the cause, and the store's Lightning setup page hands out a fresh one. The plugin
/// is pre-1.0 and its README says so.
/// </summary>
public sealed class WavedStoreTokenProtector(
    IWavedTokenSeeds tokenSeeds,
    ILogger<WavedStoreTokenProtector> logger)
{
    /// <summary>
    /// Separates the store ID from its MAC. Store IDs are opaque BTCPay-issued strings and this
    /// plugin never assumes anything about their contents beyond "no separator", which the
    /// first-separator split below enforces defensively.
    /// </summary>
    private const char PayloadSeparator = '.';

    /// <summary>
    /// This store's current token, minting a seed for it on first use. What the Lightning setup
    /// page renders and what an operator copies into a Lightning connection string.
    ///
    /// Stable across renders, which is the point: it is derived from the persisted seed rather than
    /// freshly encrypted each time. Data Protection's Protect is not deterministic (it uses a
    /// random nonce), so encrypting the store ID again on every page render - as this plugin used
    /// to - showed a different, equally valid string each time, and left no way to tell a current
    /// string from one that had since been replaced.
    /// </summary>
    public async Task<string> GetOrCreateTokenAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var seed = await tokenSeeds.GetOrCreateSeedAsync(storeId, cancellationToken);
        return Encode(storeId, seed);
    }

    /// <summary>
    /// Replaces this store's seed, which revokes every token previously issued for it - including
    /// the one in the store's own saved connection string, so Lightning for that store stops
    /// working until the new string is saved. That is the intended behaviour of a revoke, and the
    /// caller is responsible for saying so; see UIWavelengthController.Advanced's RegenerateToken
    /// action for the message shown.
    /// </summary>
    public async Task<string> RegenerateTokenAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var seed = await tokenSeeds.RegenerateSeedAsync(storeId, cancellationToken);
        logger.LogInformation(
            "Regenerated the wavelength connection-string token for store {StoreId} - every " +
            "previously issued token for this store is now invalid",
            storeId);
        return Encode(storeId, seed);
    }

    /// <summary>
    /// Resolves <paramref name="token"/> to the store it was issued for, or fails.
    ///
    /// <paramref name="expectedStoreId"/> is the store the caller is acting for, or null when the
    /// caller has no store to check against - a background consumer (the Lightning listener, a
    /// payout processor) constructing a client from a store's own saved connection string, where
    /// the store is not a claim being made but a fact about what is being read. Null therefore
    /// means "no expectation", not "any store is fine": the token still has to be a current token
    /// for whatever store it names, so a revoked string fails here no matter who is asking.
    ///
    /// Every failure is reported as one of a small number of messages that deliberately do not
    /// identify which store a rejected token belonged to. The caller already holds the token, so
    /// nothing is gained by confirming whose it is, and a store owner who pastes another store's
    /// token should learn that it was refused rather than which store it came from. The store is
    /// named in the log line instead, where it is useful to an administrator and not to the caller.
    /// </summary>
    public bool TryResolve(string token, string? expectedStoreId, out string storeId, out string? error)
    {
        storeId = "";
        error = null;

        if (string.IsNullOrEmpty(token))
        {
            error = "This wavelength connection string has no token. Copy one from this store's " +
                    "Lightning setup page.";
            return false;
        }

        if (!TrySplitPayload(token, out var tokenStoreId, out var tokenMac))
        {
            // Either a hand-edited string or a token in a shape this plugin no longer issues -
            // including the pre-change format, which was Data Protection ciphertext over a bare
            // store ID and therefore contains no separator at all. Both are reported the same way,
            // since the caller's next step is identical: get a fresh token from the setup page.
            error = "This wavelength connection string's token is invalid, or was generated by an " +
                    "older version of this plugin. Generate a fresh connection string on this " +
                    "store's Lightning setup page.";
            return false;
        }

        // Checked before the MAC so a token naming a different store is rejected for the reason
        // that is actually true, rather than reported as a forgery.
        if (expectedStoreId is not null &&
            !string.Equals(tokenStoreId, expectedStoreId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Rejected a wavelength connection-string token issued for a different store than the " +
                "one it was offered to (offered to {Expected}, issued for {TokenStoreId})",
                expectedStoreId, tokenStoreId);
            error = "This wavelength connection string's token belongs to a different store, or was " +
                    "revoked. Each store has its own token - copy the one shown on this store's " +
                    "Lightning setup page.";
            return false;
        }

        // The revocation check and the forgery check are the same check: the MAC only matches while
        // the store's current seed is the one that produced it. A regenerated token, a token for a
        // store that never had one, and a token someone assembled by hand all fail here.
        var currentSeed = tokenSeeds.GetSeed(tokenStoreId);
        if (string.IsNullOrEmpty(currentSeed) || !MacMatches(currentSeed, tokenStoreId, tokenMac))
        {
            logger.LogInformation(
                "Rejected a revoked, forged, or unknown wavelength connection-string token for store " +
                "{StoreId}",
                tokenStoreId);
            error = "This wavelength connection string's token is no longer valid - it was revoked. " +
                    "Generate a fresh connection string on this store's Lightning setup page.";
            return false;
        }

        storeId = tokenStoreId;
        return true;
    }

    private string Encode(string storeId, string seed)
        => $"{storeId}{PayloadSeparator}{MacFor(seed, storeId)}";

    /// <summary>
    /// The MAC over a store ID under that store's seed. The store ID is inside the signed message,
    /// not merely carried beside it, so a token's two halves cannot be mixed and matched: the MAC
    /// issued for one store does not verify against another's, whatever the token says.
    /// </summary>
    private static string MacFor(string seed, string storeId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(seed));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(storeId)));
    }

    /// <summary>
    /// Compares a token's MAC against the one this store's current seed produces, in constant time.
    /// A length mismatch is answered false rather than passed to
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>, which throws on unequal spans.
    /// </summary>
    private static bool MacMatches(string seed, string storeId, string tokenMac)
    {
        // The comparison is against base64 text rather than raw bytes, so a token whose MAC is not
        // even valid base64 fails here without a decode step that could throw.
        var expected = MacFor(seed, storeId);
        return tokenMac.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(
                   Encoding.UTF8.GetBytes(tokenMac), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// Splits a token into its store ID and MAC, rejecting anything that is not exactly those two
    /// non-empty halves. The first separator separates them and nothing else in the token is
    /// interpreted, so a period inside a store ID cannot be used to shift the boundary. Internal
    /// rather than private so the format itself is directly testable.
    /// </summary>
    internal static bool TrySplitPayload(string token, out string storeId, out string mac)
    {
        storeId = "";
        mac = "";

        var separator = token.IndexOf(PayloadSeparator);
        if (separator <= 0 || separator == token.Length - 1)
            return false;

        storeId = token[..separator];
        mac = token[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// Base64url without padding, so a token survives being pasted into a connection string: the
    /// standard alphabet's '+' and '/' would need escaping, and '=' padding is meaningless in a
    /// semicolon-separated key=value list.
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
