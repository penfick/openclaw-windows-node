using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;

namespace OpenClawTray.Pages;

/// <summary>编辑模型卡片：切换模型 + 修改 API Key。</summary>
public sealed partial class EditModelDialog : ContentDialog
{
    public enum Action { None, SwitchModel, SaveConfig }

    public Action ResultAction { get; private set; } = Action.None;
    public string? SelectedModelId { get; private set; }
    public string? ApiKey { get; private set; }

    private bool _modelChanged;

    public EditModelDialog() { InitializeComponent(); }

    public void Setup(ModelCardData card, IReadOnlyList<string> availableModels)
    {
        HeaderIcon.Text = card.Icon;
        HeaderTitle.Text = $"{card.ProviderName}";
        Title = card.IsDefault ? "编辑默认模型" : "编辑模型";

        ModelCombo.ItemsSource = availableModels;
        if (availableModels.Contains(card.ModelId))
            ModelCombo.SelectedValue = card.ModelId;
        else if (availableModels.Count > 0)
            ModelCombo.SelectedIndex = 0;

        ModelCombo.SelectionChanged += (_, _) => _modelChanged = true;
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var modelId = ModelCombo.SelectedItem as string;
        var apiKey = ApiKeyBox.Text.Trim();

        if (!_modelChanged && string.IsNullOrEmpty(apiKey))
        {
            args.Cancel = true;
            ErrorText.Text = "请切换模型或输入新的 API Key。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (_modelChanged && !string.IsNullOrEmpty(modelId))
        {
            SelectedModelId = modelId;
            ResultAction = Action.SwitchModel;
        }
        else if (!string.IsNullOrEmpty(apiKey))
        {
            ApiKey = apiKey;
            ResultAction = Action.SaveConfig;
        }

        // 允许关闭
    }
}
