using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>技能详情弹框。
/// - ClawHub 模式（公共市场）：传 slug，调网关 skills.detail 拉元数据。
/// - 本地模式（已安装 / 公司市场）：传 name/description/meta，直接展示，不调 RPC。</summary>
public sealed partial class SkillDetailDialog : ContentDialog
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private readonly string? _slug;
    private readonly string? _localName, _localDesc, _localMeta;

    /// <summary>ClawHub 模式：按 slug 拉取公共市场元数据。</summary>
    public SkillDetailDialog(string slug)
    {
        InitializeComponent();
        _slug = slug;
        _ = LoadAsync();
    }

    /// <summary>本地模式：已安装 / 公司市场技能，直接用已有字段展示。</summary>
    public SkillDetailDialog(string name, string description, string metaLine)
    {
        InitializeComponent();
        _slug = null;
        FillLocal(name, description, metaLine);
    }

    /// <summary>已安装技能：先试 skills.detail(slug) 拿富信息（作者/版本/完整说明），
    /// 失败（非 ClawHub 技能，如 workspace/公司市场装的）回退到本地字段。</summary>
    public SkillDetailDialog(string slug, string localName, string localDescription, string localMeta)
    {
        InitializeComponent();
        _slug = slug;
        _localName = localName;
        _localDesc = localDescription;
        _localMeta = localMeta;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { ShowError("未连接到网关。"); return; }

        try
        {
            var resp = await client.SendWizardRequestAsync("skills.detail", new { slug = _slug });

            string S(JsonElement el, string n) => el.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

            var skill = resp.TryGetProperty("skill", out var sk) ? sk : resp;
            var name = S(skill, "displayName"); if (string.IsNullOrEmpty(name)) name = S(skill, "slug");
            var summary = S(skill, "summary");
            var description = S(skill, "description");

            string owner = "";
            if (resp.TryGetProperty("owner", out var ow))
                owner = !string.IsNullOrEmpty(S(ow, "displayName")) ? S(ow, "displayName") : S(ow, "handle");

            string version = "", license = "";
            if (resp.TryGetProperty("latestVersion", out var lv))
            {
                version = S(lv, "version");
                license = S(lv, "license");
            }

            var metaParts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(owner)) metaParts.Add($"作者 {owner}");
            if (!string.IsNullOrEmpty(version)) metaParts.Add($"v{version}");
            if (!string.IsNullOrEmpty(license)) metaParts.Add(license);

            // summary 并到 description 前面（公共市场的 summary 通常是短摘要）
            var fullDesc = string.IsNullOrEmpty(description) ? summary
                : (string.IsNullOrEmpty(summary) ? description : summary + "\n\n" + description);

            FillLocal(name, fullDesc, string.Join("   ·   ", metaParts));

            // tags
            if (skill.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                var tagList = new System.Collections.Generic.List<string>();
                foreach (var t in tags.EnumerateArray())
                {
                    var tv = t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                    if (!string.IsNullOrEmpty(tv)) tagList.Add("#" + tv);
                }
                var tagsText = string.Join("  ", tagList);
                TagsTb.Text = tagsText;
                TagsTb.Visibility = string.IsNullOrEmpty(tagsText) ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        catch (System.Exception ex)
        {
            // 回退到本地字段（已安装的非 ClawHub 技能）
            if (_localName != null)
                FillLocal(_localName, _localDesc ?? "", _localMeta ?? "");
            else
                ShowError(ex.Message);
        }
    }

    private void FillLocal(string name, string description, string metaLine)
    {
        Title = string.IsNullOrEmpty(name) ? "技能详情" : name;
        NameTb.Text = name;
        NameTb.Visibility = string.IsNullOrEmpty(name) ? Visibility.Collapsed : Visibility.Visible;

        MetaTb.Text = metaLine ?? "";
        MetaTb.Visibility = string.IsNullOrEmpty(metaLine) ? Visibility.Collapsed : Visibility.Visible;

        if (string.IsNullOrEmpty(description))
        {
            DescHeader.Visibility = Visibility.Collapsed;
            DescTb.Visibility = Visibility.Collapsed;
        }
        else
        {
            DescTb.Text = description;
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;
    }

    private void ShowError(string msg)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Collapsed;
        ErrorTb.Text = msg;
        ErrorTb.Visibility = Visibility.Visible;
    }
}
