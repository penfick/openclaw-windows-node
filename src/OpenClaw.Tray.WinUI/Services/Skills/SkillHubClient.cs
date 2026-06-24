using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

public sealed record PublicMarketSkill(
    string Slug,
    string Name,
    string Summary,
    string Version,
    string Author,
    long Downloads);

/// <summary>
/// 公共 Skills 市场（ClawHub）搜索，走网关 <c>skills.search</c> RPC（operator.read）。
/// 返回 <c>{results:[{slug, displayName, summary, version, downloads, owner, ...}]}</c>。
///
/// 为什么用 ClawHub（网关 skills.search）而不是 skills.sh 的 /api/search：
/// skills.sh /api/search 索引更宽（能搜到 vision-support 等），但那些技能不在 ClawHub
/// 注册库里，<c>skills.install { source:"clawhub", slug }</c> 会 404（搜得到装不了）。
/// ClawHub 搜索保证「搜得到就能装」，且 slug 唯一（已安装检测精确，不会同名全标）。
/// 代价：skills.sh 独有、未同步到 ClawHub 的技能搜不到。
///
/// 注意：ClawHub 搜索必须带关键词（空 query 报错），浏览用默认宽泛词（如 "tool"）。
/// </summary>
internal static class SkillHubClient
{
    /// <summary>按关键词搜索 ClawHub 技能。keyword 为空时用默认浏览词。</summary>
    public static async Task<List<PublicMarketSkill>> SearchAsync(
        IOperatorGatewayClient client, string keyword, int limit = 40, CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(keyword) ? "tool" : keyword.Trim();
        var resp = await client.SendWizardRequestAsync("skills.search", new { query, limit });

        JsonElement arr = default;
        bool has = false;
        if (resp.ValueKind == JsonValueKind.Array) { arr = resp; has = true; }
        else if (resp.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array) { arr = r; has = true; }
        else if (resp.TryGetProperty("skills", out var s) && s.ValueKind == JsonValueKind.Array) { arr = s; has = true; }

        var items = new List<PublicMarketSkill>();
        if (has)
            foreach (var el in arr.EnumerateArray())
                items.Add(Parse(el));
        return items;
    }

    private static PublicMarketSkill Parse(JsonElement el)
    {
        string S(string n) => el.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        long N(string n) => el.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;

        var slug = S("slug");
        var display = S("displayName");
        var owner = S("owner");
        return new PublicMarketSkill(
            Slug: slug,
            Name: string.IsNullOrEmpty(display) ? slug : display,
            Summary: S("summary"),
            Version: S("version"),
            Author: string.IsNullOrEmpty(owner) ? S("ownerHandle") : owner,
            Downloads: N("downloads"));
    }
}
