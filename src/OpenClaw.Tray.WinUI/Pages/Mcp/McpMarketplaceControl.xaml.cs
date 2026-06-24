using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>「市场」tab：npm registry 搜索 + 精选 catalog + 一键安装（写 mcp.servers）。</summary>
public sealed partial class McpMarketplaceControl : UserControl
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public McpMarketplaceControl()
    {
        InitializeComponent();
        CatalogList.ItemsSource = McpCatalog.Entries;
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
            var results = new List<McpSearchResult>();
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
                        results.Add(new McpSearchResult { Name = name, Description = desc, Version = ver });
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
        if (sender is not Button btn || btn.DataContext is not McpCatalogEntry entry) return;
        await InstallAsync(entry.Name, entry.Package, entry.AuthEnvKey);
    }

    private async void OnInstallSearchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not McpSearchResult result) return;
        // npm 搜索结果：name 作为 server 名 + package 名
        await InstallAsync(result.Name, result.Name, null);
    }

    /// <summary>把一条 stdio/npx 配置写进 gateway 的 mcp.servers（读现有 → 合并 → SetConfigAsync）。</summary>
    private async Task InstallAsync(string name, string package, string? authEnvKey)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { StatusText.Text = "未连接到网关。"; return; }
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "名称为空。"; return; }

        StatusText.Text = $"正在安装 {name}…";
        try
        {
            var resp = await client.SendWizardRequestAsync("config.get");
            var root = resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("config", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object
                ? cfgEl
                : resp;

            var servers = new Dictionary<string, Dictionary<string, object?>>();
            if (root.TryGetProperty("mcp", out var mcp) && mcp.TryGetProperty("servers", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in s.EnumerateObject())
                    servers[prop.Name] = ToClr(prop.Value) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            }

            servers[name] = new Dictionary<string, object?>
            {
                ["transport"] = "stdio",
                ["command"] = "npx",
                ["args"] = new List<object?> { "-y", package },
            };

            await client.SetConfigAsync("mcp.servers", servers);
            StatusText.Text = $"已安装 {name}。" + (authEnvKey != null ? $" 注意：此 MCP 需要配置环境变量 {authEnvKey} 才能工作。" : "");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"安装失败：{ex.Message}";
        }
    }

    private static object? ToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => (object?)e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var i) ? i : (object)e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => e.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value)),
        _ => null,
    };
}

public sealed class McpSearchResult
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
}
