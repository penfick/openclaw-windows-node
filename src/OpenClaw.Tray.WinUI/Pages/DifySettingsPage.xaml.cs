using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Services;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>
/// 天音知识库设置页：服务地址 + API Key。从 DifyPage 抽出来的配置区，
/// 让聊天页只管问答。配置仍走 <see cref="SettingsManager"/>（本地 DPAPI 保护）。
/// </summary>
public sealed partial class DifySettingsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;

    public DifySettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFields();
    }

    /// <summary>Called by HubWindow.InitializeCurrentPage on navigation.</summary>
    public void LoadFields()
    {
        var settings = CurrentApp.Settings;
        BaseUrlBox.Text = settings.DifyBaseUrl;
        ApiKeyBox.Password = settings.DifyApiKey;
        StatusText.Text = string.IsNullOrEmpty(settings.DifyApiKey) ? "未配置" : "已配置";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var settings = CurrentApp.Settings;
        settings.DifyBaseUrl = BaseUrlBox.Text.Trim();
        settings.DifyApiKey = ApiKeyBox.Password;
        settings.Save();
        StatusText.Text = "已保存。";
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var dify = CurrentApp.Dify;
        if (dify == null) return;
        // Save first so the client reads the latest values.
        OnSaveClick(sender, e);
        TestProgress.IsActive = true;
        StatusText.Text = "测试中…";
        try
        {
            var ok = await dify.TestConnectionAsync();
            StatusText.Text = ok ? "连接成功。" : "连接失败，请检查地址与 API Key。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"连接失败：{ex.Message}";
        }
        finally
        {
            TestProgress.IsActive = false;
        }
    }
}
