using System;

namespace OpenClaw.Connection;

public enum GatewayTerminalTarget
{
    None,
    Wsl,
    Ssh
}

/// <summary>How (if at all) the app can start/stop/restart a setup-managed gateway.</summary>
public enum GatewayControlKind
{
    None,
    Wsl,
    Native
}

/// <summary>
/// Localization indirection for <see cref="GatewayHostAccessPlan"/> / <see cref="GatewayHostAccessClassifier"/>.
/// Defaults are identity (return the resource key unchanged) so the file is unit-testable
/// without a WinUI runtime. <c>App.xaml.cs</c> wires these up to <c>LocalizationHelper</c>
/// at startup so the running app sees real localized strings.
/// </summary>
public static class GatewayHostAccessLocalization
{
    public static Func<string, string> GetString { get; set; } = key => key;
    public static Func<string, object?[], string> Format { get; set; } = (key, _) => key;
}

public sealed record GatewayHostAccessPlan(
    string? GatewayId,
    GatewayTerminalTarget TerminalTarget,
    string? DistroName,
    string? SshUser,
    string? SshHost,
    bool CanControlWslGateway,
    GatewayControlKind GatewayControl,
    string TerminalLabel,
    string TerminalTooltip,
    string? DisabledReason)
{
    public bool CanOpenTerminal => TerminalTarget != GatewayTerminalTarget.None;

    /// <summary>True when the app can start/stop/restart this gateway (WSL or Native managed).</summary>
    public bool CanControlGateway => GatewayControl != GatewayControlKind.None;

    public bool IsWslManaged => !string.IsNullOrWhiteSpace(DistroName);

    /// <summary>Where the gateway runs, for "Starting gateway in …" status text.</summary>
    public string GatewayControlLocationLabel => GatewayControl switch
    {
        GatewayControlKind.Wsl => $"WSL distro '{DistroName}'",
        GatewayControlKind.Native => "native Windows",
        _ => string.Empty,
    };

    public static GatewayHostAccessPlan None(string? gatewayId = null, string? disabledReason = null)
    {
        var defaultReason = disabledReason ?? GatewayHostAccessLocalization.GetString("GatewayHostAccess_NoTerminalAccess");
        return new GatewayHostAccessPlan(
            gatewayId,
            GatewayTerminalTarget.None,
            null,
            null,
            null,
            false,
            GatewayControlKind.None,
            GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
            defaultReason,
            defaultReason);
    }
}

public static class GatewayHostAccessClassifier
{
    public static GatewayHostAccessPlan Classify(GatewayRecord? record)
    {
        if (record is null)
        {
            return GatewayHostAccessPlan.None();
        }

        // Native-managed gateway (install.ps1 + Scheduled Task). No distro, no terminal.
        if (record.SetupManagedKind == GatewayInstallKind.Native)
        {
            var noTerminal = GatewayHostAccessLocalization.GetString("GatewayHostAccess_NoTerminalAccess");
            return new GatewayHostAccessPlan(
                record.Id,
                GatewayTerminalTarget.None,
                null,
                null,
                null,
                false,
                GatewayControlKind.Native,
                GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
                noTerminal,
                noTerminal);
        }

        var distroName = Normalize(record.SetupManagedDistroName);
        var sshUser = Normalize(record.SshTunnel?.User);
        var sshHost = Normalize(record.SshTunnel?.Host);

        if (distroName is not null)
        {
            return new GatewayHostAccessPlan(
                record.Id,
                GatewayTerminalTarget.Wsl,
                distroName,
                sshUser,
                sshHost,
                true,
                GatewayControlKind.Wsl,
                GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
                GatewayHostAccessLocalization.Format("GatewayHostAccess_OpenTerminalInWslTooltip_Format", new object?[] { distroName }),
                null);
        }

        if (sshUser is not null && sshHost is not null)
        {
            return new GatewayHostAccessPlan(
                record.Id,
                GatewayTerminalTarget.Ssh,
                null,
                sshUser,
                sshHost,
                false,
                GatewayControlKind.None,
                GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenSshTerminalLabel"),
                GatewayHostAccessLocalization.Format("GatewayHostAccess_OpenSshTerminalTooltip_Format", new object?[] { sshUser, sshHost }),
                null);
        }

        return GatewayHostAccessPlan.None(
            record.Id,
            GatewayHostAccessLocalization.GetString("GatewayHostAccess_NoWslOrSshDisabled"));
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}