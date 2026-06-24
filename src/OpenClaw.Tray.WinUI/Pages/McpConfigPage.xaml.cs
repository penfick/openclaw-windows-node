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
}
