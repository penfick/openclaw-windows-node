namespace OpenClaw.Connection;

/// <summary>
/// How a setup-managed gateway is installed and run.
/// <list type="bullet">
/// <item><c>Wsl</c> — app-owned WSL distro (the historical default; runs the Linux openclaw gateway inside WSL).</item>
/// <item><c>Native</c> — native Windows process (openclaw.exe via install.ps1 + Scheduled Task; no WSL).</item>
/// </list>
/// Default is <c>Wsl</c> (= 0) so gateways.json records serialized before this field existed
/// deserialize as WSL-managed (they carry <c>SetupManagedDistroName</c>).
/// </summary>
public enum GatewayInstallKind
{
    Wsl = 0,
    Native = 1
}
