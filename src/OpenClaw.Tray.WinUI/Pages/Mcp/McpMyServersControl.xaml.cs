using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>「我的服务器」tab：列出/增删 gateway 的 mcp.servers（config.get + SetConfigAsync）。</summary>
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
            var resp = await client.SendWizardRequestAsync("config.get");
            var root = resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("config", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object
                ? cfgEl
                : resp;

            _servers.Clear();
            var rows = new List<McpServerRow>();
            if (root.TryGetProperty("mcp", out var mcp) && mcp.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in servers.EnumerateObject())
                {
                    var transport = prop.Value.TryGetProperty("transport", out var t) ? t.GetString() ?? "stdio" : "stdio";
                    var dict = ToClr(prop.Value) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
                    _servers[prop.Name] = dict;
                    rows.Add(new McpServerRow
                    {
                        Name = prop.Name,
                        Transport = transport,
                        Display = BuildDisplay(dict, transport),
                    });
                }
            }
            ServersList.ItemsSource = rows;
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private static string BuildDisplay(Dictionary<string, object?> srv, string transport)
    {
        if (transport != "stdio")
        {
            return srv.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
        }
        var cmd = srv.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
        var args = srv.TryGetValue("args", out var a) && a is List<object?> al
            ? string.Join(' ', al.Select(x => x?.ToString() ?? ""))
            : "";
        return string.IsNullOrWhiteSpace(args) ? cmd : $"{cmd} {args}";
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { StatusText.Text = "未连接到网关。"; return; }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusText.Text = "请填写名称。"; return; }

        var transport = TransportCombo.SelectedItem?.ToString() ?? "stdio";
        var dict = new Dictionary<string, object?> { ["transport"] = transport };
        if (transport == "stdio")
        {
            if (string.IsNullOrWhiteSpace(CommandBox.Text)) { StatusText.Text = "stdio 需要填写命令。"; return; }
            dict["command"] = CommandBox.Text.Trim();
            var args = ArgsBox.Text.Trim();
            if (!string.IsNullOrEmpty(args))
                dict["args"] = SplitArgs(args);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(UrlBox.Text)) { StatusText.Text = "streamable-http 需要填写 URL。"; return; }
            dict["url"] = UrlBox.Text.Trim();
        }

        _servers[name] = dict;
        StatusText.Text = $"正在写入 {name}…";
        try
        {
            await client.SetConfigAsync("mcp.servers", _servers);
            NameBox.Text = ""; CommandBox.Text = ""; ArgsBox.Text = ""; UrlBox.Text = "";
            await LoadAsync();
            StatusText.Text = $"已添加 {name}（如网关需要重启才能生效，请手动重启）。";
        }
        catch (Exception ex)
        {
            _servers.Remove(name);
            StatusText.Text = $"写入失败：{ex.Message}";
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) return;
        if (sender is not Button btn || btn.Tag?.ToString() is not { } name) return;

        if (!_servers.Remove(name)) return;
        StatusText.Text = $"正在删除 {name}…";
        try
        {
            await client.SetConfigAsync("mcp.servers", _servers);
            await LoadAsync();
            StatusText.Text = $"已删除 {name}。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败：{ex.Message}";
            await LoadAsync();
        }
    }

    private void OnTransportChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandBox == null || ArgsBox == null || UrlBox == null) return;
        var isStdio = (TransportCombo.SelectedIndex == 0);
        CommandBox.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        ArgsBox.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        UrlBox.Visibility = isStdio ? Visibility.Collapsed : Visibility.Visible;
    }

    private static List<string> SplitArgs(string args)
        => args.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

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
}
