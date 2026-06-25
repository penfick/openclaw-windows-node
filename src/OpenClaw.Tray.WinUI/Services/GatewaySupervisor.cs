using System.Net.Sockets;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Supervises the active setup-managed <b>native</b> gateway and restarts it when the
/// process dies. The native gateway runs as an <c>openclaw gateway install</c>-registered
/// Scheduled Task with no restart policy, and a clean process exit (e.g. after a config
/// hot-reload) leaves nothing listening — so the tray's WebSocket reconnects all fail until
/// someone manually runs <c>openclaw gateway start</c>. This service closes that gap.
///
/// Only native-managed local gateways are supervised. Remote / SSH / WSL-unmanaged gateways
/// are skipped (we cannot restart those from here). A user-initiated Stop suppresses restart
/// until the user starts the gateway again.
///
/// The loop is probe-driven: every <see cref="_probeInterval"/> it checks the gateway port.
/// When closed, it runs <c>openclaw gateway start</c> and sets a "reconnect pending" flag;
/// it does NOT verify the boot inline, because the gateway cold-start takes a variable amount
/// of time to bind and <c>openclaw gateway start</c> returns before the port is listening.
/// The next probe that finds the port open clears the flag and triggers a WS reconnect.
/// </summary>
internal sealed class GatewaySupervisor : IDisposable
{
    private readonly GatewayRegistry _registry;
    private readonly IOpenClawLogger _logger;
    private readonly Func<CancellationToken, Task<GatewayControlResult>> _startGatewayAsync;
    private readonly Func<Task> _reconnectAsync;
    private readonly Func<bool>? _isSuppressed;
    private readonly TimeSpan _probeInterval;
    private readonly TimeSpan _probeTimeout;

    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _userStopped;            // 1 = user intentionally stopped; suppress auto-restart
    private int _reconnectPending;       // 1 = a restart was issued; reconnect once the port is back
    private DateTime _portDownSinceUtc = DateTime.MaxValue;  // when the port was first seen closed
    private DateTime _lastStartUtc = DateTime.MinValue;      // when `openclaw gateway start` was last issued

    // `openclaw gateway start` RESTARTS the Scheduled Task — it kills an already-running
    // gateway and boots a fresh one. A native cold-boot (especially right after a kill, while
    // recovering an interrupted session) can take a minute or more to bind the port. So:
    //  - InitialGrace: how long the port must be continuously down before the FIRST restart
    //    on a fresh death (filters transient blips; the gateway is truly dead, so this is short).
    //  - PostStartBootWindow: after we issue `start`, we wait this long before issuing another
    //    one, giving the booting gateway time to bind. Must exceed worst-case boot time, else
    //    we kill the booting gateway and loop.
    private static readonly TimeSpan InitialGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PostStartBootWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Raised (best-effort, fire-and-forget) whenever the supervisor reconnects after a
    /// recovery, so the UI can surface "gateway recovered" if desired. May be null.
    /// </summary>
    public Action<string>? RecoveryNotice { get; set; }

    public GatewaySupervisor(
        GatewayRegistry registry,
        IOpenClawLogger logger,
        Func<CancellationToken, Task<GatewayControlResult>> startGatewayAsync,
        Func<Task> reconnectAsync,
        TimeSpan? probeInterval = null,
        Func<bool>? isSuppressed = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startGatewayAsync = startGatewayAsync ?? throw new ArgumentNullException(nameof(startGatewayAsync));
        _reconnectAsync = reconnectAsync ?? throw new ArgumentNullException(nameof(reconnectAsync));
        _probeInterval = probeInterval ?? TimeSpan.FromSeconds(20);
        _probeTimeout = TimeSpan.FromSeconds(2);
        _isSuppressed = isSuppressed;
    }

    /// <summary>True while the user has intentionally stopped the gateway.</summary>
    public bool IsUserStopped => Interlocked.CompareExchange(ref _userStopped, 0, 0) == 1;

    /// <summary>Start the supervision loop. Safe to call once.</summary>
    public void Start()
    {
        if (_loopTask != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => SuperviseLoopAsync(token), token);
        _logger.Info("[GatewaySupervisor] Started — supervising native-managed gateway.");
    }

