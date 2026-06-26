using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// 天音助手聊天页。直接对接自建 Dify 知识库（SSE 流式），独立于 gateway。
/// 配置（服务地址 / API Key）在「天音设置」子页；本页只负责问答。
/// 聊天记录持久化到 dify-chat.json，导航离开再回来不丢。
/// </summary>
public sealed partial class DifyPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private static string ChatHistoryPath => Path.Combine(SettingsManager.SettingsDirectoryPath, "dify-chat.json");

    private readonly ObservableCollection<DifyMessage> _messages = new();
    private string? _conversationId;
    private CancellationTokenSource? _sendCts;
    private bool _historyLoaded;

    public DifyPage()
    {
        InitializeComponent();
        MessagesList.ItemsSource = _messages;
        Loaded += (_, _) => Initialize();
    }

    /// <summary>Called by HubWindow.InitializeCurrentPage on navigation.</summary>
    public void Initialize()
    {
        if (!_historyLoaded)
        {
            _historyLoaded = true;
            LoadHistory();
        }
        UpdateConfigState();
    }

    private void UpdateConfigState()
    {
        var s = CurrentApp.Settings;
        var configured = !string.IsNullOrEmpty(s.DifyBaseUrl) && !string.IsNullOrEmpty(s.DifyApiKey);
        NotConfiguredHint.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        InputBox.IsEnabled = configured;
        SendButton.IsEnabled = configured && !string.IsNullOrWhiteSpace(InputBox.Text);
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

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0) return;
        _messages.Clear();
        _conversationId = null;
        SaveHistory();
    }

    private void OnGoSettingsClick(object sender, RoutedEventArgs e)
    {
        try { ((IAppCommands)CurrentApp).Navigate("tianyin-settings"); }
        catch { /* navigate not available */ }
    }

    private async Task SendAsync()
    {
        var dify = CurrentApp.Dify;
        if (dify == null) return;
        if (!InputBox.IsEnabled) return;
        var query = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        InputBox.Text = "";
        _messages.Add(new DifyMessage { Role = "user", Text = query });
        var assistant = new DifyMessage { Role = "assistant", Text = "", IsStreaming = true };
        assistant.ThinkingExpanded = true; // live-expand reasoning while streaming
        _messages.Add(assistant);
        SaveHistory();

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
                        assistant.ThinkingExpanded = false; // collapse reasoning once finalized
                        assistant.Citations = ev.Citations;
                        if (!string.IsNullOrEmpty(ev.ConversationId)) _conversationId = ev.ConversationId;
                        break;
                    case DifyEventType.Error:
                        assistant.IsStreaming = false;
                        assistant.Text = $"❌ {ev.Error ?? "天音知识库错误"}";
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
            SendProgress.IsActive = false;
            UpdateConfigState();
            SaveHistory();
        }
    }

    // ── persistence ───────────────────────────────────────────────────

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(ChatHistoryPath)) return;
            var json = File.ReadAllText(ChatHistoryPath);
            var hist = JsonSerializer.Deserialize<DifyHistory>(json);
            if (hist?.Messages == null) return;
            _conversationId = hist.ConversationId;
            foreach (var m in hist.Messages)
                _messages.Add(new DifyMessage
                {
                    Role = string.IsNullOrEmpty(m.Role) ? "assistant" : m.Role,
                    Text = m.Text ?? "",
                    Citations = m.Citations,
                });
        }
        catch
        {
            // Corrupt history file — start fresh rather than crash.
        }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(SettingsManager.SettingsDirectoryPath);
            var hist = new DifyHistory(_conversationId,
                _messages.Select(m => new DifyHistoryEntry(m.Role, m.Text, m.Citations)).ToList());
            File.WriteAllText(ChatHistoryPath, JsonSerializer.Serialize(hist));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}

