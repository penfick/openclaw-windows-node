namespace OpenClawTray.Pages;

/// <summary>
/// 精选 MCP 服务器目录（port 自 XClaw src/pages/Mcp/mcp-catalog.ts）。
/// 一键安装 = 往 gateway 的 mcp.servers 写一条 {transport:stdio, command:npx, args:[-y,Package]}。
/// </summary>
public sealed record McpCatalogEntry(
    string Name,
    string Description,
    string Package,
    string Category,
    string? AuthEnvKey);

public static class McpCatalog
{
    public static readonly IReadOnlyList<McpCatalogEntry> Entries = new[]
    {
        new McpCatalogEntry("Filesystem", "文件系统读写访问", "@modelcontextprotocol/server-filesystem", "开发", null),
        new McpCatalogEntry("GitHub", "GitHub 仓库 / Issue / PR 操作", "@modelcontextprotocol/server-github", "开发", "GITHUB_PERSONAL_ACCESS_TOKEN"),
        new McpCatalogEntry("SQLite", "SQLite 数据库查询", "@modelcontextprotocol/server-sqlite", "开发", null),
        new McpCatalogEntry("Puppeteer", "浏览器自动化与网页抓取", "@modelcontextprotocol/server-puppeteer", "浏览器", null),
        new McpCatalogEntry("Fetch", "网页内容抓取", "@modelcontextprotocol/server-fetch", "网络", null),
        new McpCatalogEntry("Brave Search", "Brave 搜索引擎", "@modelcontextprotocol/server-brave-search", "网络", "BRAVE_API_KEY"),
        new McpCatalogEntry("Google Maps", "Google 地图与定位", "@modelcontextprotocol/server-google-maps", "网络", "GOOGLE_MAPS_API_KEY"),
        new McpCatalogEntry("Memory", "知识图谱记忆库", "@modelcontextprotocol/server-memory", "知识", null),
        new McpCatalogEntry("Sequential Thinking", "分步推理思考", "@modelcontextprotocol/server-sequential-thinking", "知识", null),
    };
}