    /// <summary>Stop the loop and wait for it to drain.</summary>
    public async Task StopAsync()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null) return;
        cts.Cancel();
        var loop = _loopTask;
        if (loop != null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Warn($"[GatewaySupervisor] Loop ended with error: {ex.Message}"); }
        }
        _loopTask = null;
        cts.Dispose();
        _logger.Info("[GatewaySupervisor] Stopped.");
    }

    /// <summary>User clicked Stop — suppress auto-restart until <see cref="NotifyUserStarted"/>.</summary>
    public void NotifyUserStopped()
    {
        Interlocked.Exchange(ref _userStopped, 1);
        Interlocked.Exchange(ref _reconnectPending, 0);
        _logger.Info("[GatewaySupervisor] User stopped gateway — auto-restart suppressed.");
    }

    /// <summary>User clicked Start (or setup completed) — resume supervision.</summary>
    public void NotifyUserStarted()
    {
        Interlocked.Exchange(ref _userStopped, 0);
        _portDownSinceUtc = DateTime.MaxValue;
        _lastStartUtc = DateTime.MinValue;
        _logger.Info("[GatewaySupervisor] User started gateway — supervision resumed.");
    }

    private async Task SuperviseLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_probeInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await ProbeAndRecoverAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.Warn($"[GatewaySupervisor] Probe cycle error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProbeAndRecoverAsync(CancellationToken ct)
    {
        if (IsUserStopped) return;

        // Suppress while the setup wizard is open — setup manages the gateway lifecycle
        // (stop/reinstall/restart) and a concurrent supervisor restart would race it.
        if (_isSuppressed?.Invoke() == true) return;

        var record = _registry.GetActive();
        if (record is null) return;
        if (record.SetupManagedKind != GatewayInstallKind.Native) return;

        if (!TryGetEndpoint(record.Url, out var host, out int port))
        {
            _logger.Debug($"[GatewaySupervisor] Could not parse endpoint from '{record.Url}'; skipping.");
            return;
        }

        if (await IsPortOpenAsync(host, port, _probeTimeout).ConfigureAwait(false))
        {
            // Gateway is healthy. If we recently restarted, reconnect the WS client now that
            // the port is actually listening.
            _portDownSinceUtc = DateTime.MaxValue;
            if (Interlocked.CompareExchange(ref _reconnectPending, 0, 1) == 1)
            {
                _logger.Info("[GatewaySupervisor] Gateway port open after restart — reconnecting operator client.");
                await TriggerReconnectAsync().ConfigureAwait(false);
                try { RecoveryNotice?.Invoke("Gateway was down and has been restarted."); }
                catch (Exception ex) { _logger.Debug($"[GatewaySupervisor] RecoveryNotice handler threw: {ex.Message}"); }
            }
            return;
        }

        // Port is closed. Decide whether to restart:
        //  - Wait InitialGrace from when it first went down (filters transient blips, and a
        //    fresh death has no booting process to protect, so this stays short).
        //  - Wait PostStartBootWindow from the last `start` (gives a booting gateway time to
        //    bind — `start` kills the running gateway, so re-starting mid-boot would loop).
        if (_portDownSinceUtc == DateTime.MaxValue)
            _portDownSinceUtc = DateTime.UtcNow;

        var now = DateTime.UtcNow;
        var downFor = now - _portDownSinceUtc;
        var sinceStart = now - _lastStartUtc;

        if (downFor < InitialGrace)
        {
            _logger.Debug($"[GatewaySupervisor] Port closed for {downFor.TotalSeconds:F0}s (< {InitialGrace.TotalSeconds:F0}s grace); waiting.");
            return;
        }

        if (sinceStart < PostStartBootWindow)
        {
            _logger.Debug($"[GatewaySupervisor] Port closed; {sinceStart.TotalSeconds:F0}s since last start (< {PostStartBootWindow.TotalSeconds:F0}s boot window); waiting.");
            return;
        }

        await TryRestartAsync(host, port, ct).ConfigureAwait(false);
    }

    private async Task TryRestartAsync(string host, int port, CancellationToken ct)
    {
        // Serialize restart attempts so the periodic loop and any external trigger can't race.
        await _restartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: another cycle may have just restarted it.
            if (await IsPortOpenAsync(host, port, _probeTimeout).ConfigureAwait(false))
            {
                _portDownSinceUtc = DateTime.MaxValue;
                return;
            }

            _logger.Warn($"[GatewaySupervisor] Port down past grace/boot window — running 'openclaw gateway start'.");

            GatewayControlResult result;
            try { result = await _startGatewayAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.Error($"[GatewaySupervisor] Start attempt threw: {ex.Message}");
                // Stamp _lastStartUtc so we wait a full boot window before retrying.
                _lastStartUtc = DateTime.UtcNow;
                return;
            }

            if (!result.Success)
            {
                _logger.Warn($"[GatewaySupervisor] 'openclaw gateway start' exited {result.ExitCode}: {result.OutputSummary}");
                _lastStartUtc = DateTime.UtcNow;
                return;
            }

            // Start accepted. Stamp _lastStartUtc so the new boot gets a full
            // PostStartBootWindow before we'd consider restarting again (prevents killing a
            // mid-boot gateway). The next probe that finds the port open will reconnect.
            _lastStartUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _reconnectPending, 1);
            _logger.Info("[GatewaySupervisor] 'openclaw gateway start' accepted; awaiting bind on next probe.");
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private async Task TriggerReconnectAsync()
    {
        try
        {
            var reconnect = _reconnectAsync();
            _ = reconnect.ContinueWith(
                t => _logger.Warn($"[GatewaySupervisor] Post-recovery reconnect failed: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            await reconnect.ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.Warn($"[GatewaySupervisor] Post-recovery reconnect threw: {ex.Message}"); }
    }

    private static bool TryGetEndpoint(string? url, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // Normalize "localhost" to 127.0.0.1. The native gateway binds IPv4 loopback only
        // (bind=loopback → 127.0.0.1), and TcpClient.ConnectAsync("localhost") may resolve ::1
        // first and report a spurious failure, which would make the supervisor think the
        // gateway is down and `openclaw gateway start` it — killing the healthy process.
        host = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : uri.Host;
        port = uri.Port;
        return !string.IsNullOrWhiteSpace(host) && port > 0;
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        cts?.Dispose();
        _restartGate.Dispose();
    }
}
