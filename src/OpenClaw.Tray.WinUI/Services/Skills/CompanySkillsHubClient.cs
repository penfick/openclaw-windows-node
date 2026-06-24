using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

// ── 模型 ─────────────────────────────────────────────────────────────

public sealed record CompanySkillItem(
    int Id,
    string Slug,
    string Name,
    string? Description,
    string? Version,
    int? CategoryId,
    string? AuthorId,
    string? AuthorName,
    int? DeptId,
    string? DeptName,
    int DownloadCount,
    int Status,
    string? CreatedAt,
    string? UpdatedAt);

public sealed record CompanySkillSearchResult(
    bool Success,
    IReadOnlyList<CompanySkillItem> Data,
    int Total,
    int Page,
    int PageSize);

public sealed record CompanySkillUploadMeta(
    string Slug,
    string Name,
    string? Description,
    string? Version,
    int? CategoryId,
    string? AuthorId,
    string? AuthorName,
    int? DeptId,
    string? DeptName);

// ── 客户端 ───────────────────────────────────────────────────────────

/// <summary>
/// HTTP client for the corporate Company Skills Hub. Ported from XClaw's
/// <c>electron/gateway/company-skills-api.ts</c>. Every request carries the OA
/// access token as a Bearer header (obtained via <see cref="OAuthAuthService"/>,
/// auto-refreshed). Backend URL is <see cref="SettingsData.CompanySkillsHubUrl"/>.
/// </summary>
internal sealed class CompanySkillsHubClient : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly OAuthAuthService _oauth;
    private readonly HttpClient _http;
    private bool _disposed;

    internal CompanySkillsHubClient(SettingsManager settings, OAuthAuthService oauth)
    {
        _settings = settings;
        _oauth = oauth;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>Search the corporate skill catalog.</summary>
    public async Task<CompanySkillSearchResult> SearchAsync(
        string? keyword, int? dept = null, int? category = null,
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(keyword)) qs.Add($"keyword={Uri.EscapeDataString(keyword)}");
        if (dept.HasValue) qs.Add($"dept={dept.Value}");
        if (category.HasValue) qs.Add($"category={category.Value}");
        qs.Add($"page={page}");
        qs.Add($"pageSize={pageSize}");
        var url = $"{_settings.CompanySkillsHubUrl.TrimEnd('/')}/api/skills?{string.Join('&', qs)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        await AddAuthHeaderAsync(req, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"搜索失败 ({(int)resp.StatusCode})");

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseSearchResult(json);
    }

    /// <summary>Download a skill package as a zip byte array.</summary>
    public async Task<byte[]> DownloadZipAsync(int skillId, CancellationToken ct = default)
    {
        var url = $"{_settings.CompanySkillsHubUrl.TrimEnd('/')}/api/skills/{skillId}/download";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        await AddAuthHeaderAsync(req, ct);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"下载失败 ({(int)resp.StatusCode})");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Upload a local skill (zip) to the corporate hub. Returns the new skill id.</summary>
    public async Task<int> UploadAsync(byte[] zipBytes, CompanySkillUploadMeta meta, CancellationToken ct = default)
    {
        var url = $"{_settings.CompanySkillsHubUrl.TrimEnd('/')}/api/skills";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        await AddAuthHeaderAsync(req, ct);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(zipBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "file", $"{meta.Slug}.zip");
        form.Add(new StringContent(meta.Slug), "slug");
        form.Add(new StringContent(meta.Name), "name");
        if (!string.IsNullOrEmpty(meta.Description)) form.Add(new StringContent(meta.Description), "description");
        if (!string.IsNullOrEmpty(meta.Version)) form.Add(new StringContent(meta.Version), "version");
        if (meta.CategoryId.HasValue) form.Add(new StringContent(meta.CategoryId.Value.ToString()), "categoryId");
        if (!string.IsNullOrEmpty(meta.AuthorId)) form.Add(new StringContent(meta.AuthorId), "authorId");
        if (!string.IsNullOrEmpty(meta.AuthorName)) form.Add(new StringContent(meta.AuthorName), "authorName");
        if (meta.DeptId.HasValue) form.Add(new StringContent(meta.DeptId.Value.ToString()), "deptId");
        if (!string.IsNullOrEmpty(meta.DeptName)) form.Add(new StringContent(meta.DeptName), "deptName");
        req.Content = form;

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"上传失败 ({(int)resp.StatusCode}): {text}");
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
    }

    private async Task AddAuthHeaderAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var token = await _oauth.GetValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("未登录 OA 账号，请先在「OA账号」页登录。");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static CompanySkillSearchResult ParseSearchResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var success = root.TryGetProperty("success", out var sEl) && sEl.GetBoolean();
        var total = root.TryGetProperty("total", out var tEl) ? tEl.GetInt32() : 0;
        var page = root.TryGetProperty("page", out var pEl) ? pEl.GetInt32() : 1;
        var pageSize = root.TryGetProperty("pageSize", out var psEl) ? psEl.GetInt32() : 20;

        var items = new List<CompanySkillItem>();
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in dataEl.EnumerateArray())
                items.Add(ParseItem(el));
        }
        return new CompanySkillSearchResult(success, items, total, page, pageSize);
    }

    private static CompanySkillItem ParseItem(JsonElement el)
    {
        int? N(string name) => el.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;
        string? S(string name) => el.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return new CompanySkillItem(
            Id: N("id") ?? 0,
            Slug: S("slug") ?? "",
            Name: S("name") ?? "",
            Description: S("description"),
            Version: S("version"),
            CategoryId: N("categoryId"),
            AuthorId: S("authorId"),
            AuthorName: S("authorName"),
            DeptId: N("deptId"),
            DeptName: S("deptName"),
            DownloadCount: N("downloadCount") ?? 0,
            Status: N("status") ?? 0,
            CreatedAt: S("createdAt"),
            UpdatedAt: S("updatedAt"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
