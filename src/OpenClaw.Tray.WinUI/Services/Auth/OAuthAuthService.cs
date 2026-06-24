using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

/// <summary>
/// Corporate OA (OAuth 2.0 authorization-code) login. Ported from XClaw's
/// <c>electron/utils/user-auth.ts</c>. The access token gates the Company Skills
/// Hub (Bearer). Completely independent of gateway device pairing.
///
/// Callback transport: custom URI scheme <c>tclaw://oauth/callback</c>. The
/// corporate OA rejects http://localhost loopback redirects, so we register a
/// private scheme; the OS launches the app with the callback URL, and the
/// single-instance IPC forwards it to the running instance → HandleCallbackAsync.
/// </summary>
internal sealed class OAuthAuthService : IDisposable
{
    private const string OaBaseUrl = "http://172.20.200.61:8080/Report/PF";
    private const string ClientId = "xknn9sEiUnyRjH3u1mTYh6inWoxu5yQb";
    private const string ClientSecret = "dAkkMKWpdeM1QTS-CMqW6Sr9xxRpLQ6YjHKHFS_tLoQCIVkA";
    private const string Scope = "basic profile department role";
    private const string RedirectUri = "tclaw://oauth/callback";
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinRefreshDelay = TimeSpan.FromSeconds(60);

    private readonly SettingsManager _settings;
    private readonly AppState _appState;
    private readonly HttpClient _http;
    private Timer? _refreshTimer;
    private string? _pendingLoginState;
    private bool _disposed;

