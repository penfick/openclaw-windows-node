using System.Collections.ObjectModel;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

public enum WslGatewayControlAction
{
    Start,
    Stop,
    Restart
}

internal sealed record WslGatewayControlResult(
    string DistroName,
    WslGatewayControlAction Action,
    int ExitCode,
    string StandardOutput,
    string StandardError)
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

internal static class WslGatewayControlCommandBuilder
{
    internal const string OpenClawWslPathPrefix = "export PATH=\"/home/openclaw/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:$PATH\"";

    public static IReadOnlyList<string> Build(WslGatewayControlAction action)
    {
        return new ReadOnlyCollection<string>([
            "bash",
            "-lc",
            $"{OpenClawWslPathPrefix} && openclaw gateway {ToVerb(action)}"
        ]);
    }

    public static string ToVerb(WslGatewayControlAction action)
    {
        return action switch
        {
            WslGatewayControlAction.Start => "start",
            WslGatewayControlAction.Stop => "stop",
            WslGatewayControlAction.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported WSL gateway action.")
        };
    }
}

/// <summary>
/// WSL gateway lifecycle controller. Implements <see cref="IGatewayController"/> with the distro
/// bound at construction (factory use); also retains a legacy per-call overload so the current
/// <c>ConnectionPage</c> call site compiles unchanged until the factory wiring lands.
/// </summary>
internal sealed class WslGatewayController : IGatewayController
{
    private readonly string? _distroName;
    private readonly IWslCommandRunner _commandRunner;
    private readonly IOpenClawLogger _logger;

    /// <summary>Back-compat ctor (distro passed per-call). Used by current ConnectionPage.</summary>
    public WslGatewayController(IWslCommandRunner commandRunner, IOpenClawLogger logger)
    {
        _distroName = null;
        _commandRunner = commandRunner;
        _logger = logger;
    }

    /// <summary>Distro-bound ctor for the <see cref="IGatewayController"/> factory.</summary>
    public WslGatewayController(string distroName, IWslCommandRunner commandRunner, IOpenClawLogger logger)
    {
        _distroName = distroName;
        _commandRunner = commandRunner;
        _logger = logger;
    }

    /// <summary>Interface impl (distro bound at construction). Used by the gateway-control factory.</summary>
    public async Task<GatewayControlResult> RunAsync(GatewayControlAction action, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_distroName))
            throw new InvalidOperationException("WSL gateway controller was created without a distro name.");

        var wslAction = action switch
        {
            GatewayControlAction.Start => WslGatewayControlAction.Start,
            GatewayControlAction.Stop => WslGatewayControlAction.Stop,
            GatewayControlAction.Restart => WslGatewayControlAction.Restart,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported gateway action."),
        };
        var result = await RunCoreAsync(_distroName, wslAction, cancellationToken).ConfigureAwait(false);
        return new GatewayControlResult(
            action,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            $"WSL distro '{_distroName}'");
    }

    /// <summary>Legacy per-call overload (ConnectionPage current use). Delegates to the core runner.</summary>
    public Task<WslGatewayControlResult> RunAsync(
        string distroName,
        WslGatewayControlAction action,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(distroName, action, cancellationToken);

    private async Task<WslGatewayControlResult> RunCoreAsync(
        string distroName,
        WslGatewayControlAction action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(distroName))
        {
            throw new ArgumentException("WSL distro name is required.", nameof(distroName));
        }

        var normalizedDistroName = distroName.Trim();
        var distros = await _commandRunner.ListDistrosAsync(cancellationToken).ConfigureAwait(false);
        if (!distros.Any(distro => string.Equals(distro.Name, normalizedDistroName, StringComparison.OrdinalIgnoreCase)))
        {
            return new WslGatewayControlResult(
                normalizedDistroName,
                action,
                -1,
                string.Empty,
                $"WSL distro '{normalizedDistroName}' is not registered.");
        }

        _logger.Info($"Running OpenClaw gateway {WslGatewayControlCommandBuilder.ToVerb(action)} in WSL distro '{normalizedDistroName}'.");
        var result = await _commandRunner.RunInDistroAsync(
            normalizedDistroName,
            WslGatewayControlCommandBuilder.Build(action),
            cancellationToken).ConfigureAwait(false);

        return new WslGatewayControlResult(
            normalizedDistroName,
            action,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }
}
