using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Text.Json.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// 模型配置页：以模型卡片为单位展示 allowlist（agents.defaults.models）。
/// 每张卡片 = 一个 provider/model 组合。默认卡片排第一，只有编辑（切模型）。
/// 非默认卡片有编辑 + 设为默认 + 删除。
/// </summary>
public sealed partial class ModelsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;

    // 完整数据（从 config.get + models.list 组装）
    private List<ModelCardData> _allCards = new();
    private Dictionary<string, List<string>> _providerCatalog = new(); // providerId → 可选 model 列表

    public ModelsPage() { InitializeComponent(); }

    public void Initialize() => _ = LoadAsync();

    // ── 加载 ──

    private async Task LoadAsync()
    {
        var client = CurrentApp.GatewayClient;
        if (client == null) { StatusText.Text = "未连接到网关。"; return; }

        LoadingProgress.IsActive = true;
        StatusText.Text = string.Empty;
        try
        {
            var resp = await client.SendWizardRequestAsync("config.get");
            var root = resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("config", out var cfgEl) && cfgEl.ValueKind == JsonValueKind.Object
                ? cfgEl : resp;

            // 当前默认
            var primaryRef = ReadPrimary(root);

            // provider 配置（api/baseUrl/models）
            var providerInfo = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("models", out var modelsNode)
                && modelsNode.TryGetProperty("providers", out var provs)
                && provs.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in provs.EnumerateObject())
                    providerInfo[p.Name] = p.Value;
            }

            // 补全 provider 模型目录（从 models.list + config providers.models 双来源）
            _providerCatalog.Clear();
            // 来源 1：config 的 providers.<id>.models[]
            foreach (var (pid, pval) in providerInfo)
            {
                var list = new List<string>();
                if (pval.TryGetProperty("models", out var marr) && marr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in marr.EnumerateArray())
                    {
                        var mid = m.ValueKind == JsonValueKind.String ? m.GetString() ?? ""
                            : (m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "");
                        if (!string.IsNullOrEmpty(mid)) list.Add(mid);
                    }
                }
                _providerCatalog[pid] = list;
            }
            // 来源 2：models.list（含 plugin catalog 注入的模型）
            try
            {
                var mlResp = await client.SendWizardRequestAsync("models.list", new { view = "all" });
                var arr = mlResp.ValueKind == JsonValueKind.Array ? mlResp
                    : (mlResp.TryGetProperty("models", out var mlArr) && mlArr.ValueKind == JsonValueKind.Array ? mlArr : default);
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in arr.EnumerateArray())
                    {
                        var mid = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var mprov = m.TryGetProperty("provider", out var provEl) ? provEl.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(mid) && !string.IsNullOrEmpty(mprov))
                        {
                            if (!_providerCatalog.ContainsKey(mprov))
                                _providerCatalog[mprov] = new List<string>();
                            if (!_providerCatalog[mprov].Contains(mid))
                                _providerCatalog[mprov].Add(mid);
                        }
                    }
                }
            }
            catch { /* models.list 失败时用 config-only */ }

            // allowlist → 卡片
            var cards = new List<ModelCardData>();
            if (root.TryGetProperty("agents", out var agents)
                && agents.TryGetProperty("defaults", out var defs)
                && defs.TryGetProperty("models", out var allowlist)
                && allowlist.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in allowlist.EnumerateObject())
                {
                    var key = entry.Name;
                    var slash = key.IndexOf('/');
                    var provId = slash > 0 ? key[..slash] : key;
                    var modelId = slash > 0 ? key[(slash + 1)..] : "";
                    var cat = ProviderCatalog.FindById(provId);
                    var displayModel = modelId;

                    // 从 models.list 找 display name
                    if (_providerCatalog.TryGetValue(provId, out var pmodels) && pmodels.Contains(modelId))
                        displayModel = modelId;

                    cards.Add(new ModelCardData
                    {
                        Key = key,
                        ProviderId = provId,
                        ProviderName = cat?.Name ?? provId,
                        Icon = cat?.Icon ?? "📦",
                        ModelId = modelId,
                        ModelDisplay = displayModel,
                        IsDefault = key == primaryRef,
                    });
                }
            }

            // 排序：默认在前
            _allCards = cards
                .OrderByDescending(c => c.IsDefault)
                .ThenBy(c => c.ProviderName)
                .ThenBy(c => c.ModelId)
                .ToList();

            CardsControl.ItemsSource = _allCards;
            StatusText.Text = _allCards.Count > 0 ? $"{_allCards.Count} 个模型" : "暂无模型，点「+ 添加模型」开始。";
        }
        catch (Exception ex) { StatusText.Text = $"加载失败：{ex.Message}"; }
        finally
        {
            LoadingProgress.IsActive = false;
            // 刷新网关 models.list（聊天选择器读它），让配置改动尽快反映，避免"重启才生效"。
            // 注：新增 provider 的注册仍可能需要网关重启（provider 启动时加载）。
            _ = CurrentApp.GatewayClient?.RequestModelsListAsync();
        }
    }

    // ── 配置写入：定向 JSON 合并补丁（config.patch）──
    // 此网关的 config.patch 是 RFC 7396 合并补丁：对象递归合并、标量/数组替换、
    // **null 表示删除该键**、缺失的键保持不变。因此：
    //   - 添加 allowlist 项：{"agents":{"defaults":{"models":{"<key>":{}}}}}
    //   - 删除 allowlist 项：{"agents":{"defaults":{"models":{"<key>":null}}}}  ← 必须用 null
    //   - 「删掉 key 再写回整份配置」对删除无效（缺失键不会被删）。
    // 每次操作一次 config.get（取 baseHash）+ 一次 config.patch，避免限流。

    /// <summary>读取 config.get 的 baseHash（合并补丁必需的乐观锁凭证）。</summary>
    internal static async Task<string?> ReadBaseHashAsync(IOperatorGatewayClient client)
    {
        var resp = await client.SendWizardRequestAsync("config.get");
        if (resp.TryGetProperty("baseHash", out var bh) && bh.ValueKind == JsonValueKind.String)
            return bh.GetString();
        if (resp.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String)
            return h.GetString();
        if (resp.TryGetProperty("raw", out var rawEl) && rawEl.ValueKind == JsonValueKind.String)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(rawEl.GetString() ?? "");
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        return null;
    }

    /// <summary>发送一次定向 config.patch。raw 是补丁对象（合并语义）。</summary>
    internal static async Task WritePatchAsync(IOperatorGatewayClient client, JsonNode patch, string? baseHash)
    {
        object payload = baseHash != null
            ? (object)new { raw = patch.ToJsonString(), baseHash }
            : new { raw = patch.ToJsonString() };
        await client.SendWizardRequestAsync("config.patch", payload);
    }

    /// <summary>构造嵌套补丁：{"a":{"b":{finalKey:finalValue}}}（parentPath = "a.b"）。</summary>
    internal static JsonObject BuildNestedPatch(string parentPath, string finalKey, JsonNode? finalValue)
    {
        JsonObject node = new JsonObject { [finalKey] = finalValue };
        foreach (var seg in parentPath.Split('.').Reverse())
            node = new JsonObject { [seg] = node };
        return node;
    }

    // ── 组合操作：单次 config.patch（省配额，避免限流）──

    /// <summary>新增 provider：写 provider 配置 + 加 allowlist + 设默认模型，单次 config.patch。</summary>
    internal static async Task AddProviderFullyAsync(IOperatorGatewayClient client, string id, string api, string baseUrl, string apiKey, string modelId)
    {
        var baseHash = await ReadBaseHashAsync(client);
        var key = $"{id}/{modelId}";
        var patch = new JsonObject
        {
            ["models"] = new JsonObject
            {
                ["providers"] = new JsonObject
                {
                    [id] = new JsonObject
                    {
                        ["api"] = api,
                        ["baseUrl"] = baseUrl,
                        ["apiKey"] = apiKey,
                        ["models"] = new JsonArray(new JsonObject { ["id"] = modelId, ["name"] = modelId }),
                    }
                }
            },
            ["agents"] = new JsonObject
            {
                ["defaults"] = new JsonObject
                {
                    ["models"] = new JsonObject { [key] = new JsonObject() },
                    ["model"] = new JsonObject { ["primary"] = key },
                }
            }
        };
        await WritePatchAsync(client, patch, baseHash);
    }

    /// <summary>切换模型：加新 model 到 allowlist + 可选设默认，单次 config.patch。</summary>
    internal static async Task SwitchModelFullyAsync(IOperatorGatewayClient client, string newKey, bool makeDefault)
    {
        var baseHash = await ReadBaseHashAsync(client);
        var defaults = new JsonObject
        {
            ["models"] = new JsonObject { [newKey] = new JsonObject() }
        };
        if (makeDefault)
            defaults["model"] = new JsonObject { ["primary"] = newKey };
        var patch = new JsonObject { ["agents"] = new JsonObject { ["defaults"] = defaults } };
        await WritePatchAsync(client, patch, baseHash);
    }

    // ── 单项操作（各一次 config.get + 一次 config.patch）──

    internal static async Task SetProviderConfigAsync(IOperatorGatewayClient client, string providerId, string api, string baseUrl, string apiKey, string modelId)
    {
        var baseHash = await ReadBaseHashAsync(client);
        var provider = new JsonObject
        {
            ["api"] = api,
            ["baseUrl"] = baseUrl,
            ["apiKey"] = apiKey,
            ["models"] = new JsonArray(new JsonObject { ["id"] = modelId, ["name"] = modelId }),
        };
        var patch = BuildNestedPatch("models.providers", providerId, provider);
        await WritePatchAsync(client, patch, baseHash);
    }

    internal static async Task SetProviderApiKeyAsync(IOperatorGatewayClient client, string providerId, string apiKey)
    {
        var baseHash = await ReadBaseHashAsync(client);
        var patch = BuildNestedPatch("models.providers", providerId, new JsonObject { ["apiKey"] = apiKey });
        await WritePatchAsync(client, patch, baseHash);
    }

    internal static async Task AddToAllowlistAsync(IOperatorGatewayClient client, string key)
    {
        var baseHash = await ReadBaseHashAsync(client);
        var patch = BuildNestedPatch("agents.defaults.models", key, new JsonObject());
        await WritePatchAsync(client, patch, baseHash);
    }

    /// <summary>从 allowlist 删 key：合并补丁用 null 删除该键（缺失键不会被删）。</summary>
    internal static async Task RemoveFromAllowlistAsync(IOperatorGatewayClient client, string key)
    {
        var baseHash = await ReadBaseHashAsync(client);
        var patch = BuildNestedPatch("agents.defaults.models", key, null);
        await WritePatchAsync(client, patch, baseHash);
    }

    internal static async Task SetDefaultModelAsync(IOperatorGatewayClient client, string modelRef)
    {
        var baseHash = await ReadBaseHashAsync(client);
        var patch = BuildNestedPatch("agents.defaults.model", "primary", modelRef);
        await WritePatchAsync(client, patch, baseHash);
    }

    /// <summary>把网关返回的配置错误转成友好提示（主要是 config.patch 限流）。</summary>
    internal static string FriendlyConfigError(string msg)
    {
        if (msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || msg.Contains("retry after", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(msg, @"retry after\s*(\d+)\s*s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success
                ? $"网关配置写入限流，请等待 {m.Groups[1].Value} 秒后重试。"
                : "网关配置写入限流，请稍候再试。";
        }
        return msg;
    }

    private static string ReadPrimary(JsonElement root)
    {
        if (root.TryGetProperty("agents", out var agents)
            && agents.TryGetProperty("defaults", out var def)
            && def.TryGetProperty("model", out var m))
        {
            if (m.ValueKind == JsonValueKind.String) return m.GetString() ?? "";
            if (m.TryGetProperty("primary", out var pri)) return pri.GetString() ?? "";
        }
        return "";
    }

    // ── 卡片操作 ──

    /// <summary>编辑：打开编辑弹框。默认卡 = 切模型；非默认卡 = 切模型 + 改 API Key。</summary>
    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ModelCardData card) return;
        var client = CurrentApp.GatewayClient;
        if (client == null) return;

        // 汇集该 provider 的所有可选模型（catalog + config + 当前）
        var models = new HashSet<string> { card.ModelId };
        if (_providerCatalog.TryGetValue(card.ProviderId, out var catalog))
            foreach (var m in catalog) models.Add(m);

        var dialog = new EditModelDialog { XamlRoot = this.XamlRoot };
        dialog.Setup(card, models.OrderBy(m => m).ToList());
        await dialog.ShowAsync();

        if (dialog.ResultAction == EditModelDialog.Action.SwitchModel && !string.IsNullOrEmpty(dialog.SelectedModelId))
        {
            var newKey = $"{card.ProviderId}/{dialog.SelectedModelId}";
            if (newKey != card.Key)
            {
                if (_allCards.Any(c => c.Key == newKey) && !card.IsDefault)
                {
                    StatusText.Text = $"模型 {dialog.SelectedModelId} 已存在。";
                    return;
                }
                // 加新模型到 allowlist + 可选设默认，单次 config.patch 写回（避免限流）
                await SwitchModelFullyAsync(client, newKey, card.IsDefault);
                await Task.Delay(1000);
                await LoadAsync();
            }
        }
        else if (dialog.ResultAction == EditModelDialog.Action.SaveConfig && !string.IsNullOrEmpty(dialog.ApiKey))
        {
            try
            {
                await SetProviderApiKeyAsync(client, card.ProviderId, dialog.ApiKey);
                await Task.Delay(1000);
                await LoadAsync();
            }
            catch (Exception ex) { StatusText.Text = $"更新失败：{ex.Message}"; }
        }
    }

    private async void OnSetDefaultClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag?.ToString() is not { } key) return;
        var client = CurrentApp.GatewayClient;
        if (client == null) return;
        LoadingProgress.IsActive = true;
        StatusText.Text = "正在切换默认模型…";
        try
        {
            await SetDefaultModelAsync(client, key);
            await Task.Delay(1000);
            await LoadAsync();
            StatusText.Text = "默认模型已切换。";
        }
        catch (Exception ex) { StatusText.Text = $"设为默认失败：{FriendlyConfigError(ex.Message)}"; }
        finally { LoadingProgress.IsActive = false; }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag?.ToString() is not { } key) return;
        var client = CurrentApp.GatewayClient;
        if (client == null) return;
        LoadingProgress.IsActive = true;
        StatusText.Text = "正在删除…";
        try
        {
            await RemoveFromAllowlistAsync(client, key);
            await Task.Delay(1500);
            await LoadAsync();
            StatusText.Text = "已删除。";
        }
        catch (Exception ex) { StatusText.Text = $"删除失败：{FriendlyConfigError(ex.Message)}"; }
        finally { LoadingProgress.IsActive = false; }
    }

    // ── 添加 ──

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AddProviderDialog { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
        if (dialog.Success)
            await LoadAsync();
    }
}

// ── 数据模型 ──

public sealed class ModelCardData
{
    public string Key { get; set; } = "";               // "provider/modelId"
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string Icon { get; set; } = "📦";
    public string ModelId { get; set; } = "";
    public string ModelDisplay { get; set; } = "";
    public bool IsDefault { get; set; }

    public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DefaultBadgeVisibilityInverted => IsDefault ? Visibility.Collapsed : Visibility.Visible;
}