    internal OAuthAuthService(SettingsManager settings, AppState appState)
    {
        _settings = settings;
        _appState = appState;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Set by the host to marshal <see cref="AppState"/> writes onto the UI
    /// thread (AppState.SetField enforces UI-thread access). Null = call from
    /// any thread directly.
    /// </summary>
    public Action<Action>? UiEnqueue { get; set; }

    /// <summary>Last computed observable auth snapshot.</summary>
    public OaAuthState CurrentState { get; private set; } = new() { Authenticated = false };

    /// <summary>
    /// Opens the system browser at the OA authorize URL. Returns once the browser
    /// is launched; the flow completes in <see cref="HandleCallbackAsync"/> when the
    /// <c>tclaw://</c> callback URL arrives via single-instance IPC.
    /// </summary>
    public Task<bool> StartLoginAsync(CancellationToken cancellationToken = default)
    {
        var state = Guid.NewGuid().ToString("N");
        _pendingLoginState = state;
        var authorizeUrl = BuildAuthorizeUrl(state);

        try
        {
            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"[OAuth] Failed to open browser: {ex.Message}");
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    /// <summary>
    /// Called by the host when a <c>tclaw://oauth/callback?code=...&amp;state=...</c>
    /// URL arrives (forwarded by the single-instance IPC). Parses the code, exchanges
    /// it, fetches user info, persists, and pushes the new auth state.
    /// </summary>
    public async Task<bool> HandleCallbackAsync(string uri)
    {
        var query = ParseQuery(uri);
        var code = query.GetValueOrDefault("code");
        var state = query.GetValueOrDefault("state");
        var callbackError = query.GetValueOrDefault("error");

        if (!string.IsNullOrEmpty(callbackError))
        {
            Logger.Error($"[OAuth] Callback error: {callbackError}");
            PushState(false);
            return false;
        }
        if (string.IsNullOrEmpty(code))
        {
            Logger.Error("[OAuth] No code in callback");
            PushState(false);
            return false;
        }
        if (!string.IsNullOrEmpty(_pendingLoginState) && state != _pendingLoginState)
        {
            Logger.Warn($"[OAuth] state mismatch (expected {_pendingLoginState}, got {state})");
        }
        _pendingLoginState = null;

        return await CompleteLoginAsync(code);
    }

    /// <summary>Parses the query string of a callback URI into a dict (no System.Web dependency).</summary>
    private static Dictionary<string, string> ParseQuery(string uri)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = uri.IndexOf('?');
        var queryStr = q >= 0 ? uri[(q + 1)..] : uri;
        var hash = queryStr.IndexOf('#');
        if (hash >= 0) queryStr = queryStr[..hash];
        foreach (var pair in queryStr.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0)
                d[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            else
                d[pair] = "";
        }
        return d;
    }

    private async Task<bool> CompleteLoginAsync(string code)
    {
        try
        {
            var token = await ExchangeCodeAsync(code);
            var userInfo = await FetchUserInfoAsync(token.AccessToken);
            var expiresAtMs = CurrentUnixMs() + (token.ExpiresIn > 0 ? token.ExpiresIn : 3600) * 1000L;

            PersistSession(token.AccessToken, token.RefreshToken, expiresAtMs, userInfo);
            ScheduleRefresh(expiresAtMs);
            PushState(true, userInfo, expiresAtMs);
            Logger.Info($"[OAuth] Login successful: {userInfo.DisplayName ?? userInfo.Username}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[OAuth] CompleteLogin failed: {ex.Message}");
            PushState(false);
            return false;
        }
    }

    /// <summary>Restore session on app start: re-broadcast persisted state; refresh if expired.</summary>
    public async Task RestoreSessionAsync()
    {
        var token = _settings.OaAccessToken;
        if (string.IsNullOrEmpty(token))
        {
            PushState(false);
            return;
        }

        var expiresAtMs = _settings.OaTokenExpiresAtMs;
        var userInfo = _settings.OaUserInfo;
        if (expiresAtMs > 0 && CurrentUnixMs() >= expiresAtMs)
        {
            Logger.Info("[OAuth] persisted token expired, attempting refresh");
            if (!string.IsNullOrEmpty(_settings.OaRefreshToken))
            {
                PushState(true, userInfo, expiresAtMs);
                await RefreshAccessTokenAsync();
            }
            else
            {
                await LogoutAsync();
            }
            return;
        }

        PushState(true, userInfo, expiresAtMs);
        ScheduleRefresh(expiresAtMs);
    }

    /// <summary>Returns a valid access token, refreshing first if expired. For CompanySkillsHubClient.</summary>
    public async Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_settings.OaAccessToken))
            return null;
        if (_settings.OaTokenExpiresAtMs > 0 && CurrentUnixMs() >= _settings.OaTokenExpiresAtMs)
        {
            if (!await RefreshAccessTokenAsync(cancellationToken))
                return null;
        }
        return _settings.OaAccessToken;
    }

    /// <summary>Refresh the access token. Returns false (and clears session) on failure.</summary>
    private async Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = _settings.OaRefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
        {
            Logger.Warn("[OAuth] no refresh token, clearing session");
            await LogoutAsync();
            return false;
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
            };
            var resp = await _http.PostAsync($"{OaBaseUrl}/OAuthToken.ashx", new FormUrlEncodedContent(form), cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"[OAuth] refresh failed ({resp.StatusCode})");
                await LogoutAsync();
                return false;
            }
            var token = await ParseTokenResponseAsync(resp);
            var expiresAtMs = CurrentUnixMs() + (token.ExpiresIn > 0 ? token.ExpiresIn : 3600) * 1000L;
            PersistSession(token.AccessToken, token.RefreshToken ?? refreshToken, expiresAtMs, _settings.OaUserInfo);
            ScheduleRefresh(expiresAtMs);
            PushState(true, _settings.OaUserInfo, expiresAtMs);
            Logger.Info("[OAuth] token refreshed");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[OAuth] refresh error: {ex.Message}");
            await LogoutAsync();
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        var accessToken = _settings.OaAccessToken;
        if (!string.IsNullOrEmpty(accessToken))
        {
            try
            {
                var form = new Dictionary<string, string> { ["token"] = accessToken };
                await _http.PostAsync($"{OaBaseUrl}/Revoke.ashx", new FormUrlEncodedContent(form));
            }
            catch
            {
                // best-effort revoke; proceed with local cleanup
            }
        }

        _refreshTimer?.Dispose();
        _refreshTimer = null;

        _settings.OaAccessToken = "";
        _settings.OaRefreshToken = "";
        _settings.OaTokenExpiresAtMs = 0;
        _settings.OaUserInfo = null;
        _settings.Save();

        PushState(false);
        Logger.Info("[OAuth] logged out");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private string BuildAuthorizeUrl(string state)
    {
        // scope 用 '+' 连接（form-urlencoded 风格，对应 XClaw 的 URLSearchParams）。
        // 公司 OA 不接受 %20（Uri.EscapeDataString 的空格编码），点登录会无反应。
        var qs = $"response_type=code&client_id={Uri.EscapeDataString(ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
            + $"&scope={Scope.Replace(" ", "+")}"
            + $"&state={Uri.EscapeDataString(state)}";
        return $"{OaBaseUrl}/AuthorizationGrant.aspx?{qs}";
    }

    private async Task<OaTokenResponse> ExchangeCodeAsync(string code)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = RedirectUri,
        };
        var resp = await _http.PostAsync($"{OaBaseUrl}/OAuthToken.ashx", new FormUrlEncodedContent(form));
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync();
            throw new Exception($"Token exchange failed ({(int)resp.StatusCode}): {text}");
        }
        return await ParseTokenResponseAsync(resp);
    }

    private async Task<OaUserInfo> FetchUserInfoAsync(string accessToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{OaBaseUrl}/OAuthUserInfo.ashx");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync();
            throw new Exception($"UserInfo failed ({(int)resp.StatusCode}): {text}");
        }
        var json = await resp.Content.ReadAsStringAsync();
        return ParseUserInfo(json);
    }

    private static async Task<OaTokenResponse> ParseTokenResponseAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var n) ? (int)n : 0;
        return new OaTokenResponse(accessToken, refreshToken, expiresIn);
    }

    private static OaUserInfo ParseUserInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        List<string>? roles = null;
        if (root.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
        {
            roles = new List<string>();
            foreach (var r in rolesEl.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String) roles.Add(r.GetString() ?? "");
            }
        }

        return new OaUserInfo
        {
            UserId = Str("user_id") ?? Str("userid"),
            Username = Str("username"),
            DisplayName = Str("display_name") ?? Str("displayName") ?? Str("name"),
            Email = Str("email") ?? Str("mail"),
            DepartmentId = Str("department_id"),
            DepartmentName = Str("department_name"),
            Position = Str("position"),
            Roles = roles,
        };
    }

    private void PersistSession(string accessToken, string? refreshToken, long expiresAtMs, OaUserInfo? userInfo)
    {
        _settings.OaAccessToken = accessToken;
        _settings.OaRefreshToken = refreshToken ?? "";
        _settings.OaTokenExpiresAtMs = expiresAtMs;
        _settings.OaUserInfo = userInfo;
        _settings.Save();
    }

    private void ScheduleRefresh(long expiresAtMs)
    {
        _refreshTimer?.Dispose();
        var nowMs = CurrentUnixMs();
        var delayMs = expiresAtMs - nowMs - (long)RefreshLeadTime.TotalMilliseconds;
        if (delayMs < (long)MinRefreshDelay.TotalMilliseconds)
            delayMs = (long)MinRefreshDelay.TotalMilliseconds;
        _refreshTimer = new Timer(_ => _ = RefreshAccessTokenAsync(), null, delayMs, Timeout.Infinite);
    }

    private void PushState(bool authenticated, OaUserInfo? userInfo = null, long expiresAtMs = 0)
    {
        var state = new OaAuthState { Authenticated = authenticated, UserInfo = userInfo, TokenExpiresAtMs = expiresAtMs };
        CurrentState = state;
        if (UiEnqueue != null)
            UiEnqueue(() => _appState.AuthState = state);
        else
            _appState.AuthState = state;
    }

    private static long CurrentUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _http.Dispose();
    }

    private sealed record OaTokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn);
}
