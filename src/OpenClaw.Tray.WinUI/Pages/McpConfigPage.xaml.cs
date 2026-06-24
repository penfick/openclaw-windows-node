using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Pages;

/// <summary>MCP 服务器页（Pivot host）：我的服务器 + 市场。</summary>
public sealed partial class McpConfigPage : Page
{
    private bool _myServersInited;

    public McpConfigPage()
    {
        InitializeComponent();
        // 市场装完 → 立即刷新「我的服务器」，避免装完要切 tab 才看到
        Marketplace.InstalledChanged += () => MyServers.Initialize();
    }

    /// <summary>由 HubWindow.InitializeCurrentPage 在导航到本页时调用。</summary>
    public void Initialize()
    {
        if (!_myServersInited)
        {
            _myServersInited = true;
            MyServers.Initialize();
        }
    }

    /// <summary>切到「市场」时刷新已装状态（在「我的服务器」增删后保持同步）。</summary>
    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedIndex == 1)
            _ = Marketplace.LoadInstalledAndRenderCatalogAsync();
        else if (Tabs.SelectedIndex == 0 && _myServersInited)
            MyServers.Initialize();
    }
}
