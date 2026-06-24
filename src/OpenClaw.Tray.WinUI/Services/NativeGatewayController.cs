using System.Diagnostics;
using System.Text;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Native Windows gateway lifecycle controller. Implements <see cref="IGatewayController"/> by
/// shelling out to the native <c>openclaw</c> CLI (installed via install.ps1 to the npm global
/// bin, e.g. <c>D:\nodejs\node_global\openclaw.cmd</c>) and invoking
/// <c>openclaw gateway &lt;start|stop|restart&gt;</c>, which on Windows controls the
/// <c>openclaw gateway install</c>-registered Scheduled Task service.
///
/// Verified against openclaw 2026.6.10: <c>openclaw gateway install --port --token --force</c>
/// creates a schtasks service; <c>gateway start/stop/restart</c> control it; default port 18789,
/// loopback bind, dashboard http://127.0.0.1:18789/. Native config lives at
/// <c>%USERPROFILE%\.openclaw\openclaw.json</c>.
/// </summary>
internal sealed class NativeGatewayController : IGatewayController
{
    private readonly IOpenClawLogger _logger;
    private readonly TimeSpan _timeout;

    public NativeGatewayController(IOpenClawLogger logger, TimeSpan? timeout = null)
    {
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromSeconds(45);
    }

    public async Task<GatewayControlResult> RunAsync(GatewayControlAction action, CancellationToken cancellationToken = default)
    {
        var verb = action switch
        {
            GatewayControlAction.Start => "start",
            GatewayControlAction.Stop => "stop",
            GatewayControlAction.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported gateway action."),
        };

        // cmd /c so PATH resolution finds the openclaw.cmd shim (node-based); propagates exit code.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c openclaw gateway {verb}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        _logger.Info($"[NativeGateway] openclaw gateway {verb}");

        int exitCode;
        string stdout, stderr;
        using (var process = new Process { StartInfo = psi })
        {
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return new GatewayControlResult(action, -1, string.Empty, $"Failed to start openclaw: {ex.Message}", "native Windows");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            try { stdout = await stdoutTask; } catch { stdout = string.Empty; }
            try { stderr = await stderrTask; } catch { stderr = string.Empty; }
            exitCode = timedOut ? -1 : process.ExitCode;
            if (timedOut)
                stderr = string.IsNullOrWhiteSpace(stderr) ? "openclaw gateway timed out" : stderr + "\nopenclaw gateway timed out";
        }

        return new GatewayControlResult(action, exitCode, stdout, stderr, "native Windows");
    }
}
