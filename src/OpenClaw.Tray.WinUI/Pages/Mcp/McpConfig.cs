using OpenClaw.Shared;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

/// <summary>openclaw 网关的 MCP 配置读写助手。
///
/// 关键事实（核对 openclaw 2026.6.5 源码 dist/mcp-config-BWapYmhD.js:70）：
///   <c>openclaw mcp list/set/show</c> 和聊天 native agent 读写的是 <b>mcp.servers</b>（顶层 mcp 对象），
///   形如 mcp.servers.&lt;name&gt; = { command, args?, env? }（stdio，**不带 transport**）
///   或 { transport:"sse"|"streamable-http", url }（http）。
///
/// 雷区：
///   - 把 transport:"stdio" 写进 mcp.servers 会被网关拒（allowed: sse, streamable-http）；stdio 省略 transport 即可。
///   - 在 server 对象里塞 null 字段（如 transport:null）也会被网关 schema 拒（leaf 必须是 string 或缺失），
///     所以这里**只写实际有的字段**，不做「先 null 再覆盖」。
///   - plugins.entries.acpx.config.mcpServers 是给 ACP 外部 agent 会话用的，且 acpx 未安装时被忽略——别往那里写。</summary>
internal static class McpConfig
{
    /// <summary>native agent 的 MCP 配置路径（stdio 与 http 共用，靠有无 transport 区分）。</summary>
    public const string ServersPath = "mcp.servers";

    /// <summary>旧版误写的 acpx 路径（仅用于一次性迁移）。</summary>
    public const string LegacyAcpxPath = "plugins.entries.acpx.config.mcpServers";

    /// <summary>沿点分路径下钻；任一段缺失返回 ValueKind=Undefined。</summary>
    public static JsonElement Walk(JsonElement root, string dotPath)
    {
        var cur = root;
        foreach (var seg in dotPath.Split('.'))
        {
            if (!cur.TryGetProperty(seg, out var next)) return default;
            cur = next;
        }
        return cur;
    }

    /// <summary>写入一条 MCP 服务器到 mcp.servers。server 直接写入（只含实际字段，不塞 null）；
    /// server=null 则整条删除（null 删键合法）。直接写 openclaw.json，gateway file-watch 后台热重载。</summary>
    public static async Task WriteAsync(IOperatorGatewayClient client, string name, JsonNode? server)
    {
        var node = ModelsPage.BuildNestedPatch(ServersPath, name, server);
        await ModelsPage.WritePatchAsync(client, node);
    }

    /// <summary>一次性迁移：旧版误写到 plugins.entries.acpx.config.mcpServers 的条目挪到 mcp.servers，
    /// 并清空旧路径。规范成 stdio（{command, args?, env?}，丢弃 transport，不带 null）。</summary>
    public static async Task<bool> MigrateLegacyAsync(IOperatorGatewayClient client, JsonElement acpxEl)
    {
        if (acpxEl.ValueKind != JsonValueKind.Object) return false;
        var servers = new JsonObject();
        var count = 0;
        foreach (var prop in acpxEl.EnumerateObject())
        {
            if (string.IsNullOrEmpty(prop.Name)) continue;
            var entry = new JsonObject();
            if (prop.Value.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
                entry["command"] = c.GetString();
            if (prop.Value.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                var arr = new JsonArray();
                foreach (var item in a.EnumerateArray()) arr.Add(item.GetString() ?? "");
                entry["args"] = arr;
            }
            if (prop.Value.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                var env = new JsonObject();
                foreach (var ep in e.EnumerateObject())
                    env[ep.Name] = ep.Value.ValueKind == JsonValueKind.String ? ep.Value.GetString() : ep.Value.GetRawText();
                entry["env"] = env;
            }
            servers[prop.Name] = entry;
            count++;
        }
        if (count == 0) return false;

        // 添到 mcp.servers + 整个移除旧版误建的 acpx 条目（acpx 未安装，留着只会刷 "not installed" 告警）
        var patch = new JsonObject
        {
            ["mcp"] = new JsonObject { ["servers"] = servers },
            ["plugins"] = new JsonObject
            {
                ["entries"] = new JsonObject { ["acpx"] = null }
            },
        };
        await ModelsPage.WritePatchAsync(client, patch);
        return true;
    }
}
