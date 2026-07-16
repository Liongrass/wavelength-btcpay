using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using BTCPayServer.Events;
using BTCPayServer.Services.Stores;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wavewalletrpc;
using WalletInspectionServiceClient = Wavewalletrpc.WalletInspectionService.WalletInspectionServiceClient;
using WalletServiceClient = Wavewalletrpc.WalletService.WalletServiceClient;

namespace BTCPayServer.Plugins.Wavelength.Services;

/// <summary>
/// Manages per-store waved processes. Unlike a shared daemon, every store gets its own waved
/// instance (own --datadir, own port) so balances/invoices never cross stores - waved is
/// single-wallet-per-process and has no multi-tenant mode.
///
/// Stores are started lazily: on startup, only stores that already have wallet data on disk
/// are restarted; a brand-new store's waved instance starts on first use via
/// <see cref="EnsureStartedAsync"/> (called from the Lightning connection handler / setup UI).
/// Starting the *process* is automatic and side-effect-free; creating the *wallet* (a seed) is
/// deliberately not - see <see cref="CreateWalletAsync"/>. A store whose wallet was never
/// explicitly created just has a running, walletless waved instance; every wallet RPC against it
/// fails until a human clicks "Create wallet" in the dashboard.
///
/// On StoreEvent.Removed the process is stopped but its --datadir is intentionally NEVER
/// deleted automatically - that directory holds the wallet seed and DB. Cleanup of orphaned
/// store data after a real store deletion is a manual operator action.
/// </summary>
public sealed class WavedProcessManager : BackgroundService, IDisposable
{
    private readonly WavedConfiguration _config;
    private readonly StoreRepository _storeRepository;
    private readonly EventAggregator _eventAggregator;
    private readonly WavedWalletCredentialStore _credentialStore;
    private readonly WavedMnemonicPendingCache _mnemonicCache;
    private readonly ILogger<WavedProcessManager> _logger;
    private readonly string _nativeDir;

    private readonly ConcurrentDictionary<string, StoreProcess> _stores = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new();
    private readonly ConcurrentDictionary<string, Task<string[]?>> _creationTasks = new();
    private readonly object _creationLock = new();
    private readonly ConcurrentQueue<string> _removedStoreIds = new();
    private IEventAggregatorSubscription? _storeRemovedSub;
    private readonly object _portLock = new();
    private readonly HashSet<int> _reservedPorts = new();
    private bool _disposed;

    public WavedProcessManager(
        WavedConfiguration config,
        StoreRepository storeRepository,
        EventAggregator eventAggregator,
        WavedWalletCredentialStore credentialStore,
        WavedMnemonicPendingCache mnemonicCache,
        ILogger<WavedProcessManager> logger)
    {
        _config = config;
        _storeRepository = storeRepository;
        _eventAggregator = eventAggregator;
        _credentialStore = credentialStore;
        _mnemonicCache = mnemonicCache;
        _logger = logger;

        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Cannot determine assembly location");
        _nativeDir = Path.Combine(assemblyDir, "Native");
    }

    public bool IsRunning(string storeId)
        => _stores.TryGetValue(storeId, out var sp) && sp.Process is { HasExited: false };

    public Uri? GetStoreUri(string storeId)
        => _stores.TryGetValue(storeId, out var sp) ? sp.Uri : null;

    /// <summary>
    /// Returns a WalletService client bound to this store's waved instance, or null if it isn't
    /// running. The channel is plaintext gRPC (h2c) - waved is started with --rpc.notls
    /// --rpc.no-macaroons and only ever binds to loopback, so this is only safe because the
    /// connection never leaves the host. See WavelengthPlugin.Execute for the AppContext switch
    /// that enables unencrypted HTTP/2 on the client side.
    /// </summary>
    public WalletServiceClient? GetWalletClient(string storeId)
        => _stores.TryGetValue(storeId, out var sp) ? new WalletServiceClient(sp.Channel) : null;

    public WalletInspectionServiceClient? GetWalletInspectionClient(string storeId)
        => _stores.TryGetValue(storeId, out var sp) ? new WalletInspectionServiceClient(sp.Channel) : null;

