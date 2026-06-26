using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Direct read / merge-patch / write of the gateway's <c>openclaw.json</c>, bypassing the
/// <c>config.patch</c> RPC. The gateway file-watches openclaw.json (chokidar,
/// <c>awaitWriteFinish</c> 200ms) and hot-reloads affected subsystems asynchronously
/// (<c>gateway.reload.mode=hot</c>), so a direct write returns as soon as the file is on
/// disk — no synchronous ~4.5s reload wait the way <c>config.patch</c> imposes. Verified
/// empirically and in openclaw dist <c>server-reload-handlers-d99vbE44.js</c>.
///
/// Rules (must respect):
/// <list type="bullet">
/// <item>Write VALID config only — unknown top-level keys are schema-rejected and the
/// reload is skipped ("config reload skipped (invalid config)").</item>
/// <item>Atomic temp + <see cref="File.Move(string,string,bool)"/> — the gateway also
/// writes this file, so a plain overwrite can race/corrupt.</item>
/// <item>Read-merge-write from the latest content (don't clobber concurrent gateway writes).
/// RFC 7396 semantics: objects recurse, scalars/arrays replace, <c>null</c> deletes a key.</item>
/// </list>
/// </summary>
internal static class OpenClawConfigFile
{
    /// <summary>openclaw.json path (native install default). Non-default config locations not yet supported.</summary>
    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".openclaw", "openclaw.json");

    private const int LockRetries = 6;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// RFC 7396 merge-patch openclaw.json and return immediately — the gateway's file
    /// watcher picks up the change (~1s) and hot-reloads in the background. Retries on
    /// transient <see cref="IOException"/> (file locked by a concurrent gateway write).
    /// </summary>
    public static async Task MergePatchAsync(JsonNode patch, CancellationToken ct = default)
    {
        if (patch is not JsonObject patchObj)
            throw new ArgumentException("patch must be a JSON object", nameof(patch));

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var root = await ReadRootAsync(ct);
                MergeInto(root, patchObj);
                await WriteAtomicAsync(root, ct);
                return;
            }
            catch (IOException) when (attempt < LockRetries)
            {
                await Task.Delay(LockRetryDelay * (attempt + 1), ct);
            }
        }
    }

    /// <summary>Read openclaw.json as a JsonObject. Empty object if missing; throws on corrupt JSON.</summary>
    public static async Task<JsonObject> ReadRootAsync(CancellationToken ct = default)
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new JsonObject();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return (await JsonSerializer.DeserializeAsync<JsonObject>(fs, cancellationToken: ct))
            ?? new JsonObject();
    }

    private static async Task WriteAtomicAsync(JsonObject root, CancellationToken ct)
    {
        var path = ConfigPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, root, new JsonSerializerOptions { WriteIndented = true }, ct);
            }
            // Atomic on NTFS same-volume; overwrites the existing openclaw.json in one step
            // so the gateway's file watcher observes a single clean change.
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>RFC 7396 merge: recurse into matching objects; null deletes; everything else replaces.</summary>
    private static void MergeInto(JsonObject target, JsonObject patch)
    {
        foreach (var kv in patch)
        {
            var key = kv.Key;
            var value = kv.Value;

            if (value is null)
            {
                target.Remove(key);
                continue;
            }

            if (value is JsonObject patchObj
                && target.TryGetPropertyValue(key, out var existing)
                && existing is JsonObject targetObj)
            {
                MergeInto(targetObj, patchObj);
                continue;
            }

            target[key] = value.DeepClone();
        }
    }
}
