using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

// ── 模型 ─────────────────────────────────────────────────────────────

public enum DifyEventType { Delta, Replace, End, Error }

public sealed record DifyCitation(string DocumentName, string Content, double Score, int Position);

public sealed record DifyEvent(
    DifyEventType Type,
    string? Text = null,
    string? ConversationId = null,
    IReadOnlyList<DifyCitation>? Citations = null,
    string? Error = null);

// ── 客户端 ───────────────────────────────────────────────────────────

/// <summary>
/// Streaming chat client for a self-hosted/private Dify knowledge-base app.
/// Ported from XClaw's <c>electron/main/ipc-handlers.ts</c> dify:sendMessage
/// SSE state machine. Reads baseUrl/apiKey from <see cref="SettingsManager"/>
/// (apiKey DPAPI-protected on disk).
/// </summary>
internal sealed class DifyClient : IDisposable
{
    private const string DefaultUser = "openclaw-user";
    private readonly SettingsManager _settings;
    private readonly HttpClient _http;
    private bool _disposed;

    internal DifyClient(SettingsManager settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    private string ResolveChatUrl()
    {
        var baseUrl = (_settings.DifyBaseUrl ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("未配置天音知识库服务地址，请在「天音设置」中填写。");
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/chat-messages"
            : $"{baseUrl}/v1/chat-messages";
    }

    private void EnsureApiKey()
    {
        if (string.IsNullOrEmpty(_settings.DifyApiKey))
            throw new InvalidOperationException("未配置天音知识库 API Key。");
    }

    /// <summary>Stream a chat message, raising progress events as SSE arrives.</summary>
    public async Task StreamChatAsync(string query, string? conversationId, IProgress<DifyEvent> progress, CancellationToken ct = default)
    {
        EnsureApiKey();
        var url = ResolveChatUrl();

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.DifyApiKey);
        req.Content = JsonContent.Create(new
        {
            query,
            inputs = new { },
            response_mode = "streaming",
            user = DefaultUser,
            conversation_id = conversationId,
            auto_generate_name = true,
        });

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"天音知识库请求失败 ({(int)resp.StatusCode}): {text}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break; // stream ended
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var json = line["data: ".Length..];
            var evt = TryParseEvent(json);
            if (evt != null) progress.Report(evt);
            if (evt?.Type == DifyEventType.Error) break;
        }
    }

    /// <summary>
    /// Fast auth/reachability probe. Uses <c>response_mode=streaming</c> and reads only the
    /// response headers: a Dify chat-messages request returns 200 + headers as soon as the
    /// stream opens (before any LLM work), so this completes in ~RTT instead of waiting for a
    /// full answer. A bad key returns 401 just as fast. Fails after 15s if the host is down.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        EnsureApiKey();
        var url = ResolveChatUrl();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.DifyApiKey);
        req.Content = JsonContent.Create(new
        {
            query = "hi",
            inputs = new { },
            response_mode = "streaming",
            user = DefaultUser,
        });
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // timed out waiting for headers — host unreachable / too slow
        }
    }

    private static DifyEvent? TryParseEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ev = root.TryGetProperty("event", out var eEl) ? eEl.GetString() : null;

            switch (ev)
            {
                case "message":
                    return new DifyEvent(DifyEventType.Delta,
                        Text: root.TryGetProperty("answer", out var aEl) ? aEl.GetString() : null);
                case "message_replace":
                    return new DifyEvent(DifyEventType.Replace,
                        Text: root.TryGetProperty("answer", out var rEl) ? rEl.GetString() : null);
                case "message_end":
                    {
                        var cid = root.TryGetProperty("conversation_id", out var cEl) ? cEl.GetString() : null;
                        var cites = new List<DifyCitation>();
                        if (root.TryGetProperty("metadata", out var meta)
                            && meta.TryGetProperty("retriever_resources", out var rr)
                            && rr.ValueKind == JsonValueKind.Array)
                        {
                            var pos = 0;
                            foreach (var c in rr.EnumerateArray())
                            {
                                cites.Add(new DifyCitation(
                                    DocumentName: c.TryGetProperty("document_name", out var dn) ? dn.GetString() ?? "" : "",
                                    Content: c.TryGetProperty("content", out var ctEl) ? ctEl.GetString() ?? "" : "",
                                    Score: c.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var d) ? d : 0,
                                    Position: c.TryGetProperty("position", out var pEl) && pEl.TryGetInt32(out var p2) ? p2 : pos));
                                pos++;
                            }
                        }
                        return new DifyEvent(DifyEventType.End, ConversationId: cid, Citations: cites);
                    }
                case "error":
                    return new DifyEvent(DifyEventType.Error,
                        Error: root.TryGetProperty("message", out var mEl) ? mEl.GetString() ?? "天音知识库错误" : "天音知识库错误");
                default:
                    return null; // ping, workflow, tts etc.
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