/// <summary>Observable chat message for the 天音助手 conversation list.</summary>
public sealed class DifyMessage : INotifyPropertyChanged
{
    public string Role { get; set; } = "assistant";

    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value; Raise(nameof(Text)); RecomputeSplit(); }
    }

    // Computed think/answer split (parsed from <think>...</think> blocks in Text).
    public string Thinking { get; private set; } = "";
    public string Answer { get; private set; } = "";
    public bool HasThinking => !string.IsNullOrWhiteSpace(Thinking);
    public Visibility ThinkingVisibility => HasThinking ? Visibility.Visible : Visibility.Collapsed;

    private bool _thinkingExpanded;
    public bool ThinkingExpanded
    {
        get => _thinkingExpanded;
        set { _thinkingExpanded = value; Raise(nameof(ThinkingExpanded)); Raise(nameof(ThinkingTextVisibility)); }
    }
    public Visibility ThinkingTextVisibility => HasThinking && _thinkingExpanded ? Visibility.Visible : Visibility.Collapsed;

    private void RecomputeSplit()
    {
        var (thinking, answer) = DifyThinkSplit.Split(_text);
        Thinking = thinking ?? "";
        Answer = answer ?? "";
        Raise(nameof(Thinking));
        Raise(nameof(Answer));
        Raise(nameof(HasThinking));
        Raise(nameof(ThinkingVisibility));
        Raise(nameof(ThinkingTextVisibility));
    }

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; Raise(nameof(IsStreaming)); Raise(nameof(StreamingVisibility)); }
    }
    public Visibility StreamingVisibility => IsStreaming ? Visibility.Visible : Visibility.Collapsed;

    private IReadOnlyList<DifyCitation>? _citations;
    public IReadOnlyList<DifyCitation>? Citations
    {
        get => _citations;
        set { _citations = value; Raise(nameof(Citations)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Splits a response into reasoning (&lt;think&gt;…&lt;/think&gt;) and answer.</summary>
internal static class DifyThinkSplit
{
    public static (string? thinking, string answer) Split(string content)
    {
        if (string.IsNullOrEmpty(content)) return (null, content);

        var thinkingParts = new List<string>();
        var answerParts = new List<string>();
        int i = 0;
        while (i < content.Length)
        {
            int open = content.IndexOf("<think", i, StringComparison.Ordinal);
            if (open < 0)
            {
                answerParts.Add(content[i..]);
                break;
            }
            if (open > i) answerParts.Add(content[i..open]);

            int gt = content.IndexOf('>', open);
            int bodyStart = gt >= 0 ? gt + 1 : open + "<think".Length;
            int close = content.IndexOf("</think>", bodyStart, StringComparison.Ordinal);
            if (close < 0)
            {
                // Streaming: <think> opened but </think> hasn't arrived yet — the remainder
                // is in-progress reasoning. Route it to the thinking block now (small font)
                // instead of leaving it in the answer, which is what made reasoning text look
                // oversized while streaming and snap to the right size once </think> landed.
                var partial = content[bodyStart..].Trim();
                if (!string.IsNullOrWhiteSpace(partial)) thinkingParts.Add(partial);
                break;
            }

            var inner = content[bodyStart..close].Trim();
            if (!string.IsNullOrWhiteSpace(inner)) thinkingParts.Add(inner);
            i = close + "</think>".Length;
        }

        var thinking = string.Join("\n\n", thinkingParts).Trim();
        var answer = string.Join("\n", answerParts).Trim();

        if (!string.IsNullOrEmpty(thinking) && !string.IsNullOrEmpty(answer))
            return (thinking, answer);
        if (!string.IsNullOrEmpty(thinking))
            return (thinking, ""); // only reasoning so far (mid-stream)
        return (null, content);
    }
}

/// <summary>Picks the user vs assistant bubble template by message role.</summary>
internal sealed class DifyMessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? UserMsgTemplate { get; set; }
    public DataTemplate? AssistantMsgTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is DifyMessage m && m.Role == "user" ? UserMsgTemplate : AssistantMsgTemplate;
}

// Persistence DTOs
internal sealed record DifyHistoryEntry(string Role, string Text, IReadOnlyList<DifyCitation>? Citations);
internal sealed record DifyHistory(string? ConversationId, List<DifyHistoryEntry> Messages);