    /// <summary>Daemon-level info (version, network, block height, wallet state) - the "wavecli getinfo" equivalent.</summary>
    public Waverpc.DaemonService.DaemonServiceClient? GetDaemonClient(string storeId)
        => _stores.TryGetValue(storeId, out var sp) ? new Waverpc.DaemonService.DaemonServiceClient(sp.Channel) : null;

    /// <summary>The extra waved flags this store's currently-running instance was actually started with, or null if it isn't running.</summary>
    public IReadOnlyDictionary<string, string>? GetRunningFlags(string storeId)
        => _stores.TryGetValue(storeId, out var sp) ? sp.Flags : null;

    public IReadOnlyList<string> GetRunningStoreIds()
        => _stores.Where(kv => kv.Value.Process is { HasExited: false }).Select(kv => kv.Key).ToList();

    /// <summary>
    /// Starts this store's waved instance if it isn't already running. Safe to call repeatedly.
    /// <paramref name="extraFlags"/> are extra waved CLI flags parsed from the store's
    /// connection string (see WavelengthLightningConnectionStringHandler) - pass null from
    /// internal callers (crash recovery, startup restart) that don't have a live connection
    /// string to hand; the last-known flags persisted via a real connection string are reused.
    /// If the store is already running, a fresh non-null extraFlags is persisted for the *next*
    /// restart but does not affect the already-running process - waved has no config-reload RPC.
    /// </summary>
    public async Task EnsureStartedAsync(
        string storeId, IReadOnlyDictionary<string, string>? extraFlags = null, CancellationToken cancellationToken = default)
    {
        var startLock = _startLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));
        await startLock.WaitAsync(cancellationToken);
        try
        {
            if (extraFlags is not null)
                await PersistFlagsAsync(storeId, extraFlags, cancellationToken);

            if (IsRunning(storeId))
            {
                if (extraFlags is not null)
                    WarnIfFlagsDiffer(storeId, extraFlags);
                return;
            }

            if (await _storeRepository.FindStore(storeId) is null)
            {
                _logger.LogWarning("Ignoring EnsureStartedAsync for non-existent store {StoreId}", storeId);
                return;
            }

            var resolvedFlags = extraFlags ?? await LoadPersistedFlagsAsync(storeId, cancellationToken);
            await StartStoreAsync(storeId, resolvedFlags, cancellationToken);
        }
        finally
        {
            startLock.Release();
        }
    }

    private async Task PersistFlagsAsync(
        string storeId, IReadOnlyDictionary<string, string> extraFlags, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var settings = await _storeRepository.GetSettingAsync<WavedStoreSettings>(storeId, WavedStoreSettings.SettingsKey)
            ?? new WavedStoreSettings();
        await _storeRepository.UpdateSetting(storeId, WavedStoreSettings.SettingsKey, settings with
        {
            ExtraWavedFlags = new Dictionary<string, string>(extraFlags, StringComparer.OrdinalIgnoreCase)
        });
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadPersistedFlagsAsync(string storeId, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var settings = await _storeRepository.GetSettingAsync<WavedStoreSettings>(storeId, WavedStoreSettings.SettingsKey);
        return settings?.ExtraWavedFlags ?? new Dictionary<string, string>();
    }

    private void WarnIfFlagsDiffer(string storeId, IReadOnlyDictionary<string, string> extraFlags)
    {
        if (!_stores.TryGetValue(storeId, out var sp))
            return;

        if (sp.Flags.Count == extraFlags.Count &&
            sp.Flags.All(kv => extraFlags.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return;

        _logger.LogWarning(
            "Store {StoreId}'s connection string flags changed, but its waved instance is already " +
            "running with the previous ones - stop it (or restart BTCPay Server) to apply the change.",
            storeId);
    }

    public async Task StopStoreAsync(string storeId)
    {
        if (!_stores.TryRemove(storeId, out var sp))
            return;

        await TerminateProcessAsync(sp.Process);
        sp.Channel.Dispose();
        sp.Process.Dispose();
        ReleasePort(sp.Port);
        _logger.LogInformation("Stopped waved for store {StoreId}", storeId);
    }

    /// <summary>
    /// Permanently destroys this store's wallet: stops the process, deletes its on-disk datadir
    /// (seed, DB, wallet_password file - unrecoverable, unlike the automatic-removal path this
    /// plugin otherwise never takes, see the class doc comment), and clears the persisted
    /// password so the next Create bootstraps a genuinely fresh wallet. Deliberately does NOT
    /// clear ExtraWavedFlags - network/wallet.type/wallet.feeurl etc. are configuration for how
    /// this store should run, not part of the wallet/seed being destroyed; wiping them here would
    /// silently fall back to server defaults (e.g. mainnet) on the next start, which is exactly
    /// what happened before this was fixed. Callers are responsible for confirming this with a
    /// human first - this method itself does not ask.
    /// </summary>
    public async Task DeleteStoreDataAsync(string storeId)
    {
        await StopStoreAsync(storeId);
        _mnemonicCache.Acknowledge(storeId);

        var settings = await _storeRepository.GetSettingAsync<WavedStoreSettings>(storeId, WavedStoreSettings.SettingsKey);
        if (settings is not null)
        {
            await _storeRepository.UpdateSetting(storeId, WavedStoreSettings.SettingsKey, settings with
            {
                EncryptedWalletPassword = null
            });
        }

        var dataDir = _config.GetStoreDataDir(storeId);
        if (Directory.Exists(dataDir))
            Directory.Delete(dataDir, recursive: true);

        _logger.LogWarning("Deleted all Wavelength wallet data for store {StoreId} at operator request", storeId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wavedPath = ResolveBinaryPath("waved");
        if (!File.Exists(wavedPath))
        {
            _logger.LogWarning(
                "waved binary not found at {Path} (rid: {Rid}) - per-store wallets will not start. " +
                "Place the waved binary under Native/{Rid}/waved.",
                wavedPath, GetRuntimeIdentifier(), GetRuntimeIdentifier());
            return;
        }

        _storeRemovedSub = _eventAggregator.Subscribe<StoreEvent.Removed>((_, ev) =>
        {
            _logger.LogInformation("Store {StoreId} removed, queuing waved shutdown (data is kept on disk)", ev.StoreId);
            _removedStoreIds.Enqueue(ev.StoreId);
        });

        try
        {
            // Only restart waved for stores that were previously initialized (have wallet data
            // on disk). A brand-new store starts on demand via EnsureStartedAsync.
            var storesDir = Path.Combine(_config.DataDir, "stores");
            if (Directory.Exists(storesDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(storesDir))
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var storeId = Path.GetFileName(dir);
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        continue;

                    if (await _storeRepository.FindStore(storeId) is null)
                    {
                        _logger.LogInformation("Skipping orphaned wallet directory for deleted store {StoreId}", storeId);
                        continue;
                    }

                    await EnsureStartedAsync(storeId, cancellationToken: stoppingToken);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

                while (_removedStoreIds.TryDequeue(out var removedId))
                {
                    try
                    {
                        await StopStoreAsync(removedId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to stop waved for removed store {StoreId}", removedId);
                    }
                }

                foreach (var (storeId, sp) in _stores.ToArray())
                {
                    if (sp.Process is not { HasExited: true })
                        continue;

                    _logger.LogWarning(
                        "waved for store {StoreId} exited unexpectedly with code {ExitCode}, restarting",
                        storeId, sp.Process.ExitCode);
                    sp.Process.Dispose();
                    // Disposing the channel matters beyond cleanup: it's a plaintext, unauthenticated
                    // loopback gRPC channel (rpc.notls/rpc.no-macaroons) tied only to an IP:port, with
                    // nothing to verify the process on the other end is still this store's waved
                    // instance. If a stale reference to this channel outlived the crash and its port
                    // gets reused by a different store before we get here, an un-disposed channel
                    // would happily reconnect and silently talk to the wrong store's wallet. Disposing
                    // it now makes any such stale reference fail fast instead.
                    sp.Channel.Dispose();
                    _stores.TryRemove(storeId, out _);
                    ReleasePort(sp.Port);

                    try
                    {
                        await EnsureStartedAsync(storeId, cancellationToken: stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to restart waved for store {StoreId}", storeId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WavedProcessManager failed");
        }
        finally
        {
            _storeRemovedSub?.Dispose();
            await Task.WhenAll(_stores.Keys.Select(StopStoreAsync));
        }
    }

    private async Task StartStoreAsync(
        string storeId, IReadOnlyDictionary<string, string> extraFlags, CancellationToken cancellationToken)
    {
        var dataDir = _config.GetStoreDataDir(storeId);
        Directory.CreateDirectory(dataDir);

        // Written unconditionally, even for a brand-new store with no wallet yet: waved simply
        // ignores it when there's nothing to auto-unlock (see waved/server.go's "no wallet
        // found, awaiting InitWallet RPC" path). We still need the same password in hand below
        // to actually call Create on first boot.
        var password = await _credentialStore.GetOrCreatePasswordAsync(storeId, cancellationToken);
        var passwordFilePath = WritePasswordFile(dataDir, password);

        var port = ReserveFreePort();
        var uri = new Uri($"http://{_config.Host}:{port}");
        var wavedPath = ResolveBinaryPath("waved");

        Process process;
        List<string> startupStderr;
        string flagsLog;
        try
        {
            // Server-wide default first, then whatever the store's connection string actually
            // asked for (skipping anything WavedReservedFlags owns - the connection string
            // handler already rejects those at Create() time, this is defense in depth), then the
            // plugin-owned flags always win regardless of what's upstream of them.
            var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["network"] = _config.Network,
            };
            foreach (var (key, value) in extraFlags)
            {
                if (!WavedReservedFlags.Keys.Contains(key))
                    flags[key] = value;
            }
            flags["datadir"] = dataDir;
            flags["rpc.listenaddr"] = $"{_config.Host}:{port}";
            flags["wallet.password_file"] = passwordFilePath;
            // waved is only ever bound to loopback and only ever talked to by this plugin, so
            // plaintext RPC is acceptable here - a macaroon can't ride an unencrypted connection,
            // so these two flags must be passed together (see wavelength's INSTALL.md). Actual
            // flag names are rpc.notls / rpc.no-macaroons (waved/config.go mapstructure tags) -
            // NOT --no-tls/--no-macaroons, which waved rejects outright ("unknown flag").
            flags["rpc.notls"] = null;
            flags["rpc.no-macaroons"] = null;

            var startInfo = new ProcessStartInfo
            {
                FileName = wavedPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var (key, value) in flags)
            {
                startInfo.ArgumentList.Add($"--{key}");
                if (value is not null)
                    startInfo.ArgumentList.Add(value);
            }
            // Must be a single --flag=false argument, not two separate ones ("--flag" "false") -
            // pflag/cobra bool flags don't consume the next argument as their value; --flag alone
            // sets it true, and only the combined --flag=false form explicitly negates a
            // default-true flag like this one. See WavedReservedFlags for why it's disabled.
            startInfo.ArgumentList.Add("--rpc.gateway.enabled=false");

            // Captured so a startup failure can report *why* waved exited, not just its exit code
            // - an exit code alone is useless for diagnosing a bad flag/config combination.
            startupStderr = [];

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => LogOutput(storeId, e.Data, isError: false);
            process.ErrorDataReceived += (_, e) =>
            {
                LogOutput(storeId, e.Data, isError: true);
                if (!string.IsNullOrEmpty(e.Data))
                {
                    lock (startupStderr) startupStderr.Add(e.Data);
                }
            };

            if (!process.Start())
                throw new InvalidOperationException($"Failed to start waved for store {storeId}");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // GrpcChannel.ForAddress doesn't connect eagerly - the actual connection attempt
            // happens on the first RPC call, by which point WaitForReadyAsync below has confirmed
            // the port is accepting connections.
            var channel = GrpcChannel.ForAddress(uri, new GrpcChannelOptions { Credentials = ChannelCredentials.Insecure });
            _stores[storeId] = new StoreProcess(process, uri, port, channel, extraFlags);
            flagsLog = string.Join(' ', flags.Select(kv => kv.Value is null ? $"--{kv.Key}" : $"--{kv.Key}={kv.Value}"))
                + " --rpc.gateway.enabled=false";
        }
        catch
        {
            // Nothing made it into _stores, so the port would otherwise stay reserved forever
            // with nothing ever going to release it - ReserveFreePort's whole point is a
            // reservation that lasts until the store actually stops, and a store that never
            // started counts as already stopped.
            ReleasePort(port);
            throw;
        }

        await WaitForReadyAsync(storeId, process, _config.Host, port, startupStderr, cancellationToken);

        _logger.LogInformation("Started waved for store {StoreId} on port {Port} with flags: {Flags}", storeId, port, flagsLog);
    }

    /// <summary>
    /// Picks a port for a new waved instance by actually probing it, rather than handing out the
    /// next number from a simple counter. A counter resets to BasePort every time
    /// WavedProcessManager is constructed (i.e. every BTCPay restart), but a waved process that
    /// was never cleanly killed - e.g. an ungraceful shutdown that didn't give TerminateProcessAsync
    /// a chance to run - keeps its old port bound indefinitely. Reusing that same port for a
    /// different store on the next restart produces exactly the "address already in use" error
    /// this fixes.
    ///
    /// The port is added to <see cref="_reservedPorts"/> before the lock is released, and stays
    /// reserved until <see cref="ReleasePort"/> is called - not just for the instant of the probe.
    /// A probe-then-immediately-release check alone isn't enough: the real waved process still has
    /// to be spawned and reach its own listen() call after this method returns, which takes real
    /// wall-clock time (building flags, forking/exec'ing, waved's own startup). Without a
    /// persistent reservation, a second store's StartStoreAsync racing in that gap would probe the
    /// same now-briefly-free port, see it as available too, and both would try to bind it - exactly
    /// reproducing the "address already in use" bug this is meant to fix, just moved from
    /// "orphaned process from a previous run" to "two stores starting at the same time" (which
    /// BTCPay's own background Lightning listener can trigger across multiple stores concurrently,
    /// independent of when a human happens to visit each store's dashboard).
    /// </summary>
    private int ReserveFreePort()
    {
        lock (_portLock)
        {
            _logger.LogInformation(
                "Reserving a port starting from {BasePort} - currently reserved: {ReservedPorts}",
                _config.BasePort, string.Join(',', _reservedPorts.OrderBy(p => p)));

            for (var port = _config.BasePort; port < _config.BasePort + 10_000; port++)
            {
                if (_reservedPorts.Contains(port))
                {
                    _logger.LogInformation(
                        "Port {Port} is already reserved by our own bookkeeping - trying the next one",
                        port);
                    continue;
                }

                try
                {
                    using var probe = new TcpListener(IPAddress.Parse(_config.Host), port);
                    probe.Start();
                }
                catch (SocketException)
                {
                    _logger.LogInformation(
                        "Port {Port} is already bound by something outside our own tracking " +
                        "(possibly an orphaned waved instance from before a restart) - trying the next one",
                        port);
                    continue;
                }

                _reservedPorts.Add(port);
                return port;
            }

            throw new InvalidOperationException(
                $"No free port found in range {_config.BasePort}-{_config.BasePort + 10_000} for a new waved instance");
        }
    }

    /// <summary>Frees a port reserved by <see cref="ReserveFreePort"/> once its store has stopped
    /// (or never made it past its own start attempt) - see every call site for why each one is safe.</summary>
    private void ReleasePort(int port)
    {
        lock (_portLock)
        {
            _reservedPorts.Remove(port);
        }
    }

    /// <summary>
    /// True once this store has an actual wallet (its process must already be running - call
    /// EnsureStartedAsync first). Starting the waved process never implies a wallet exists -
    /// see CreateWalletAsync's doc comment for why creation is a separate, explicit step.
    /// </summary>
    public async Task<bool> WalletExistsAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var wallet = GetWalletClient(storeId);
        if (wallet is null)
            return false;

        try
        {
            var status = await wallet.StatusAsync(new StatusRequest(), cancellationToken: cancellationToken);
            return status.Unlocked;
        }
        catch (RpcException)
        {
            // waved's Status RPC (swapwallet/service.go) unconditionally fetches the wallet
            // balance before it can report readiness, so it fails outright - rather than
            // returning Unlocked=false - when no wallet exists yet.
            return false;
        }
    }

    /// <summary>
    /// Explicitly creates this store's wallet (its process must already be running - call
    /// EnsureStartedAsync first) and returns its mnemonic, or null if a wallet already existed
    /// (a safe no-op - see InitWallet's atomic guard in waved/rpc_wallet.go). Deliberately never
    /// called automatically: wallet creation generates a seed that only ever exists in memory
    /// until the caller shows it to a human (see WavedMnemonicPendingCache) - it must never happen
    /// as a side effect of something else (a dashboard page load, or worse, an inbound Lightning
    /// payment attempt on a store nobody has finished setting up yet) where nobody is watching
    /// for it.
    ///
    /// Deliberately takes no CancellationToken parameter: this is invoked from an HTTP request
    /// (the "Create wallet" button), and once Create is sent there is no safe way to abort it -
    /// InitWallet's server-side effects are already committed by the time it returns, so
    /// cancelling our wait for the response would silently orphan the mnemonic. That is exactly
    /// what happened before this fix: the request appeared to time out, the wallet was created
    /// regardless, and the mnemonic was never received - and thus never shown to anyone - because
    /// our own code stopped waiting for the response. A token parameter here would invite exactly
    /// that mistake again, so there deliberately isn't one; this method always runs to its own
    /// internal timeout regardless of what happens to the caller's request.
    /// </summary>
    public async Task<string[]?> CreateWalletAsync(string storeId)
    {
        var wallet = GetWalletClient(storeId)
            ?? throw new InvalidOperationException($"waved for store {storeId} is not running");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var password = await _credentialStore.GetOrCreatePasswordAsync(storeId, cts.Token);

        _logger.LogInformation("Creating wallet for store {StoreId}", storeId);
        try
        {
            var response = await wallet.CreateAsync(new CreateRequest
            {
                WalletPassword = ByteString.CopyFromUtf8(password)
            }, cancellationToken: cts.Token);

            var mnemonic = response.Mnemonic.ToArray();

            // The mnemonic is never persisted anywhere - held in memory only until the store
            // owner explicitly acknowledges writing it down (see WavedMnemonicPendingCache) or
            // the process restarts, whichever is first. Re-visiting the dashboard or the mnemonic
            // page in the meantime re-shows the same phrase rather than losing it. This cache
            // entry is also the hand-off from CreateWallet's POST action to the Mnemonic GET
            // action it redirects to - there is no other copy of this value.
            _mnemonicCache.Store(storeId, string.Join(' ', mnemonic));

            return mnemonic;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            _logger.LogInformation(
                "Wallet for store {StoreId} already exists ({Detail}); Create was a no-op",
                storeId, ex.Status.Detail);
            return null;
        }
    }

    /// <summary>True while a Create RPC for this store is in flight.</summary>
    public bool IsCreatingWallet(string storeId)
        => _creationTasks.TryGetValue(storeId, out var task) && !task.IsCompleted;

    /// <summary>
    /// Kicks off wallet creation without waiting for it to finish. waved's InitWallet can take
    /// long enough that awaiting it inline in an HTTP request risks a reverse proxy's own gateway
    /// timeout firing before waved responds - showing the visitor a 504 even though the wallet
    /// really did get created a moment later, and (without this) leaving the mnemonic sitting
    /// unseen in WavedMnemonicPendingCache forever, since nothing would ever redirect to Mnemonic to
    /// collect it. Callers should redirect the visitor to a page that polls IsCreatingWallet /
    /// WalletExistsAsync instead of awaiting this directly - see UIWavelengthController's
    /// Index/CreateWallet actions. Safe to call while a creation for this store is already in
    /// flight - returns the same task rather than starting a redundant one.
    /// </summary>
    public Task<string[]?> StartCreateWalletAsync(string storeId)
    {
        lock (_creationLock)
        {
            if (_creationTasks.TryGetValue(storeId, out var existing) && !existing.IsCompleted)
                return existing;

            var task = CreateWalletAsync(storeId);
            _creationTasks[storeId] = task;
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "Wallet creation failed for store {StoreId}", storeId);
            }, TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>
    /// Returns and clears the error from this store's most recent failed background creation, or
    /// false if the last attempt succeeded (or none has run). Take-once, unlike
    /// WavedMnemonicPendingCache's Peek - shown once via TempData, not on every subsequent
    /// dashboard reload.
    /// </summary>
    public bool TryGetCreationError(string storeId, out string? error)
    {
        error = null;
        if (!_creationTasks.TryGetValue(storeId, out var task) || !task.IsFaulted)
            return false;

        _creationTasks.TryRemove(storeId, out _);
        var ex = task.Exception?.InnerException ?? task.Exception;
        error = ex is RpcException rpcEx ? rpcEx.Status.Detail : ex?.Message;
        return true;
    }

    private static string WritePasswordFile(string dataDir, string password)
    {
        var path = Path.Combine(dataDir, "wallet_password");
        File.WriteAllText(path, password);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task WaitForReadyAsync(
        string storeId, Process process, string host, int port, List<string> startupStderr, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var delayMs = 50;
        while (!timeoutCts.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                if (_stores.TryRemove(storeId, out var failed))
                    failed.Channel.Dispose();
                ReleasePort(port);

                string detail;
                lock (startupStderr)
                {
                    detail = startupStderr.Count > 0
                        ? string.Join(" | ", startupStderr)
                        : "(waved printed nothing to stderr - check its stdout in the BTCPay log for [waved:" + storeId + "] lines)";
                }
                throw new InvalidOperationException(
                    $"waved for store {storeId} exited during startup with code {process.ExitCode}: {detail}");
            }
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(host, port, timeoutCts.Token);
                return;
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { break; }
            try { await Task.Delay(delayMs, timeoutCts.Token); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { break; }
            delayMs = Math.Min(delayMs * 2, 500);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"waved for store {storeId} did not become ready on {host}:{port} within 30 seconds");
    }

    private async Task TerminateProcessAsync(Process process)
    {
        if (process.HasExited)
            return;

        _logger.LogInformation("Stopping waved (PID {Pid})", process.Id);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var killInfo = new ProcessStartInfo
                {
                    FileName = "kill",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                killInfo.ArgumentList.Add("-TERM");
                killInfo.ArgumentList.Add(process.Id.ToString());
                using var kill = Process.Start(killInfo);
                kill?.WaitForExit(1000);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SIGTERM failed, falling back to Kill()");
            }

            if (await WaitForExitAsync(process, TimeSpan.FromSeconds(10)))
                return;

            _logger.LogWarning("waved did not exit gracefully, forcing termination");
        }

        process.Kill(entireProcessTree: true);
        await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal static string GetRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => throw new PlatformNotSupportedException(
                    $"Unsupported Linux architecture: {RuntimeInformation.OSArchitecture}")
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => throw new PlatformNotSupportedException(
                    $"Unsupported macOS architecture: {RuntimeInformation.OSArchitecture}")
            };
        }

        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }

    private string ResolveBinaryPath(string binaryName)
        => Path.Combine(_nativeDir, GetRuntimeIdentifier(), binaryName);

    private void LogOutput(string storeId, string? data, bool isError)
    {
        if (string.IsNullOrEmpty(data))
            return;

        if (isError)
            _logger.LogWarning("[waved:{StoreId}] {Output}", storeId, data);
        else
            _logger.LogDebug("[waved:{StoreId}] {Output}", storeId, data);
    }

    public override void Dispose()
    {
        if (_disposed) return;

        foreach (var sp in _stores.Values)
        {
            sp.Channel.Dispose();
            sp.Process.Dispose();
        }
        _stores.Clear();

        lock (_portLock)
            _reservedPorts.Clear();

        foreach (var startLock in _startLocks.Values)
            startLock.Dispose();
        _startLocks.Clear();

        _disposed = true;
        base.Dispose();
    }

    private sealed record StoreProcess(
        Process Process, Uri Uri, int Port, GrpcChannel Channel, IReadOnlyDictionary<string, string> Flags);
}
