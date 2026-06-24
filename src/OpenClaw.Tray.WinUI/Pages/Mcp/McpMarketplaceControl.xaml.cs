using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>「市场」tab：npm registry 搜索 + 精选 catalog + 一键安装。
/// 安装走 config.patch 合并补丁（config.set 在此网关被拒）；已配置的服务器名匹配后标「已安装」。
/// 装完触发 <see cref="InstalledChanged"/>，由 McpConfigPage 刷新「我的服务器」。</summary>
public sealed partial class McpMarketplaceControl : UserControl
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // 已配置的 MCP 服务器名集合（权威状态来自 config.get）
    private readonly HashSet<string> _installedNames = new(StringComparer.OrdinalIgnoreCase);
    // 正在安装的服务器名（按钮显示「正在安装…」并禁用）
    private string? _installingName;

    /// <summary>市场装完触发：McpConfigPage 订阅后刷新「我的服务器」。</summary>
    public event Action? InstalledChanged;

    public McpMarketplaceControl()
    {
        InitializeComponent();
        _ = LoadInstalledAndRenderCatalogAsync();
    }

    /// <summary>读 mcp.servers 名称集合，并渲染精选 catalog（标记已安装）。</summary>
    public async Task LoadInstalledAndRenderCatalogAsync()
    {
        await LoadInstalledNamesAsync();
        RenderCatalog();
    }

    private async Task LoadInstalledNamesAsync()
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) return;
        try
        {
            var resp = await client.SendWizardRequestAsync("config.get");
            var root = resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("config", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object
                ? cfgEl : resp;
            _installedNames.Clear();
            // native agent MCP 在 mcp.servers（openclaw mcp list 读的就是这里）
            var serversEl = McpConfig.Walk(root, McpConfig.ServersPath);
            if (serversEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in serversEl.EnumerateObject())
                    if (!string.IsNullOrEmpty(prop.Name)) _installedNames.Add(prop.Name);
            }
        }
        catch { /* 忽略，按未安装处理 */ }
    }

    private void RenderCatalog()
    {
        CatalogList.ItemsSource = McpCatalog.Entries
            .Select(e => new McpCatalogCard(e, _installedNames.Contains(e.Name), _installingName == e.Name))
            .ToList();
    }

    /// <summary>把搜索结果里同名片刷新成最新安装状态（不重发搜索）。</summary>
    private void RefreshSearchInstalled()
    {
        if (SearchResults.ItemsSource is not IEnumerable<McpSearchCard> rows) return;
        SearchResults.ItemsSource = rows
            .Select(r => r with { IsInstalled = _installedNames.Contains(r.Name), Installing = _installingName == r.Name })
            .ToList();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter) _ = SearchAsync();
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        var q = SearchBox.Text.Trim();
        SearchProgress.IsActive = true;
        StatusText.Text = string.Empty;
        try
        {
            var term = string.IsNullOrWhiteSpace(q) ? "mcp server" : q;
            var url = $"https://registry.npmmirror.com/-/v1/search?text={Uri.EscapeDataString(term)}&size=40";
            var json = await Http.GetStringAsync(url);
            var results = new List<McpSearchCard>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("objects", out var objs) && objs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in objs.EnumerateArray())
                    {
                        if (!o.TryGetProperty("package", out var pkg)) continue;
                        var name = pkg.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var desc = pkg.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var ver = pkg.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                        if (!name.Contains("mcp", StringComparison.OrdinalIgnoreCase)
                            && !name.Contains("@modelcontextprotocol", StringComparison.OrdinalIgnoreCase)
                            && !desc.Contains("mcp", StringComparison.OrdinalIgnoreCase))
                            continue;
                        results.Add(new McpSearchCard(name, desc, ver, _installedNames.Contains(name), false));
                    }
                }
            }
            SearchResults.ItemsSource = results;
            StatusText.Text = results.Count > 0 ? $"找到 {results.Count} 个结果" : "无结果";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"搜索失败：{ex.Message}";
            SearchResults.ItemsSource = null;
        }
        finally
        {
            SearchProgress.IsActive = false;
        }
    }

    private async void OnInstallCatalogClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not McpCatalogCard card) return;
        await InstallAsync(card.Entry.Name, card.Entry.Package, card.Entry.AuthEnvKey);
    }

    private async void OnInstallSearchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not McpSearchCard card) return;
        // npm 搜索结果：name 同时作为 server 名 + package 名
        await InstallAsync(card.Name, card.Name, null);
    }

    /// <summary>把一条 stdio/npx 配置合并进 gateway 的 mcp.servers（config.patch）。</summary>
    private async Task InstallAsync(string name, string package, string? authEnvKey)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { StatusText.Text = "未连接到网关。"; return; }
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称为空。"; return; }
        if (_installingName != null) { StatusText.Text = "请等待当前安装完成。"; return; }

        // 立刻把目标卡片切成「正在安装…」并禁用，给出明确反馈
        _installingName = name;
        RenderCatalog();
        RefreshSearchInstalled();
        StatusText.Text = $"正在安装 {name}…";

        try
        {
            // stdio 服务器：{command, args}（不带 transport），写 mcp.servers（native agent 路径）
            var server = new JsonObject
            {
                ["command"] = "npx",
                ["args"] = new JsonArray { "-y", package },
            };
            await McpConfig.WriteAsync(client, name, server);

            // 权威重读 config（而不是只乐观地加本地集合），确保「已安装」状态可信
            await LoadInstalledNamesAsync();
            _installingName = null;
            RenderCatalog();
            RefreshSearchInstalled();
            InstalledChanged?.Invoke();

            StatusText.Text = $"已安装 {name}。" + (authEnvKey != null
                ? $" 注意：此 MCP 需要配置环境变量 {authEnvKey}（在「我的服务器」里编辑该服务器的 env）才能工作。"
                : "");
        }
        catch (Exception ex)
        {
            _installingName = null;
            RenderCatalog();
            RefreshSearchInstalled();
            StatusText.Text = $"安装失败：{ModelsPage.FriendlyConfigError(ex.Message)}";
        }
    }
}

public sealed class McpCatalogCard
{
    public McpCatalogEntry Entry { get; }
    public McpCatalogCard(McpCatalogEntry entry, bool isInstalled, bool installing)
    {
        Entry = entry;
        IsInstalled = isInstalled;
        Installing = installing;
    }
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public string Category => Entry.Category;
    public string Package => Entry.Package;
    public string EnvHint => string.IsNullOrEmpty(Entry.AuthEnvKey) ? "" : $"需 {Entry.AuthEnvKey}";
    public bool IsInstalled { get; }
    public bool Installing { get; }
    public string InstallLabel => Installing ? "正在安装…" : (IsInstalled ? "已安装" : "一键安装");
    public bool CanInstall => !IsInstalled && !Installing;
}

public sealed record McpSearchCard(string Name, string Description, string Version, bool IsInstalled, bool Installing)
{
    public string InstallLabel => Installing ? "正在安装…" : (IsInstalled ? "已安装" : "安装");
    public bool CanInstall => !IsInstalled && !Installing;
}
