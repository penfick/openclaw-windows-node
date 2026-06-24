using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>Gateway lifecycle action. Runtime-agnostic — WSL and Native controllers both implement it.</summary>
internal enum GatewayControlAction
{
    Start,
    Stop,
    Restart
}

/// <summary>Result of a gateway control action.</summary>
internal sealed record GatewayControlResult(
    GatewayControlAction Action,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? LocationLabel)
{
    public bool Success => ExitCode == 0;

    public string OutputSummary
    {
        get
        {
            var summary = string.IsNullOrWhiteSpace(StandardOutput) ? StandardError : StandardOutput;
            return string.IsNullOrWhiteSpace(summary) ? string.Empty : summary.Trim();
        }
    }
}

/// <summary>
/// Gateway lifecycle controller. The connection UI uses this to start/stop/restart the
/// active gateway regardless of how it was installed.
/// <list type="bullet">
/// <item><see cref="WslGatewayController"/> — runs <c>openclaw gateway &lt;verb&gt;</c> inside the app-owned WSL distro.</item>
/// <item><c>NativeGatewayController</c> (planned) — runs <c>openclaw.exe gateway &lt;verb&gt;</c> as a native Windows process.</item>
/// </list>
/// </summary>
internal interface IGatewayController
{
    Task<GatewayControlResult> RunAsync(GatewayControlAction action, CancellationToken cancellationToken = default);
}

/// <summary>
/// Picks the right gateway controller for an active <see cref="GatewayRecord"/>:
/// <list type="bullet">
/// <item><see cref="GatewayInstallKind.Native"/> → <see cref="NativeGatewayController"/> (openclaw.exe Scheduled Task).</item>
/// <item>WSL distro present → <see cref="WslGatewayController"/> (bound to that distro).</item>
/// <item>otherwise (remote/SSH/unknown) → null (not app-managed; start/stop/restart disabled).</item>
/// </list>
/// </summary>
internal static class GatewayControllerFactory
{
    public static IGatewayController? Create(GatewayRecord? record, IWslCommandRunner wslRunner, IOpenClawLogger logger)
    {
        if (record is null)
            return null;

        if (record.SetupManagedKind == GatewayInstallKind.Native)
            return new NativeGatewayController(logger);

        var distro = record.SetupManagedDistroName?.Trim();
        if (!string.IsNullOrWhiteSpace(distro))
            return new WslGatewayController(distro, wslRunner, logger);

        return null;
    }
}
