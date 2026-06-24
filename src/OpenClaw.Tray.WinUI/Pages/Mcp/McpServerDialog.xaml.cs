using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace OpenClawTray.Pages;

/// <summary>MCP 服务器添加/编辑弹框。
/// stdio → {command, args?, env?}（无 transport 字段，写 plugins.entries.acpx.config.mcpServers）；
/// streamable-http → {transport, url}（写 mcp.servers）。
/// 校验通过后 <see cref="ServerName"/>/<see cref="Server"/>/<see cref="IsStdio"/> 可读。</summary>
public sealed partial class McpServerDialog : ContentDialog
{
    public string ServerName { get; private set; } = "";
    public JsonObject? Server { get; private set; }
    /// <summary>当前选中的是否 stdio（决定写入哪条配置路径）。</summary>
    public bool IsStdio => TransportCombo.SelectedIndex == 0;

    public McpServerDialog()
    {
        InitializeComponent();
        ConfigureForAdd();
    }

    /// <summary>新增模式：空白表单。</summary>
    public void ConfigureForAdd()
    {
        Title = "添加 MCP 服务器";
        PrimaryButtonText = "添加";
        ServerName = "";
        Server = null;
        NameBox.Text = "";
        NameBox.IsEnabled = true;
        TransportCombo.IsEnabled = true;
        TransportCombo.SelectedIndex = 0;
        CommandBox.Text = "";
        ArgsBox.Text = "";
        EnvBox.Text = "";
        UrlBox.Text = "";
        ErrorBar.IsOpen = false;
        ApplyTransportVisibility();
    }

    /// <summary>编辑模式：预填已有配置（名称锁定）。</summary>
    public void ConfigureForEdit(string name, Dictionary<string, object?> server)
    {
        Title = "编辑 MCP 服务器";
        PrimaryButtonText = "保存";
        ServerName = name;
        Server = null;
        NameBox.Text = name;
        NameBox.IsEnabled = false;
        // 编辑时锁定类型：merge-patch 无法干净替换子对象（leaf null 被网关拒），
        // 切类型请删除后重新添加。
        TransportCombo.IsEnabled = false;

        // 探测类型：无 transport / transport=stdio / 含 command → 视为 stdio
        var transport = server.TryGetValue("transport", out var t) ? t?.ToString() ?? "" : "";
        var isStdio = string.IsNullOrEmpty(transport)
            || transport == "stdio"
            || server.ContainsKey("command");
        TransportCombo.SelectedIndex = isStdio ? 0 : 1;

        CommandBox.Text = server.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
        UrlBox.Text = server.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
        ArgsBox.Text = server.TryGetValue("args", out var a) && a is List<object?> al
            ? string.Join(' ', al.Select(x => x?.ToString() ?? "")) : "";
        EnvBox.Text = server.TryGetValue("env", out var e) && e is Dictionary<string, object?> ed
            ? string.Join("\n", ed.Select(kv => $"{kv.Key}={kv.Value}")) : "";

        ErrorBar.IsOpen = false;
        ApplyTransportVisibility();
    }

    private void OnTransportChanged(object sender, SelectionChangedEventArgs e) => ApplyTransportVisibility();

    private void ApplyTransportVisibility()
    {
        if (CommandBox == null || ArgsBox == null || UrlBox == null || EnvBox == null) return;
        var isStdio = TransportCombo.SelectedIndex == 0;
        CommandBox.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        ArgsBox.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        EnvBox.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        UrlBox.Visibility = isStdio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var name = NameBox.Text.Trim();
        var transport = TransportCombo.SelectedItem?.ToString() ?? "stdio";
        var command = CommandBox.Text.Trim();
        var argText = ArgsBox.Text.Trim();
        var envText = EnvBox.Text;
        var url = UrlBox.Text.Trim();

        if (string.IsNullOrEmpty(name)) { ShowError("请填写名称。"); args.Cancel = true; return; }
        if (transport == "stdio")
        {
            if (string.IsNullOrWhiteSpace(command)) { ShowError("stdio 类型需要填写命令。"); args.Cancel = true; return; }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(url)) { ShowError("streamable-http 类型需要填写 URL。"); args.Cancel = true; return; }
        }

        ServerName = name;
        Server = BuildServer(transport, command, argText, url, envText);
    }

    private void ShowError(string msg)
    {
        ErrorBar.Message = msg;
        ErrorBar.IsOpen = true;
    }

    /// <summary>据表单构造一条 mcp 条目。
    /// stdio: {command, args?, env?}（不带 transport）；http: {transport, url}。</summary>
    internal static JsonObject BuildServer(string transport, string command, string argText, string url, string envText)
    {
        if (transport == "stdio")
        {
            var o = new JsonObject { ["command"] = command };
            var argList = argText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (argList.Length > 0)
                o["args"] = new JsonArray(argList.Select(a => (JsonNode)a).ToArray());
            var env = ParseEnv(envText);
            if (env != null) o["env"] = env;
            return o;
        }
        return new JsonObject
        {
            ["transport"] = transport,
            ["url"] = url,
        };
    }

    /// <summary>"KEY=value" 多行 → JsonObject；空则返回 null。</summary>
    internal static JsonObject? ParseEnv(string envText)
    {
        var env = new JsonObject();
        foreach (var raw in envText.Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            env[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return env.Count > 0 ? env : null;
    }
}
