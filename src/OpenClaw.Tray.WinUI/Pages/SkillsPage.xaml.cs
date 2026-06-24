using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class SkillsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;
    private List<SkillData> _allSkills = new();

    public string? CurrentAgentId => GetSelectedAgentId();

    public SkillsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        _appState = CurrentApp.AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        PopulateAgentFilter();

        // Show cached data immediately if available
        if (_allSkills.Count == 0 && _appState.SkillsData.HasValue)
        {
            UpdateFromGateway(_appState.SkillsData.Value);
        }
        else if (_allSkills.Count > 0)
        {
            RebuildCards();
        }
        else
        {
            // No cached data — show loading spinner
            LoadingState.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }

        if (CurrentApp.GatewayClient != null)
        {
            _ = CurrentApp.GatewayClient.RequestSkillsStatusAsync(GetSelectedAgentId());
        }

        // 公司市场 tab：首次导航 CompanySkillsPage（在 Frame 中复用）
        if (CompanyFrame.Content == null)
        {
            CompanyFrame.Navigate(typeof(CompanySkillsPage));
        }
    }

    // ── 公共市场（skills.sh，走网关 skills.search：关键词搜索 + 客户端分页 + 已安装检查）──

    private const int PublicPageSize = 12;       // 客户端每页卡片数
    private const string PublicDefaultKeyword = "tool";
    private List<PublicMarketSkill> _publicAll = new();
    private int _publicPage = 1;
    private string _publicKeyword = PublicDefaultKeyword;
    private bool _publicLoaded;
    // 本次会话通过公共市场装过的 slug（用于把卡片标成「已安装」）。
    // 不用 _allSkills 全量 slug 匹配——同名技能（不同 owner）会全部误标，而
    // bundled 技能（如 1password）本就不是从 skills.sh 市场来的，不该标。
    private readonly HashSet<string> _installedThisSession = new(StringComparer.OrdinalIgnoreCase);
    private int PublicPageCount => (_publicAll.Count + PublicPageSize - 1) / PublicPageSize;

    private void OnPivotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 首次切到「公共市场」时用默认词搜索（skills.sh 不支持无关键词浏览）
        if (SkillsPivot.SelectedItem is PivotItem pi && pi.Header?.ToString() == "公共市场" && !_publicLoaded)
            _ = SearchPublicAsync(_publicKeyword);
    }

    private void OnPublicSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter) DoPublicSearch();
    }

    private void OnPublicSearchClick(object sender, RoutedEventArgs e) => DoPublicSearch();

    private void DoPublicSearch()
    {
        var kw = PublicSearchBox.Text.Trim();
        _publicKeyword = string.IsNullOrEmpty(kw) ? PublicDefaultKeyword : kw;
        _ = SearchPublicAsync(_publicKeyword);
    }

    private void OnPublicPrevClick(object sender, RoutedEventArgs e)
    {
        if (_publicPage > 1) RenderPublicPage(_publicPage - 1);
    }

    private void OnPublicNextClick(object sender, RoutedEventArgs e)
    {
        if (_publicPage < PublicPageCount) RenderPublicPage(_publicPage + 1);
    }

    /// <summary>调网关 skills.search（ClawHub）拉取结果，存入 _publicAll。</summary>
    private async Task SearchPublicAsync(string keyword)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { PublicStatusText.Text = "未连接到网关。"; return; }

        PublicProgress.IsActive = true;
        PublicPrevBtn.IsEnabled = false;
        PublicNextBtn.IsEnabled = false;
        try
        {
            _publicAll = await SkillHubClient.SearchAsync(client, keyword);
            _publicKeyword = keyword;
            _publicLoaded = true;
            RenderPublicPage(1);
        }
        catch (Exception ex)
        {
            PublicStatusText.Text = $"搜索失败：{ex.Message}";
            PublicGrid.ItemsSource = null;
        }
        finally
        {
            PublicProgress.IsActive = false;
        }
    }

    /// <summary>已安装 slug 集合：_allSkills（skills.status 的 skillKey）+ _installedThisSession。
    /// 注：skillKey 就是 slug，但不同来源/owner 可能共享 slug（workspace 的 pptx-generator vs
    /// ClawHub 的 pptx-generator），无法从 source 区分（ClawHub 装的也落 workspace），所以个别同名
    /// 技能可能误标——这是数据层面的限制，靠 slug 匹配已是最优。</summary>
    private HashSet<string> GetInstalledSlugs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _allSkills)
            if (!string.IsNullOrEmpty(s.SkillKey)) set.Add(s.SkillKey);
        foreach (var slug in _installedThisSession) set.Add(slug);
        return set;
    }

    /// <summary>客户端分页：从 _publicAll 切出第 page 页渲染。</summary>
    private void RenderPublicPage(int page)
    {
        _publicPage = page;
        var installed = GetInstalledSlugs();

        var pageItems = _publicAll
            .Skip((page - 1) * PublicPageSize)
            .Take(PublicPageSize)
            .Select(s => new PublicSkillCard(s, installed.Contains(s.Slug)))
            .ToList();
        PublicGrid.ItemsSource = pageItems;

        var totalPages = PublicPageCount;
        var isDefault = string.IsNullOrEmpty(_publicKeyword) || _publicKeyword == PublicDefaultKeyword;
        var kwTxt = isDefault ? "热门技能" : $"「{_publicKeyword}」";
        PublicStatusText.Text = _publicAll.Count > 0
            ? $"{kwTxt} 共 {_publicAll.Count} 个 · 第 {page}/{totalPages} 页"
            : $"未找到匹配 {kwTxt} 的技能，换个关键词试试。";

        PublicPageText.Text = totalPages > 1 ? $"第 {page} / {totalPages} 页" : "";
        PublicPrevBtn.IsEnabled = page > 1;
        PublicNextBtn.IsEnabled = page < totalPages;
    }

    private async void OnPublicDetailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag?.ToString() is not { } slug) return;
        await new SkillDetailDialog(slug) { XamlRoot = this.XamlRoot }.ShowAsync();
    }

    private async void OnPublicInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag?.ToString() is not { } slug) return;
        var client = CurrentApp.GatewayClient;
        if (client == null) { PublicStatusText.Text = "未连接到网关。"; return; }

        PublicStatusText.Text = $"正在安装 {slug}…";
        btn.IsEnabled = false;
        try
        {
            // skills.install ClawHub 模式：{ source:"clawhub", slug }。SendWizardRequestAsync
            // 在网关返回 ok:false 时抛异常，所以能拿到真实失败原因（之前 InstallSkillAsync
            // 是 fire-and-forget，发出去就当成功，实际没装）。
            await client.SendWizardRequestAsync("skills.install",
                new { source = "clawhub", slug }, timeoutMs: 60_000);

            _installedThisSession.Add(slug);
            RenderPublicPage(_publicPage);                       // 当前页这张标成「已安装」
            _ = client.RequestSkillsStatusAsync(GetSelectedAgentId()); // 已安装 tab 刷新
            PublicStatusText.Text = $"已安装：{slug}";
        }
        catch (Exception ex)
        {
            PublicStatusText.Text = $"安装失败 [{slug}]：{ex.Message}";
            btn.IsEnabled = true;
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.SkillsData):
                if (_appState!.SkillsData.HasValue) UpdateFromGateway(_appState.SkillsData.Value);
                break;
        }
    }

    private void PopulateAgentFilter()
    {
        AgentFilterCombo.SelectionChanged -= OnAgentFilterChanged;
        AgentFilterCombo.Items.Clear();
        AgentFilterCombo.Items.Add(new ComboBoxItem { Content = "All Agents", Tag = "" });
        foreach (var id in CurrentApp.AppState?.GetAgentIds() ?? new List<string> { "main" })
            AgentFilterCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
        AgentFilterCombo.SelectedIndex = 0;
        AgentFilterCombo.SelectionChanged += OnAgentFilterChanged;
    }

    private string? GetSelectedAgentId()
    {
        if (AgentFilterCombo.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag as string;
            return string.IsNullOrEmpty(tag) ? null : tag;
        }
        return null;
    }

    private void OnAgentFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client != null)
            _ = client.RequestSkillsStatusAsync(GetSelectedAgentId());
    }

    private void OnToggleSkillClick(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnToggleSkillClickAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnToggleSkillClick));

    private async Task OnToggleSkillClickAsync(object sender)
    {
        if (sender is not Button btn || btn.Tag is not string skillKey) return;
        if (CurrentApp.GatewayClient == null) return;

        var skill = _allSkills.FirstOrDefault(s => s.SkillKey == skillKey);
        if (skill == null) return;

        bool newState = !skill.IsEnabled;
        btn.IsEnabled = false;
        var success = await CurrentApp.GatewayClient.SetSkillEnabledAsync(skillKey, newState);
        btn.IsEnabled = true;

        if (success)
        {
            // Re-lookup after await — _allSkills may have been replaced by UpdateFromGateway
            var current = _allSkills.FirstOrDefault(s => s.SkillKey == skillKey);
            if (current != null)
            {
                current.IsEnabled = newState;
                RebuildCards();
            }
        }
    }

    public void UpdateFromGateway(JsonElement data)
    {
        OpenClawTray.Services.Logger.Info("[SkillsPage] Received gateway skills data");

        JsonElement skillsArray;
        if (data.TryGetProperty("skills", out var inner))
            skillsArray = inner;
        else if (data.TryGetProperty("payload", out var payload))
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("skills", out var nested))
                skillsArray = nested;
            else if (payload.ValueKind == JsonValueKind.Array)
                skillsArray = payload;
            else
                return;
        }
        else if (data.ValueKind == JsonValueKind.Array)
            skillsArray = data;
        else
            return;

        var skills = new List<SkillData>();

        foreach (var item in skillsArray.EnumerateArray())
        {
            var s = new SkillData();

            if (item.TryGetProperty("name", out var nameEl))
            {
                s.Name = nameEl.GetString() ?? "";
                s.Id = s.Name;
            }
            if (item.TryGetProperty("id", out var idEl))
                s.Id = idEl.GetString() ?? s.Id;
            if (item.TryGetProperty("emoji", out var emojiEl))
            {
                var emoji = emojiEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(emoji))
                    s.Emoji = emoji;
            }
            if (item.TryGetProperty("description", out var descEl))
                s.Description = descEl.GetString() ?? "";
            if (item.TryGetProperty("source", out var srcEl))
                s.Source = srcEl.GetString() ?? "";
            if (item.TryGetProperty("baseDir", out var baseDirEl))
                s.BaseDir = baseDirEl.GetString() ?? "";
            if (item.TryGetProperty("homepage", out var homeEl))
                s.Homepage = homeEl.GetString() ?? "";
            if (item.TryGetProperty("bundled", out var bundledEl) && bundledEl.ValueKind == JsonValueKind.True)
                s.Bundled = true;
            if (item.TryGetProperty("skillKey", out var keyEl))
                s.SkillKey = keyEl.GetString() ?? s.Id;
            else
                s.SkillKey = s.Id;

            if (item.TryGetProperty("disabled", out var disabledEl))
                s.IsEnabled = disabledEl.ValueKind != JsonValueKind.True;
            else if (item.TryGetProperty("enabled", out var enabledEl))
                s.IsEnabled = enabledEl.ValueKind == JsonValueKind.True;
            else
                s.IsEnabled = item.TryGetProperty("eligible", out var eligibleEl) && eligibleEl.ValueKind == JsonValueKind.True;

            skills.Add(s);
        }

        // Sort alphabetically
        skills.Sort((a, b) => string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));

        _allSkills = skills;
        RebuildCards();
    }

    private void RebuildCards()
    {
        LoadingState.Visibility = Visibility.Collapsed;
        var enabled = _allSkills.Where(s => s.IsEnabled).ToList();
        var disabled = _allSkills.Where(s => !s.IsEnabled).ToList();

        EnabledPanel.Items.Clear();
        DisabledPanel.Items.Clear();

        foreach (var s in enabled)
            EnabledPanel.Items.Add(BuildCard(s));
        foreach (var s in disabled)
            DisabledPanel.Items.Add(BuildCard(s));

        EnabledHeaderText.Text = LocalizationHelper.Format("SkillsPage_EnabledHeaderFormat", enabled.Count);
        DisabledHeaderText.Text = LocalizationHelper.Format("SkillsPage_DisabledHeaderFormat", disabled.Count);
        DisabledExpander.Visibility = disabled.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = _allSkills.Count;
        CountText.Text = total > 0 ? LocalizationHelper.Format("SkillsPage_CountFormat", enabled.Count, total) : "";

        if (total > 0)
        {
            SkillsGroups.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            SkillsGroups.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    private Border BuildCard(SkillData s)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var root = new StackPanel { Spacing = 6 };
        double contentOpacity = s.IsEnabled ? 1.0 : 0.55;

        // \u2500\u2500 header: emoji + name + badge \u2500\u2500
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (!string.IsNullOrEmpty(s.Emoji))
            header.Children.Add(new TextBlock { Text = s.Emoji, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Opacity = contentOpacity });
        header.Children.Add(new TextBlock
        {
            Text = s.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = contentOpacity
        });

        var badgeBgKey = s.IsEnabled ? "SystemFillColorSuccessBackgroundBrush" : "ControlFillColorSecondaryBrush";
        var badgeFgKey = s.IsEnabled ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush";
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            MinHeight = 20,
            Padding = new Thickness(8, 2, 8, 2),
            Background = (Brush)Application.Current.Resources[badgeBgKey],
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock
        {
            Text = LocalizationHelper.GetString(s.IsEnabled ? "SkillsPage_BadgeEnabled" : "SkillsPage_BadgeDisabled"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources[badgeFgKey],
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(badge);
        root.Children.Add(header);

        // \u2500\u2500 description\uff08\u9650\u5236 2 \u884c + \u7701\u7565\u53f7\uff0c\u907f\u514d\u957f\u63cf\u8ff0\u628a\u5e95\u90e8\u6309\u94ae\u6324\u51fa\u53ef\u89c6\u533a\uff09\u2500\u2500
        if (!string.IsNullOrEmpty(s.Description))
        {
            root.Children.Add(new TextBlock
            {
                Text = s.Description, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Opacity = contentOpacity
            });
        }

        // \u2500\u2500 footer: source + upload + toggle \u2500\u2500
        var footer = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var srcText = s.Bundled ? "\u5185\u7F6E\u6280\u80FD" : (string.IsNullOrEmpty(s.Source) ? "" : s.Source);
        footer.Children.Add(new TextBlock
        {
            Text = srcText, FontSize = 10, FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        });

        // \u4E0A\u4F20\u5230\u516C\u53F8\u5E02\u573A
        var uploadBtn = new Button { Tag = s.SkillKey, Padding = new Thickness(8, 4, 8, 4), MinWidth = 0, MinHeight = 0 };
        uploadBtn.Content = new TextBlock { Text = "\u4E0A\u4F20", FontSize = 11 };
        ToolTipService.SetToolTip(uploadBtn, "\u4E0A\u4F20\u5230\u516C\u53F8\u5E02\u573A");
        uploadBtn.Click += OnUploadClick;
        Grid.SetColumn(uploadBtn, 2);
        footer.Children.Add(uploadBtn);

        // \u8BE6\u60C5\u6309\u94AE\uFF08col 1\uFF09
        var detailBtn = new Button { Tag = s.SkillKey, Padding = new Thickness(8, 4, 8, 4), MinWidth = 0, MinHeight = 0 };
        detailBtn.Content = new TextBlock { Text = "\u8BE6\u60C5", FontSize = 11 };
        ToolTipService.SetToolTip(detailBtn, "\u67E5\u770B\u8BE6\u60C5");
        detailBtn.Click += OnInstalledDetailClick;
        Grid.SetColumn(detailBtn, 1);
        footer.Children.Add(detailBtn);

        // \u542F\u7528/\u7981\u7528
        var toggleBtn = new Button { Tag = s.SkillKey, Padding = new Thickness(8, 4, 8, 4), MinWidth = 0, MinHeight = 0 };
        ToolTipService.SetToolTip(toggleBtn, s.IsEnabled ? "\u7981\u7528" : "\u542F\u7528");
        toggleBtn.Content = new FontIcon { Glyph = s.IsEnabled ? "\uE769" : "\uE768", FontSize = 12 };
        toggleBtn.Click += OnToggleSkillClick;
        Grid.SetColumn(toggleBtn, 3);
        footer.Children.Add(toggleBtn);

        root.Children.Add(footer);
        card.Child = root;
        return card;
    }

    // \u2500\u2500 \u4E0A\u4F20\u5230\u516C\u53F8\u5E02\u573A \u2500\u2500

    private async void OnInstalledDetailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string skillKey) return;
        var skill = _allSkills.FirstOrDefault(s => s.SkillKey == skillKey);
        if (skill == null) return;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(skill.Source)) parts.Add($"来源 {skill.Source}");
        parts.Add(skill.Bundled ? "内置技能" : "工作区技能");
        if (!string.IsNullOrEmpty(skill.Homepage)) parts.Add(skill.Homepage);
        var localMeta = string.Join("   ·   ", parts);
        // 先试 skills.detail(skillKey) 拿作者/版本/完整说明；非 ClawHub 技能回退本地
        await new SkillDetailDialog(skill.SkillKey, skill.Name, skill.Description, localMeta) { XamlRoot = this.XamlRoot }.ShowAsync();
    }

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string skillKey) return;
        var skill = _allSkills.FirstOrDefault(s => s.SkillKey == skillKey);
        if (skill == null) return;

        if (string.IsNullOrEmpty(skill.BaseDir))
        {
            ShowInstalledStatus("\u8BE5\u6280\u80FD\u65E0\u672C\u5730\u76EE\u5F55\uFF0C\u65E0\u6CD5\u4E0A\u4F20\u3002");
            return;
        }

        // \u6253\u5305\u8D70 wsl.exe\uFF08\u4E0D\u4F9D\u8D56 UNC\uFF09\uFF0C\u5931\u8D25\u7531\u5F39\u6846\u5185\u7684 PackSkill \u629B\u51FA\u5E76\u663E\u793A\u3002
        var dialog = new UploadSkillDialog { XamlRoot = this.XamlRoot };
        dialog.Setup(skill.Name, skill.SkillKey, skill.Description, skill.BaseDir);
        await dialog.ShowAsync();
    }

    private void ShowInstalledStatus(string msg)
    {
        InstalledStatusText.Text = msg;
        InstalledStatusText.Visibility = Visibility.Visible;
    }

    private class SkillData
    {
        public string Id { get; set; } = "";
        public string SkillKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        public string Emoji { get; set; } = "";
        public string BaseDir { get; set; } = "";
        public string Homepage { get; set; } = "";
        public bool Bundled { get; set; }
        public bool IsEnabled { get; set; } = true;
    }
}

public sealed class PublicSkillCard
{
    private readonly PublicMarketSkill _item;
    public PublicSkillCard(PublicMarketSkill item, bool isInstalled) { _item = item; IsInstalled = isInstalled; }

    public string Slug => _item.Slug;
    public string Name => string.IsNullOrEmpty(_item.Name) ? _item.Slug : _item.Name;
    public string Description => _item.Summary;
    public string AuthorLine => string.IsNullOrEmpty(_item.Author) ? "" : $"by {_item.Author}";
    public string StatsLine
    {
        get
        {
            var parts = new List<string>();
            if (_item.Downloads > 0) parts.Add($"⬇ {_item.Downloads}");
            if (!string.IsNullOrEmpty(_item.Version)) parts.Add($"v{_item.Version}");
            return string.Join("   ", parts);
        }
    }

    public bool IsInstalled { get; }
    public string InstallLabel => IsInstalled ? "已安装" : "安装";
    public bool CanInstall => !IsInstalled;
}
