using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>「我的服务器」tab：列出/增删改 gateway 的 MCP 服务器（mcp.servers）。
/// 写入走 config.patch 合并补丁；CleanEntry 保证类型切换不留残字段；删除用 null 删键。
/// 启动时把旧版误写到 acpx.config.mcpServers 的条目一次性迁回 mcp.servers。</summary>
public sealed partial class McpMyServersControl : UserControl
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private readonly Dictionary<string, Dictionary<string, object?>> _servers = new();

    public McpMyServersControl()
    {
        InitializeComponent();
    }

    /// <summary>由 Pivot host 在切到本 tab 时调用。</summary>
    public void Initialize() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            StatusText.Text = "未连接到网关，请先连接。";
            return;
        }

        LoadingProgress.IsActive = true;
        StatusText.Text = string.Empty;
        try
        {
            // 直读 openclaw.json（瞬时），不走 config.get RPC（避免 ~0.5-1.2s + reload 争用尖峰）。
            var root = await OpenClawConfigFile.ReadRootElementAsync();

            // 一次性迁移：旧版误写到 acpx.config.mcpServers → 挪到 mcp.servers
            if (await TryMigrateLegacyAsync(client, root))
            {
                await LoadAsync(); // 迁移后重读
                return;
            }

            _servers.Clear();
            var rows = new List<McpServerRow>();
            var serversEl = McpConfig.Walk(root, McpConfig.ServersPath);
            if (serversEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in serversEl.EnumerateObject())
                {
                    var dict = ToClr(prop.Value) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                    var transport = dict.TryGetValue("transport", out var t) ? t?.ToString() ?? "" : "";
                    // 无 transport 且有 command → stdio；否则按 transport 字段（sse/streamable-http）
                    var isStdio = string.IsNullOrEmpty(transport) || transport == "stdio";
                    var displayTransport = isStdio ? "stdio" : transport;
                    _servers[prop.Name] = dict;
                    rows.Add(new McpServerRow
                    {
                        Name = prop.Name,
                        Transport = displayTransport,
                        Display = BuildDisplay(dict, displayTransport),
                        EnvHint = BuildEnvHint(dict),
                    });
                }
            }

            ServersList.ItemsSource = rows;
            var empty = rows.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ServersList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = rows.Count > 0 ? $"共 {rows.Count} 个服务器" : string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取配置失败：{ex.Message}";
        }
        finally
        {
            LoadingProgress.IsActive = false;
        }
    }

    /// <summary>若有旧版 acpx.mcpServers 条目，迁移到 mcp.servers 并返回 true（调用方应重读）。</summary>
    private async Task<bool> TryMigrateLegacyAsync(IOperatorGatewayClient client, JsonElement root)
    {
        var acpxEl = McpConfig.Walk(root, McpConfig.LegacyAcpxPath);
        if (acpxEl.ValueKind != JsonValueKind.Object) return false;
        bool hasAny = false;
        foreach (var _ in acpxEl.EnumerateObject()) { hasAny = true; break; }
        if (!hasAny) return false;

        StatusText.Text = "正在迁移旧版 MCP 配置到 mcp.servers…";
        try
        {
            return await McpConfig.MigrateLegacyAsync(client, acpxEl);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"迁移失败：{ModelsPage.FriendlyConfigError(ex.Message)}（可忽略，新安装已用正确路径）";
            return false;
        }
    }

    private static string BuildDisplay(Dictionary<string, object?> srv, string transport)
    {
        if (transport == "stdio")
        {
            var cmd = srv.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
            var args = srv.TryGetValue("args", out var a) && a is List<object?> al
                ? string.Join(' ', al.Select(x => x?.ToString() ?? ""))
                : "";
            return string.IsNullOrWhiteSpace(args) ? cmd : $"{cmd} {args}";
        }
        return srv.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
    }

    /// <summary>"env: KEY1, KEY2"（仅列出键名，值不外泄）。</summary>
    private static string BuildEnvHint(Dictionary<string, object?> srv)
    {
        if (!srv.TryGetValue("env", out var e) || e is not Dictionary<string, object?> ed || ed.Count == 0)
            return "";
        return "env: " + string.Join(", ", ed.Keys);
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { StatusText.Text = "未连接到网关。"; return; }

        var dialog = new McpServerDialog { XamlRoot = this.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Server is not { } server) return;

        var name = dialog.ServerName;
        StatusText.Text = $"正在添加 {name}…";
        try
        {
            await McpConfig.WriteAsync(client, name, server);
            await LoadAsync();
            StatusText.Text = $"已添加 {name}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"写入失败：{ModelsPage.FriendlyConfigError(ex.Message)}";
        }
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) return;
        if (sender is not Button btn || btn.Tag?.ToString() is not { } name) return;
        if (!_servers.TryGetValue(name, out var raw)) return;

        var dialog = new McpServerDialog { XamlRoot = this.XamlRoot };
        dialog.ConfigureForEdit(name, raw);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Server is not { } server) return;

        StatusText.Text = $"正在保存 {name}…";
        try
        {
            await McpConfig.WriteAsync(client, name, server);
            await LoadAsync();
            StatusText.Text = $"已更新 {name}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"写入失败：{ModelsPage.FriendlyConfigError(ex.Message)}";
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) return;
        if (sender is not Button btn || btn.Tag?.ToString() is not { } name) return;
        if (!_servers.ContainsKey(name)) return;

        var confirm = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "删除服务器",
            Content = $"确定删除「{name}」吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        StatusText.Text = $"正在删除 {name}…";
        try
        {
            await McpConfig.WriteAsync(client, name, null);
            await LoadAsync();
            StatusText.Text = $"已删除 {name}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败：{ModelsPage.FriendlyConfigError(ex.Message)}";
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

public sealed class McpServerRow
{
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "";
    public string Display { get; set; } = "";
    public string EnvHint { get; set; } = "";
    public Visibility EnvHintVisibility => string.IsNullOrEmpty(EnvHint) ? Visibility.Collapsed : Visibility.Visible;
    public string TransportBadge => Transport switch
    {
        "stdio" => "stdio",
        "streamable-http" => "http",
        "sse" => "sse",
        _ => Transport,
    };
}
