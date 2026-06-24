using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Installs a corporate skill from the Company Skills Hub into the gateway:
/// downloads the zip, streams it via <c>skills.upload.begin/chunk/commit</c>,
/// then <c>skills.install { source: "upload" }</c>. The gateway extracts
/// server-side — no local filesystem access to the skills dir required
/// (works against a WSL/remote gateway). Ported from XClaw's
/// <c>electron/api/routes/skills.ts</c> company-install flow.
/// </summary>
internal sealed class CompanySkillInstaller
{
    /// <summary>Chunk size for <c>skills.upload.chunk</c> (base64-encoded).</summary>
    private const int ChunkSize = 512 * 1024;

    private readonly CompanySkillsHubClient _hub;
    private readonly Func<IOperatorGatewayClient?> _clientAccessor;

    internal CompanySkillInstaller(CompanySkillsHubClient hub, Func<IOperatorGatewayClient?> clientAccessor)
    {
        _hub = hub;
        _clientAccessor = clientAccessor;
    }

    /// <summary>
    /// Downloads the skill from the hub and installs it on the connected gateway.
    /// </summary>
    public async Task InstallFromHubAsync(int skillId, string slug, IProgress<(int done, int total)>? progress = null, CancellationToken ct = default)
    {
        var client = _clientAccessor()
            ?? throw new InvalidOperationException("未连接到网关，请先在「Connection」页连接网关。");

        // Best-effort: enable uploaded-archive installs once via config.patch (config.set
        // {path,value} 在此网关被拒，必须用合并补丁 {raw,baseHash})。幂等；失败吞掉。
        try
        {
            var resp = await client.SendWizardRequestAsync("config.get");
            string? baseHash = null;
            if (resp.TryGetProperty("baseHash", out var bh) && bh.ValueKind == JsonValueKind.String) baseHash = bh.GetString();
            else if (resp.TryGetProperty("hash", out var hsh) && hsh.ValueKind == JsonValueKind.String) baseHash = hsh.GetString();

            var patch = new JsonObject
            {
                ["skills"] = new JsonObject
                {
                    ["install"] = new JsonObject { ["allowUploadedArchives"] = true }
                }
            };
            object payload = baseHash != null
                ? (object)new { raw = patch.ToJsonString(), baseHash }
                : new { raw = patch.ToJsonString() };
            await client.SendWizardRequestAsync("config.patch", payload);
        }
        catch (Exception ex) { Logger.Warn($"[CompanySkillInstaller] allowUploadedArchives set failed (continuing): {ex.Message}"); }

        var zip = await _hub.DownloadZipAsync(skillId, ct);

        // begin
        var beginResp = await client.SendWizardRequestAsync(
            "skills.upload.begin",
            new { kind = "skill-archive", slug, sizeBytes = zip.Length });
        var uploadId = beginResp.TryGetProperty("uploadId", out var uidEl) ? uidEl.GetString() : null;
        if (string.IsNullOrEmpty(uploadId))
            throw new Exception("网关未返回 uploadId，skills.upload.begin 失败。");

        // chunked upload
        var totalChunks = (zip.Length + ChunkSize - 1) / ChunkSize;
        var done = 0;
        for (var offset = 0; offset < zip.Length; offset += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var len = Math.Min(ChunkSize, zip.Length - offset);
            var chunk = new byte[len];
            Array.Copy(zip, offset, chunk, 0, len);
            var dataBase64 = Convert.ToBase64String(chunk);

            await client.SendWizardRequestAsync(
                "skills.upload.chunk",
                new { uploadId, offset, dataBase64 },
                timeoutMs: 60_000);

            done++;
            progress?.Report((done, totalChunks));
        }

        // commit
        await client.SendWizardRequestAsync("skills.upload.commit", new { uploadId });

        // install (gateway extracts server-side)
        await client.SendWizardRequestAsync(
            "skills.install",
            new { source = "upload", uploadId, slug });
    }
}
