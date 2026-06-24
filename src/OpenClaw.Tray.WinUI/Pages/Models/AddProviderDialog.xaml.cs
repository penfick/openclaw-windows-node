using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>添加 Provider 弹框：选类型 → 填凭据 → 获取模型 → 选模型 → 确认。</summary>
public sealed partial class AddProviderDialog : ContentDialog
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private ProviderDefinition? _selected;
    private bool _isEditMode;
    private List<string> _fetchedModels = new();
    private string? _selectedModelId;

    public bool Success { get; private set; }

    public AddProviderDialog()
    {
        InitializeComponent();
        ProviderList.ItemsSource = ProviderCatalog.BuiltIn;
    }

    // ── 编辑模式 ──

    internal void StartEdit(string providerId, string api, string baseUrl, string modelsText)
    {
        _isEditMode = true;
        _selected = ProviderCatalog.FindById(providerId) ?? ProviderCatalog.FindById("custom");

        Step1.Visibility = Visibility.Collapsed;
        Step2.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = false; // 需要先选模型
        DialogTitle.Text = $"编辑 {providerId}";
        PrimaryButtonText = "保存";
        SecondaryButtonText = null; // 编辑模式不显示返回

        SelectedIcon.Text = _selected?.Icon ?? "📦";
        SelectedName.Text = providerId;
        SelectedHint.Text = "编辑模式";

        IdBox.Text = providerId;
        IdBox.IsEnabled = false;
        BaseUrlBox.Text = baseUrl;
        ApiKeyBox.PlaceholderText = "输入新 Key 覆盖（留空不改）";

        FetchModelsButton.Visibility = Visibility.Visible;
    }

    // ── 步骤 1 → 2 ──

    private void OnProviderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ProviderDefinition def) return;
        _selected = def;

        Step1.Visibility = Visibility.Collapsed;
        Step2.Visibility = Visibility.Visible;
        IsPrimaryButtonEnabled = false;
        PrimaryButtonText = "添加";
        SecondaryButtonText = "返回";

        SelectedIcon.Text = def.Icon;
        SelectedName.Text = def.Name;
        SelectedHint.Text = def.Category switch
        {
            "official" => "官方 Provider",
            "compatible" => "OpenAI 兼容",
            "cn" => "国内 Provider",
            "local" => "本地推理",
            "custom" => "自定义端点",
            _ => def.Category,
        };

        IdBox.Text = def.Id;
        BaseUrlBox.Text = def.DefaultBaseUrl ?? "";
        ApiKeyBox.PlaceholderText = def.Category == "local" ? "本地模型可留空" : "sk-...";

        // 官方 provider 隐藏 baseUrl（内置已知）
        BaseUrlBox.Visibility = (def.ShowBaseUrl || def.Id == "custom") ? Visibility.Visible : Visibility.Collapsed;

        // custom 显示 API 类型
        ApiTypePanel.Visibility = def.Id == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    // SecondaryButton（"返回"）：Step2 回到 Step1，而不是关弹框
    private void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // 阻止 ContentDialog 默认关闭行为
        GoBackToStep1();
    }

    private void GoBackToStep1()
    {
        Step2.Visibility = Visibility.Collapsed;
        Step1.Visibility = Visibility.Visible;
        PrimaryButtonText = null;   // Step1 无主操作（点 provider 卡片进入 Step2）
        SecondaryButtonText = null; // Step1 无返回
        IsPrimaryButtonEnabled = false;
        _selected = null;
        ModelCombo.Visibility = Visibility.Collapsed;
        FetchStatusRow.Visibility = Visibility.Collapsed;
        _fetchedModels.Clear();
        _selectedModelId = null;
    }

    // 右上角 ✕：直接关弹框（取消）
    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    // ── 获取模型（调 HTTP GET {baseUrl}/models）──

    private async void OnFetchModelsClick(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlBox.Text.Trim().TrimEnd('/');
        var apiKey = ApiKeyBox.Password.Trim();

        if (string.IsNullOrEmpty(baseUrl))
        {
            ShowError("请先填写 Base URL。");
            return;
        }

        FetchProgress.IsActive = true;
        FetchStatusRow.Visibility = Visibility.Visible;
        FetchModelsButton.IsEnabled = false;
        FetchStatus.Text = "正在获取模型列表…";
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            // 大多数 OpenAI 兼容 API: GET {baseUrl}/models, Authorization: Bearer {key}
            var url = $"{baseUrl}/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                ShowError($"获取失败 ({(int)resp.StatusCode})：{Truncate(body, 200)}");
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            _fetchedModels = ParseModelsResponse(json);

            if (_fetchedModels.Count == 0)
            {
                ShowError("未找到模型。请检查 Base URL 和 API Key。");
                return;
            }

            // 填充 ComboBox
            ModelCombo.ItemsSource = _fetchedModels;
            ModelCombo.SelectedIndex = 0;
            ModelCombo.Visibility = Visibility.Visible;
            _selectedModelId = _fetchedModels[0];
            IsPrimaryButtonEnabled = true;

            FetchStatus.Text = $"找到 {_fetchedModels.Count} 个可用模型";
            FetchStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }
        catch (Exception ex)
        {
            ShowError($"获取失败：{ex.Message}");
        }
        finally
        {
            FetchProgress.IsActive = false;
            FetchModelsButton.IsEnabled = true;
        }
    }

    private static List<string> ParseModelsResponse(string json)
    {
        var models = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            // OpenAI 格式: { data: [{ id: "gpt-4o" }, ...] }
            if (doc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in dataEl.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id)) models.Add(id);
                }
            }
            // 直接数组: [{ id: "..." }, ...]
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(id)) models.Add(id);
                }
            }
        }
        catch { /* 解析失败返回空 */ }
        return models.OrderBy(m => m).ToList();
    }

    private void OnModelSelected(object sender, SelectionChangedEventArgs e)
    {
        _selectedModelId = ModelCombo.SelectedItem as string;
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(_selectedModelId);
    }

    // ── 确认 ──

    private async void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var def = _selected;
        if (def == null || string.IsNullOrEmpty(_selectedModelId)) { args.Cancel = true; return; }

        var id = IdBox.Text.Trim();
        var baseUrl = BaseUrlBox.Text.Trim();
        var apiKey = ApiKeyBox.Password.Trim();
        var modelId = _selectedModelId;
        var api = def.Id == "custom"
            ? (ApiTypeCombo.SelectedItem?.ToString() ?? "openai-completions")
            : def.Api;

        if (string.IsNullOrEmpty(id)) { args.Cancel = true; ShowError("Provider ID 为空。"); return; }

        var client = CurrentApp.GatewayClient;
        if (client == null) { args.Cancel = true; ShowError("未连接到网关。"); return; }

        var deferral = args.GetDeferral();
        args.Cancel = true;

        try
        {
            if (!_isEditMode)
            {
                // 新增：provider 配置 + allowlist + 默认模型，累积后单次 config.patch 写回（避免限流）
                await ModelsPage.AddProviderFullyAsync(client, id, api, baseUrl, apiKey, modelId);
            }
            else if (!string.IsNullOrEmpty(apiKey))
            {
                // 编辑模式：只更新 apiKey
                await ModelsPage.SetProviderApiKeyAsync(client, id, apiKey);
            }

            Success = true;
            args.Cancel = false;
        }
        catch (Exception ex)
        {
            ShowError($"写入失败：{ModelsPage.FriendlyConfigError(ex.Message)}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
        FetchStatusRow.Visibility = Visibility.Collapsed;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
