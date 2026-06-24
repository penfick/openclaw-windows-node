using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// Self-hosted Dify knowledge-base chat. Independent of the gateway — talks
/// directly to the configured Dify instance over SSE. Ported from XClaw's
/// dify store + dify:sendMessage handler. Text is hardcoded zh-CN for now.
/// </summary>
public sealed partial class DifyPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private readonly ObservableCollection<DifyMessage> _messages = new();
    private string? _conversationId;
    private CancellationTokenSource? _sendCts;

    public DifyPage()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _messages;
        Loaded += (_, _) => Initialize();
    }

    /// <summary>Called by HubWindow.InitializeCurrentPage on navigation.</summary>
    public void Initialize()
    {
        var settings = CurrentApp.Settings;
        BaseUrlBox.Text = settings.DifyBaseUrl;
        ApiKeyBox.Password = settings.DifyApiKey;
    }

    private void OnSaveConfigClick(object sender, RoutedEventArgs e)
    {
        var settings = CurrentApp.Settings;
        settings.DifyBaseUrl = BaseUrlBox.Text.Trim();
        settings.DifyApiKey = ApiKeyBox.Password;
        settings.Save();
        TestResultText.Text = "已保存。";
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var dify = CurrentApp.Dify;
        if (dify == null) return;
        // Save first so the client reads the latest values.
        OnSaveConfigClick(sender, e);
        TestProgress.IsActive = true;
        TestResultText.Text = "测试中…";
        try
        {
            var ok = await dify.TestConnectionAsync();
            TestResultText.Text = ok ? "连接成功。" : "连接失败，请检查地址与 API Key。";
        }
        catch (Exception ex)
        {
            TestResultText.Text = $"连接失败：{ex.Message}";
        }
        finally
        {
            TestProgress.IsActive = false;
        }
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendAsync();

    private async Task SendAsync()
    {
        var dify = CurrentApp.Dify;
        if (dify == null) return;
        var query = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        InputBox.Text = "";
        _messages.Add(new DifyMessage { Role = "user", Text = query });
        var assistant = new DifyMessage { Role = "assistant", Text = "", IsStreaming = true };
        _messages.Add(assistant);

        SendButton.IsEnabled = false;
        SendProgress.IsActive = true;
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<DifyEvent>(ev =>
            {
                switch (ev.Type)
                {
                    case DifyEventType.Delta:
                        assistant.Text += ev.Text ?? "";
                        break;
                    case DifyEventType.Replace:
                        assistant.Text = ev.Text ?? "";
                        break;
                    case DifyEventType.End:
                        assistant.IsStreaming = false;
                        assistant.Citations = ev.Citations;
                        if (!string.IsNullOrEmpty(ev.ConversationId)) _conversationId = ev.ConversationId;
                        break;
                    case DifyEventType.Error:
                        assistant.IsStreaming = false;
                        assistant.Text = $"❌ {ev.Error ?? "Dify 错误"}";
                        break;
                }
            });
            await dify.StreamChatAsync(query, _conversationId, progress, _sendCts.Token);
        }
        catch (OperationCanceledException)
        {
            assistant.IsStreaming = false;
        }
        catch (Exception ex)
        {
            assistant.IsStreaming = false;
            assistant.Text = $"❌ {ex.Message}";
        }
        finally
        {
            assistant.IsStreaming = false;
            SendButton.IsEnabled = true;
            SendProgress.IsActive = false;
        }
    }
}

/// <summary>Observable chat message for the Dify conversation list.</summary>
public sealed class DifyMessage : INotifyPropertyChanged
{
    public string Role { get; set; } = "assistant";
    public string RoleLabel => Role == "user" ? "我" : "AI";

    private string _text = "";
    public string Text { get => _text; set { _text = value; Raise(nameof(Text)); } }

    private bool _isStreaming;
    public bool IsStreaming { get => _isStreaming; set { _isStreaming = value; Raise(nameof(IsStreaming)); Raise(nameof(StreamingVisibility)); } }

    public Microsoft.UI.Xaml.Visibility StreamingVisibility => IsStreaming ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private IReadOnlyList<DifyCitation>? _citations;
    public IReadOnlyList<DifyCitation>? Citations { get => _citations; set { _citations = value; Raise(nameof(Citations)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
