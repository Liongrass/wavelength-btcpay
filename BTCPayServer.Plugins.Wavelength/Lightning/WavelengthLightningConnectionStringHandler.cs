using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Wavelength.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Wavelength.Lightning;

/// <summary>
/// Resolves "type=wavelength;token=...;(extra waved flags)" connection strings to a per-store
/// WavelengthLightningClient. Unlike LND/CLN-style handlers, the connection string carries no
/// externally-meaningful address - waved is plugin-managed per store, so the token is an opaque
/// lookup key into WavedProcessManager, resolved back to the real storeId via
/// WavedStoreTokenProtector rather than being the storeId itself (see that class's doc comment for
/// why). Every other key (besides "type"/"token") is passed through as a "--key=value" waved flag -
/// e.g. "network=regtest" becomes "--network=regtest" - but only if WavedAllowedFlags.IsAllowed
/// accepts the key, WavedFlagValues accepts the value, and the store is permitted to use Wavelength
/// at all. Everything else is rejected here rather than silently dropped or silently passed through
/// unreviewed.
///
/// This is where two of the three boundaries this plugin relies on are actually enforced, and both
/// matter because this method is reached by a connection string that a store owner can write:
///  - The token has to belong to the store it is being saved for. Without that check, possessing
///    store B's connection string would be enough to make store A's Lightning connection reach B's
///    wallet - reading and spending another store's funds, and overwriting its persisted waved
///    flags along the way. See the store-binding check in <see cref="Create"/>.
///  - The store has to have been adopted for Wavelength by someone entitled to make that decision
///    for this server, since the alternative is that any store owner can make the server run a
///    longer-lived process and hold a wallet seed on their behalf. See the approval check in
///    <see cref="Create"/> and WavelengthServerSettings.
/// </summary>
public sealed class WavelengthLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WavedStoreTokenProtector _tokenProtector;
    private readonly IWavedStoreApprovals _storeApprovals;
    private readonly IWavelengthServerSettingsSource _serverSettings;
    private readonly ILogger<WavelengthLightningConnectionStringHandler> _logger;

    /// <summary>
    /// The ambient-context accessor, resolved lazily and then kept. Resolved lazily because the
    /// handler is a singleton while IHttpContextAccessor is registered scoped, so asking for it
    /// during construction would either fail or capture whichever scope happened to be current at
    /// startup; resolved through a scope for the same reason, and because a host running with scope
    /// validation on (as BTCPay does in Development) refuses a scoped service requested straight
    /// from the root provider. Keeping the resolved instance is safe precisely because
    /// HttpContextAccessor holds no per-scope state - it reads the current request off a static
    /// AsyncLocal - so one instance answers correctly for every later request.
    /// </summary>
    private readonly Lazy<IHttpContextAccessor?> _httpContextAccessor;

    public WavelengthLightningConnectionStringHandler(
        IServiceProvider serviceProvider,
        WavedStoreTokenProtector tokenProtector,
        IWavedStoreApprovals storeApprovals,
        IWavelengthServerSettingsSource serverSettings,
        ILogger<WavelengthLightningConnectionStringHandler> logger)
    {
        _serviceProvider = serviceProvider;
        _tokenProtector = tokenProtector;
        _storeApprovals = storeApprovals;
        _serverSettings = serverSettings;
        _logger = logger;
        _httpContextAccessor = new Lazy<IHttpContextAccessor?>(ResolveHttpContextAccessor);
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "wavelength")
        {
            error = null;
            return null;
        }

        // What this call knows about who is asking. Null in a background context - the Lightning
        // listener, a payout processor, a cron-driven poll - where core builds a client from a
        // store's own saved connection string, and where the store is not a claim being made but a
        // fact about what is being read. Null there therefore means "no expectation to check
        // against", never "any store will do": the token still has to be a current one, which is
        // what keeps a revoked string dead regardless of who re-reads it.
        var context = _httpContextAccessor.Value?.HttpContext;
        // Two sources, deliberately: Core's authorization handler records the store it just
        // authorized (BTCPayServer.Security.BuiltInPermissionHandler, read back through
        // GetStoreDataOrNull) - which is the authoritative answer when it is there - and, as a
        // fallback, the {storeId} route value. The fallback covers a request that reached this
        // handler without having passed through that permission handler, such as a Greenfield API
        // save, where a store-scoped check would otherwise find no store to compare against and
        // quietly accept a token for a different store. Route values are not authorization - they
        // are what the caller asked for - which is exactly why the value is only ever compared
        // against the token and never trusted as identity.
        var actingStoreId = context?.GetStoreDataOrNull()?.Id;
        if (actingStoreId is null && context?.Request.RouteValues.TryGetValue("storeId", out var routeStoreId) == true)
            actingStoreId = routeStoreId as string;

        if (!kv.TryGetValue("token", out var token))
        {
            error = kv.ContainsKey("store-id")
                ? "The key 'store-id' is no longer used for wavelength connection strings - replace " +
                  "it with the token shown on this store's Lightning setup page."
                : "The key 'token' is required for wavelength connection strings - see this store's " +
                  "Lightning setup page for its own generated token.";
            return null;
        }

        // H-1's enforcement point: the token must be a current token for the store it is being used
        // for. A store pasting another store's string is refused here, before any client exists to
        // bind to the other store's waved instance - and refused at save time, since core validates
        // a connection string by parsing it through this same method (see
        // LightningLikePaymentHandler.ValidatePaymentMethodConfig, which turns the error below into
        // the model error shown next to the field).
        //
        // Checked first, before anything consults the store's settings: the token's store binding is
        // a pure cryptographic fact, so a token naming a store this instance never issued one for
        // should not cause a database read or an approval check on anyone's behalf.
        var isServerAdmin = context?.User.IsInRole(Roles.ServerAdmin) == true;
        if (!_tokenProtector.TryResolve(token, actingStoreId, out var storeId, out error))
            return null;

        // M-1's enforcement point, on the save side, and only asked when this store has not already
        // been approved - see CheckStoreIsAllowedToUseWavelength.
        var approvalError = CheckStoreIsAllowedToUseWavelength(actingStoreId, isServerAdmin);
        if (approvalError is not null)
        {
            error = approvalError;
            return null;
        }

        // The value check rides along inside TryParseExtraFlags (see WavedFlagValues). The escape
        // hatch is isServerAdmin OR'd with this store's own persisted AllowLocalEndpoints marker -
        // not isServerAdmin alone - because Create is re-run on every single later use of an already
        // saved connection string, not just at the moment someone saves it: an invoice, a payout, a
        // background poll, none of which carry an administrator's own request context (isServerAdmin
        // is false for all of them). An admin who was once allowed to save a local value - a LAN
        // esplora, a local Ark operator - approved it exactly once; a store whose saved string still
        // carries that value must keep working on every later call, not just the admin's own. Without
        // the marker here, Create would re-reject the very value it just accepted the moment anyone
        // other than that admin (which in practice means almost every call) asked for a client again -
        // which is exactly the bug this fixes, found by reproducing it with a valid token against an
        // approved store carrying a local server= value. isServerAdmin still has to stay in the OR: a
        // brand new admin save introducing a local value for the first time has nothing in the marker
        // yet (RecordApproval below is what writes it, and only after this call succeeds), so without
        // isServerAdmin that very save would reject itself.
        //
        // AllowsLocalEndpoints is a blanket "this store may carry local values at all", though, not
        // "this store may carry THESE SPECIFIC local values" - so for an actual non-admin SAVE (a
        // request context naming this store, as opposed to a background re-read of what is already
        // persisted), the marker alone is not enough: it must also be true that this save is not
        // introducing or changing which local value is used. Otherwise an administrator approving one
        // private endpoint (their own LAN esplora, say) would silently let the store owner replace it
        // with any other local address afterward, including the highest-value SSRF target
        // (169.254.169.254) - reproduced directly: after an admin approves
        // wallet.esploraurl=http://127.0.0.1:5000/api, a non-admin save of
        // wallet.esploraurl=http://169.254.169.254/latest/meta-data/ was accepted purely because the
        // marker was already true, with BTCPay core's own IsSafe check providing no backstop for this
        // particular key at all. A background re-read is unaffected - see
        // LocalValuesMatchWhatIsAlreadyPersisted's doc comment for why it is deliberately not asked
        // there.
        var allowLocal = isServerAdmin || _storeApprovals.AllowsLocalEndpoints(storeId);
        if (!isServerAdmin && allowLocal && context is not null && actingStoreId is not null)
        {
            // allowLocal=true here is only to extract the flags for comparison, not the final
            // verdict - a disallowed key or malformed value fails identically regardless of
            // allowLocal (see WavedFlagValues: the escape hatch gates locality, not parsing), so a
            // failure here is never masked by this being more lenient than the real check below.
            if (!TryParseExtraFlags(connectionString, out var candidateFlags, out error, allowLocal: true))
                return null;

            if (!LocalValuesMatchWhatIsAlreadyPersisted(candidateFlags, _storeApprovals.GetPersistedFlags(storeId)))
                allowLocal = false;
        }

        if (!TryParseExtraFlags(connectionString, out var extraFlags, out error, allowLocal))
            return null;

        if (context is not null && actingStoreId is not null)
            RecordApproval(actingStoreId, isServerAdmin, extraFlags);

        // Not ActivatorUtilities.CreateInstance here on purpose: WavelengthLightningClient's
        // constructor has two string parameters (storeId, token, in that order) - relying on
        // ActivatorUtilities' type-based positional matching to keep them in the right order is
        // exactly the kind of thing that fails silently (swapped values, not a compile error or
        // even necessarily a visible runtime error) rather than loudly. WavedProcessManager is
        // the only actual DI dependency here, so resolving it directly removes the ambiguity
        // entirely instead of just trusting it works out.
        var processManager = _serviceProvider.GetRequiredService<WavedProcessManager>();
        return new WavelengthLightningClient(
            processManager, network, storeId, token, (IReadOnlyDictionary<string, string>)extraFlags);
    }

    /// <summary>
    /// Extracts the extra waved flags from a wavelength connection string ("type" and "token"
    /// stripped, everything WavedAllowedFlags.IsAllowed doesn't accept rejected) - the same parsing
    /// <see cref="Create"/> uses, exposed so other call sites (e.g. the Advanced page's "Restart
    /// waved" action, which needs to re-derive flags from the store's current live connection
    /// string rather than whatever was last persisted) don't have to duplicate it.
    ///
    /// Those call sites pass <paramref name="allowLocal"/> from the store's persisted
    /// AllowLocalEndpoints marker: the values were already accepted once, by an administrator, and
    /// a store whose flags the plugin is merely re-reading should not have them re-judged by
    /// whoever happens to be looking at the page.
    /// </summary>
    public static bool TryParseExtraFlags(
        string connectionString, out Dictionary<string, string> extraFlags, out string? error, bool allowLocal = false)
    {
        extraFlags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out _);
        foreach (var (key, value) in kv)
        {
            if (key is "type" or "token")
                continue;

            if (!WavedAllowedFlags.IsAllowed(key))
            {
                error = $"The key '{key}' isn't one this plugin allows in a wavelength connection " +
                        "string. Only a reviewed set of waved flags can be set this way - see " +
                        "WavedAllowedFlags for the full list.";
                return false;
            }

            extraFlags[key] = value;
        }

        return WavedFlagValues.Validate(extraFlags, out error, allowLocal);

    }
    /// <summary>
    /// M-1's enforcement point, on the save side: a store may only be adopted for Wavelength by
    /// someone entitled to make that decision for this server, unless the server has said every
    /// store may have it.
    ///
    /// Only asked when the store has not been approved yet, which is what keeps ordinary use of an
    /// already-approved store working for the people and API keys that operate it. A store gets its
    /// approval recorded below, at the moment its connection string is first accepted; from then on
    /// this returns null immediately and an API key holding nothing but CanCreateInvoice can create
    /// invoices on that store exactly as before. Asking every time would instead break every such
    /// call - and, worse, would reject a customer at checkout, since an invoice creation is also an
    /// HTTP request.
    /// </summary>
    private string? CheckStoreIsAllowedToUseWavelength(string? actingStoreId, bool isServerAdmin)
    {
        // A request that names no store is not a save: there is nothing to adopt, and the token has
        // already been checked. Read-only consumers of a store's own string land here.
        if (actingStoreId is null)
            return null;

        if (_storeApprovals.IsApproved(actingStoreId))
            return null;

        if (isServerAdmin || _serverSettings.GetAsync().GetAwaiter().GetResult().AllowForAllStores)
            return null;

        _logger.LogWarning(
            "Refused a wavelength connection string for store {StoreId}: the store has not been " +
            "approved for Wavelength by a server administrator",
            actingStoreId);
        return "Using Wavelength requires server admin approval - ask the server administrator to " +
               "enable it in the server settings.";
    }

    /// <summary>
    /// Records, on the store, the two decisions that only a request context can witness: that the
    /// store has been adopted, and - when an administrator deliberately pointed it at a local
    /// esplora, fee service, or peer - that this store's flags may legitimately contain local
    /// addresses.
    ///
    /// Only a server administrator's own save records approval. A save that was allowed through
    /// by the AllowForAllStores setting is deliberately NOT recorded: that setting is a server-wide
    /// stance an administrator can flip on for a day and off again, and a store that happened to
    /// save while it was on must not keep a private approval after it goes back off - turning the
    /// setting off is the revoke, and nothing should survive it. A later save by an actual
    /// administrator is what records a durable, store-specific approval.
    ///
    /// Written here rather than at the process start that follows, because the start happens later,
    /// possibly in a background loop with no user attached, and possibly not at all until the store
    /// is visited. Only written when the value actually changes, so an ordinary Lightning operation
    /// on an approved store (a call that reaches this same handler with a context attached) does no
    /// work and no write.
    /// </summary>
    private void RecordApproval(string storeId, bool isServerAdmin, IReadOnlyDictionary<string, string> extraFlags)
    {
        if (isServerAdmin)
            _storeApprovals.ApproveAsync(storeId, allowLocalValues: HasLocalFlagValues(extraFlags))
                .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Whether any flag value in this set is one a non-admin save would have rejected. Used to
    /// decide whether the store needs the persisted AllowLocalEndpoints marker at all, so a store
    /// that never uses local endpoints does not carry a permission it does not need.
    /// </summary>
    private static bool HasLocalFlagValues(IReadOnlyDictionary<string, string> extraFlags)
        => !WavedFlagValues.Validate(extraFlags, out _, allowLocal: false);

    /// <summary>
    /// Whether every address-valued flag in candidateFlags that needs the local-endpoint escape
    /// hatch to pass strict validation already has that EXACT value in persistedFlags - i.e. whether
    /// this non-admin save only re-saves local values an administrator already reviewed, rather than
    /// introducing or changing one. See Create's call site for why this distinction matters: a
    /// single persisted marker means "may use local values at all", not "may use THESE local
    /// values", and conflating the two lets a store owner redirect an admin-approved local endpoint
    /// to any other local address with no further admin involvement.
    ///
    /// Only asked for an actual save (a request context naming this store) - deliberately never for
    /// a background re-read of a string that is, by definition, already exactly what was last
    /// persisted, so there is nothing for it to have changed relative to. Asking it there would be
    /// redundant at best, and would break every Lightning operation on a store whose saved string
    /// still carries an approved local value the instant GetPersistedFlags raced a concurrent write
    /// (a real possibility, since that read and the ExtraWavedFlags row it compares against are not
    /// part of one transaction) - a background consumer has no admin to blame that on and no save to
    /// refuse, only a payment that must not spuriously fail.
    ///
    /// Checked per flag, not as a whole-string comparison: changing or adding an unrelated,
    /// non-local flag (network=signet, say) alongside an untouched local one must not itself demand
    /// re-approval, and a flag whose new value validates fine strictly (a public URL, a non-address
    /// flag entirely) was never gated by the escape hatch in the first place, so whether it changed
    /// is irrelevant here.
    /// </summary>
    internal static bool LocalValuesMatchWhatIsAlreadyPersisted(
        IReadOnlyDictionary<string, string> candidateFlags, IReadOnlyDictionary<string, string>? persistedFlags)
    {
        foreach (var (key, value) in candidateFlags)
        {
            if (WavedFlagValues.Validate(new Dictionary<string, string> { [key] = value }, out _, allowLocal: false))
                continue;

            if (persistedFlags is null ||
                !persistedFlags.TryGetValue(key, out var persistedValue) ||
                !string.Equals(persistedValue, value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private IHttpContextAccessor? ResolveHttpContextAccessor()
    {
        try
        {
            // A scope of its own, disposed immediately: IHttpContextAccessor is not disposable and
            // carries no per-scope state, so the instance outliving the scope that produced it is
            // exactly the point.
            using var scope = _serviceProvider.GetRequiredService<IServiceScopeFactory>().CreateScope();
            return scope.ServiceProvider.GetService<IHttpContextAccessor>();
        }
        catch (Exception ex)
        {
            // Not fatal: without an accessor there is simply no way to tell a user-initiated save
            // from a background read, and the token's own store binding and freshness are still
            // enforced. Logged rather than swallowed, because in that state the approval boundary
            // is not being applied.
            _logger.LogError(ex,
                "Could not resolve IHttpContextAccessor - wavelength connection strings will be " +
                "accepted without checking who is saving them");
            return null;
        }
    }
}
