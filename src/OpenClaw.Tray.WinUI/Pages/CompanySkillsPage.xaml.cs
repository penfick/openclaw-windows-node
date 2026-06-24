using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// Corporate Skills marketplace. Browses the Company Skills Hub, installs via
/// gateway skills.upload/install, and shares local skills back. Text is
/// hardcoded zh-CN for now (enterprise-internal feature).
/// </summary>
public sealed partial class CompanySkillsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private OpenClawTray.Services.AppState? _appState;
    private bool _browsed;
    private readonly HashSet<int> _installedCompanyIds = new();
    private List<CompanySkillRow> _lastRows = new();
    // 已安装技能的 slug 集合（来自 skills.status），用于把公司市场里已装的标成「已安装」
    private HashSet<string> _installedSlugs = new(StringComparer.OrdinalIgnoreCase);

    public CompanySkillsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Initialize();
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appState != null)
        {
            _appState.PropertyChanged -= OnAppStateChanged;
            _appState = null;
        }
    }

    public void Initialize()
    {
        var appState = CurrentApp.AppState!;
        if (!ReferenceEquals(_appState, appState))
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            _appState = appState;
            _appState.PropertyChanged += OnAppStateChanged;
        }

        // 首次渲染前先用当前 skills.status 填充已装 slug（否则要等下一次变更才标记）
        if (_appState.SkillsData.HasValue)
            _installedSlugs = ExtractInstalledSlugs(_appState.SkillsData.Value);
        RefreshLoginGate();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenClawTray.Services.AppState.AuthState))
            RefreshLoginGate();
        else if (e.PropertyName == nameof(OpenClawTray.Services.AppState.SkillsData))
        {
            // skills.status 刷新 → 更新已装 slug 集合 → 公司市场卡片重新标记
            if (_appState?.SkillsData.HasValue == true)
            {
                _installedSlugs = ExtractInstalledSlugs(_appState.SkillsData.Value);
                if (_lastRows.Count > 0) RenderCompanyResults();
            }
        }
    }

    /// <summary>从 skills.status 载荷提取已安装技能的 skillKey（= slug）集合。</summary>
    private static HashSet<string> ExtractInstalledSlugs(JsonElement data)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (data.ValueKind != JsonValueKind.Object) return set;
        if (!data.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Array) return set;
        foreach (var s in skills.EnumerateArray())
        {
            if (s.TryGetProperty("skillKey", out var k) && k.ValueKind == JsonValueKind.String)
            {
                var key = k.GetString();
                if (!string.IsNullOrEmpty(key)) set.Add(key);
            }
        }
        return set;
    }

    private void RefreshLoginGate()
    {
        var loggedIn = CurrentApp.AppState?.AuthState?.Authenticated == true;
        LoginRequiredCard.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        MainContent.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        // 登录后自动浏览一次公司市场（不再空数据）
        if (loggedIn && !_browsed)
        {
            _browsed = true;
            _ = SearchAsync();
        }
    }

    private void OnKeywordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter) _ = SearchAsync();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        var hub = CurrentApp.CompanySkillsHub;
        if (hub == null) return;

        SearchButton.IsEnabled = false;
        SearchProgress.IsActive = true;
        StatusText.Text = string.Empty;
        try
        {
            var result = await hub.SearchAsync(KeywordBox.Text.Trim());
            _lastRows = result.Data.Select(i => new CompanySkillRow
            {
                Id = i.Id,
                Slug = i.Slug,
                Name = i.Name,
                Description = i.Description ?? "",
                VersionBadge = string.IsNullOrEmpty(i.Version) ? "" : $"v{i.Version}",
                MetaLine = BuildMetaLine(i),
            }).ToList();
            RenderCompanyResults();
            StatusText.Text = result.Total > 0 ? $"共 {result.Total} 个结果" : "无结果";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"搜索失败：{ex.Message}";
            ResultsList.ItemsSource = null;
        }
        finally
        {
            SearchButton.IsEnabled = true;
            SearchProgress.IsActive = false;
        }
    }

    private static string BuildMetaLine(CompanySkillItem i)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(i.AuthorName)) parts.Add(i.AuthorName);
        if (!string.IsNullOrEmpty(i.DeptName)) parts.Add(i.DeptName);
        if (i.DownloadCount > 0) parts.Add($"下载 {i.DownloadCount}");
        return string.Join(" · ", parts);
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var installer = CurrentApp.SkillInstaller;
        if (installer == null) return;
        if (sender is not Button btn || btn.DataContext is not CompanySkillRow row) return;

        btn.IsEnabled = false;
        StatusText.Text = $"正在下载 {row.Name}…";
        try
        {
            var progress = new Progress<(int done, int total)>(p =>
                StatusText.Text = $"正在上传 {row.Name}… {p.done}/{p.total} 块");
            await installer.InstallFromHubAsync(row.Id, row.Slug, progress);

            _installedCompanyIds.Add(row.Id);
            RenderCompanyResults();                                      // 公司市场卡片标「已安装」
            _ = CurrentApp.GatewayClient?.RequestSkillsStatusAsync();   // 已安装 tab 实时刷新
            StatusText.Text = $"已安装：{row.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"安装失败 [{row.Name}]：{ex.Message}";
            btn.IsEnabled = true;
        }
    }

    private void RenderCompanyResults()
    {
        foreach (var r in _lastRows)
            r.IsInstalled = _installedCompanyIds.Contains(r.Id) || _installedSlugs.Contains(r.Slug);
        // CompanySkillRow 无 INPC，重设 ItemsSource 强制刷新
        ResultsList.ItemsSource = null;
        ResultsList.ItemsSource = _lastRows;
    }

    private async void OnCompanyDetailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not CompanySkillRow row) return;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(row.VersionBadge)) parts.Add(row.VersionBadge);
        if (!string.IsNullOrEmpty(row.MetaLine)) parts.Add(row.MetaLine);
        await new SkillDetailDialog(row.Name, row.Description, string.Join("   ·   ", parts)) { XamlRoot = this.XamlRoot }.ShowAsync();
    }

    private void OnGotoAccountClick(object sender, RoutedEventArgs e) =>
        ((IAppCommands)CurrentApp).Navigate("account");
}

/// <summary>Row view-model for the results ListView (adds display-only fields).</summary>
public sealed class CompanySkillRow
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string VersionBadge { get; set; } = "";
    public string MetaLine { get; set; } = "";
    public bool IsInstalled { get; set; }
    public string InstallLabel => IsInstalled ? "已安装" : "安装";
    public bool CanInstall => !IsInstalled;
}
