using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Services;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// Corporate OA (OAuth) account entry. Drives <see cref="OAuthAuthService"/> and
/// reflects <see cref="AppState.AuthState"/>. Text is hardcoded zh-CN for now
/// (enterprise-internal feature); migrate to .resw when localization is needed.
/// </summary>
public sealed partial class AccountPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;

    public AccountPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Initialize();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appState != null)
        {
            _appState.PropertyChanged -= OnAppStateChanged;
            _appState = null;
        }
    }

    public void Initialize()
    {
        var appState = CurrentApp.AppState!;
        if (!ReferenceEquals(_appState, appState))
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
            _appState = appState;
            _appState.PropertyChanged += OnAppStateChanged;
        }
        RefreshAuthUi();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.AuthState))
            RefreshAuthUi();
    }

    private void RefreshAuthUi()
    {
        var auth = CurrentApp.AppState?.AuthState;
        if (auth?.Authenticated == true && auth.UserInfo != null)
        {
            LoggedOutCard.Visibility = Visibility.Collapsed;
            LoggedInCard.Visibility = Visibility.Visible;
            var u = auth.UserInfo;
            DisplayNameText.Text = u.DisplayName ?? u.Username ?? "—";
            UsernameText.Text = u.Username ?? "—";
            DepartmentText.Text = string.IsNullOrEmpty(u.DepartmentName) ? "—" : u.DepartmentName;
            PositionText.Text = u.Position ?? "—";
            EmailText.Text = u.Email ?? "—";
            LoginMessageText.Text = string.Empty;
        }
        else
        {
            LoggedOutCard.Visibility = Visibility.Visible;
            LoggedInCard.Visibility = Visibility.Collapsed;
        }
    }

    private void OnLoginClick(object sender, RoutedEventArgs e) => _ = LoginAsync();

    private async Task LoginAsync()
    {
        var svc = CurrentApp.OAuthAuth;
        if (svc == null) return;

        LoginButton.IsEnabled = false;
        LoginProgress.IsActive = true;
        LoginMessageText.Text = "请在弹出的浏览器中完成登录…";
        try
        {
            var ok = await svc.StartLoginAsync();
            if (!ok)
                LoginMessageText.Text = "登录未完成或已取消。";
        }
        catch (Exception ex)
        {
            LoginMessageText.Text = $"登录失败：{ex.Message}";
        }
        finally
        {
            LoginProgress.IsActive = false;
            LoginButton.IsEnabled = true;
        }
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e) => _ = CurrentApp.OAuthAuth?.LogoutAsync();
}
