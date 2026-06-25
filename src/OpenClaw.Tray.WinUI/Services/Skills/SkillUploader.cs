using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>
/// 把已安装技能的目录打包成 zip 字节，用于上传到公司市场。
///
/// 按网关安装方式分流：
/// <list type="bullet">
/// <item><b>Native</b> 网关：技能目录是 Windows 路径（<c>%USERPROFILE%\.openclaw\skills\…</c>），
///   直接用 <see cref="ZipFile"/> 在进程内打包——不依赖 wsl.exe。</item>
/// <item><b>WSL</b> 网关：技能目录在 distro 内（<c>\\wsl.localhost\</c> UNC 不可用、<c>/mnt/c</c> 未挂载），
///   用 <c>wsl.exe</c> 在 WSL 侧 python3 现场打包 + base64 流回。</item>
/// </list>
/// </summary>
internal static class SkillUploader
{
    private const string DefaultDistro = "OpenClawGateway";

    /// <summary>读 SKILL.md 目录树、内存 zip、base64 输出到 stdout。argv[1]=baseDir。</summary>
    private const string ZipScript = @"import os, sys, zipfile, io, base64
d = sys.argv[1]
buf = io.BytesIO()
with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(d):
        for fn in files:
            fp = os.path.join(root, fn)
            z.write(fp, os.path.relpath(fp, d))
sys.stdout.write(base64.b64encode(buf.getvalue()).decode())
";

    /// <summary>从 setup-state.json 解析本地网关的 WSL distro 名。</summary>
    private static string ResolveDistro()
    {
        try
        {
            var dir = SetupExistingGatewayClassifier.ResolveLocalDataPath();
            var stateFile = Path.Combine(dir, "setup-state.json");
            if (File.Exists(stateFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(stateFile));
                if (doc.RootElement.TryGetProperty("DistroName", out var dn) &&
                    dn.GetString() is { Length: > 0 } name)
                    return name;
            }
        }
        catch { /* 解析失败回退默认 */ }
        return DefaultDistro;
    }

    /// <summary>打包技能目录为 zip 字节。Windows 路径（native）走 C# ZipFile；Linux 路径（WSL）走 wsl.exe。</summary>
    public static byte[] PackSkill(string gatewayBaseDir)
    {
        if (string.IsNullOrWhiteSpace(gatewayBaseDir))
            throw new InvalidOperationException("该技能无本地目录，无法上传。");

        if (IsWindowsPath(gatewayBaseDir))
            return PackNative(gatewayBaseDir);

        return PackViaWsl(gatewayBaseDir);
    }

    /// <summary>Windows 盘符路径（<c>X:\…</c>）或 UNC（<c>\\…</c>）视为 Windows 可直达。</summary>
    private static bool IsWindowsPath(string p) =>
        p.Length >= 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/')
        || p.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Native：进程内 ZipFile 打包（includeBaseDirectory:false → 条目相对 baseDir）。</summary>
    private static byte[] PackNative(string dir)
    {
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"技能目录不存在：{dir}");

        using var ms = new MemoryStream();
        ZipFile.CreateFromDirectory(dir, ms, CompressionLevel.Optimal, includeBaseDirectory: false);
        return ms.ToArray();
    }

    /// <summary>WSL：用 wsl.exe 在 WSL 侧打包 baseDir 为 zip，返回字节数组。</summary>
    private static byte[] PackViaWsl(string gatewayBaseDir)
    {
        var distro = ResolveDistro();
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro);
        psi.ArgumentList.Add("python3"); psi.ArgumentList.Add("-"); psi.ArgumentList.Add(gatewayBaseDir);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 wsl.exe。");
        p.StandardInput.Write(ZipScript);
        p.StandardInput.Close();

        var b64 = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch { }
            throw new InvalidOperationException("打包超时（wsl.exe 30s 未退出）。");
        }

        b64 = b64.Trim();
        // wsl.exe 会把 NAT 模式等 locale 警告打到 stderr（退出码仍为 0、stdout 是干净的 base64），
        // 不能把 stderr 非空当成失败。只有退出码非 0 或 stdout 无数据才算真失败。
        if (p.ExitCode != 0 || b64.Length == 0)
            throw new InvalidOperationException($"打包失败：{err.Trim()}");

        return Convert.FromBase64String(b64);
    }
}
