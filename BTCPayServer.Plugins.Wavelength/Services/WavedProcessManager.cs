using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using BTCPayServer.Events;
using BTCPayServer.Plugins.Wavelength.Notifications;
using BTCPayServer.Services.Notifications;
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
    private readonly WavedMnemonicOnceCache _mnemonicCache;
    private readonly NotificationSender _notificationSender;
    private readonly ILogger<WavedProcessManager> _logger;
    private readonly string _nativeDir;

    private readonly ConcurrentDictionary<string, StoreProcess> _stores = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startLocks = new();
    private readonly ConcurrentQueue<string> _removedStoreIds = new();
    private IEventAggregatorSubscription? _storeRemovedSub;
    private int _nextPort;
    private bool _disposed;

    public WavedProcessManager(
        WavedConfiguration config,
        StoreRepository storeRepository,
        EventAggregator eventAggregator,
        WavedWalletCredentialStore credentialStore,
        WavedMnemonicOnceCache mnemonicCache,
        NotificationSender notificationSender,
        ILogger<WavedProcessManager> logger)
    {
        _config = config;
        _storeRepository = storeRepository;
        _eventAggregator = eventAggregator;
        _credentialStore = credentialStore;
        _mnemonicCache = mnemonicCache;
        _notificationSender = notificationSender;
        _logger = logger;
        _nextPort = config.BasePort;

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
    /// running. The channel is plaintext gRPC (h2c) - waved is started with --no-tls
    /// --no-macaroons and only ever binds to loopback, so this is only safe because the
    /// connection never leaves the host. See WavelengthPlugin.Execute for the AppContext switch
    /// that enables unencrypted HTTP/2 on the client side.
    /// </summary>
    public WalletServiceClient? GetWalletClient(string storeId)
        => _stores.TryGetValue(storeId, out var sp) ? new WalletServiceClient(sp.Channel) : null;

    public WalletInspectionServiceClient? GetWalletInspectionClient(string storeId)
        => _stores.TryGetValue(storeId, out var sp) ? new WalletInspectionServiceClient(sp.Channel) : null;

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
        _logger.LogInformation("Stopped waved for store {StoreId}", storeId);
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
                    _stores.TryRemove(storeId, out _);

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

        var port = Interlocked.Increment(ref _nextPort) - 1;
        var uri = new Uri($"http://{_config.Host}:{port}");
        var wavedPath = ResolveBinaryPath("waved");

        // Server-wide default first, then whatever the store's connection string actually asked
        // for (skipping anything WavedReservedFlags owns - the connection string handler already
        // rejects those at Create() time, this is defense in depth), then the plugin-owned flags
        // always win regardless of what's upstream of them.
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
        // so these two flags must be passed together (see wavelength's INSTALL.md).
        flags["no-tls"] = null;
        flags["no-macaroons"] = null;

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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => LogOutput(storeId, e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => LogOutput(storeId, e.Data, isError: true);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start waved for store {storeId}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // GrpcChannel.ForAddress doesn't connect eagerly - the actual connection attempt happens
        // on the first RPC call, by which point WaitForReadyAsync below has confirmed the port is
        // accepting connections.
        var channel = GrpcChannel.ForAddress(uri, new GrpcChannelOptions { Credentials = ChannelCredentials.Insecure });
        _stores[storeId] = new StoreProcess(process, uri, port, channel, extraFlags);

        await WaitForReadyAsync(storeId, process, _config.Host, port, cancellationToken);
        await EnsureWalletInitializedAsync(storeId, channel, password, cancellationToken);

        _logger.LogInformation("Started waved for store {StoreId} on port {Port} with flags: {Flags}", storeId, port,
            string.Join(' ', flags.Select(kv => kv.Value is null ? $"--{kv.Key}" : $"--{kv.Key}={kv.Value}")));
    }

    /// <summary>
    /// Bootstraps a brand-new store's wallet via WalletService.Create on first boot. For a store
    /// that already has a wallet on disk, waved's own --wallet.password_file auto-unlock (passed
    /// at process start above) has already taken care of it by the time we get here - Status
    /// reports Unlocked=true and there's nothing left to do.
    /// </summary>
    private async Task EnsureWalletInitializedAsync(
        string storeId, GrpcChannel channel, string password, CancellationToken cancellationToken)
    {
        var wallet = new WalletServiceClient(channel);
        var status = await wallet.StatusAsync(new StatusRequest(), cancellationToken: cancellationToken);
        if (status.Unlocked)
            return;

        _logger.LogInformation("No wallet found for store {StoreId}, creating one", storeId);
        var response = await wallet.CreateAsync(new CreateRequest
        {
            WalletPassword = ByteString.CopyFromUtf8(password)
        }, cancellationToken: cancellationToken);

        // The mnemonic is never persisted anywhere - held in memory only until the store owner
        // views it once (see WavedMnemonicOnceCache) or the process restarts, whichever is first.
        _mnemonicCache.Store(storeId, string.Join(' ', response.Mnemonic));
        await _notificationSender.SendNotification(
            new StoreScope(storeId),
            new WavelengthWalletCreatedNotification { StoreId = storeId });
    }

    private static string WritePasswordFile(string dataDir, string password)
    {
        var path = Path.Combine(dataDir, "wallet_password");
        File.WriteAllText(path, password);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task WaitForReadyAsync(string storeId, Process process, string host, int port, CancellationToken cancellationToken)
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
                throw new InvalidOperationException(
                    $"waved for store {storeId} exited during startup with code {process.ExitCode}");
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

        foreach (var startLock in _startLocks.Values)
            startLock.Dispose();
        _startLocks.Clear();

        _disposed = true;
        base.Dispose();
    }

    private sealed record StoreProcess(
        Process Process, Uri Uri, int Port, GrpcChannel Channel, IReadOnlyDictionary<string, string> Flags);
}
