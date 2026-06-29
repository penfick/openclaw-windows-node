# Mac 端企业二开交接文档

> 给在 macOS 上新开 Claude Code 会话的 agent 看。本会话（Windows 侧）已积累完整上下文，
> 本文档把**与 OS 无关的 80% 领域知识**（网关协议、配置约定、所有踩过的坑、企业功能规格）沉淀下来，
> 让 Mac 侧不用从头理解。Mac 侧只需要在这些知识之上做 **Swift UI 实现**。

---

## 0. 一句话目标

基于官方 openclaw **Mac 菜单栏 app**（Swift，在主仓 `apps/macos/`）做企业二开，
**对标 Windows 端（`penfick/openclaw-windows-node`，C#/WinUI）已实现的企业功能**：
模型管理、技能（已装/公司市场/公共市场）、MCP 配置/市场、OA 集成、Dify 等。
两边代码零复用（Swift vs C#），但**网关协议和配置约定 1:1 相同**——这才是本文档保住的价值。

---

## 1. 仓库与起点

- **主仓**：`https://github.com/openclaw/openclaw`
- **Mac app 源码**：`apps/macos/`（SwiftUI 菜单栏 app）
  - 关键文件：`apps/macos/Sources/OpenClaw/AppState.swift`
  - dev 跑：仓库根目录 `scripts/restart-mac.sh`
  - README：`apps/macos/README.md`
- **官方 Mac 文档**：`https://docs.openclaw.ai/platforms/macos`、`/platforms/mac/menu-bar`、`/platforms/mac/dev-setup`
- **构建**：Xcode 26.2+，只能在 macOS 上编译/运行/调试。

> 第一步：先读 `apps/macos/` 现有结构，搞清它现在怎么连网关、怎么组织 UI（菜单栏 + 弹出面板），
> 再决定企业功能页放哪。不要假设它的架构跟 Windows 一样。

---

## 2. 网关架构与协议（OS 无关，跨平台 1:1 复用）

### 网关是什么
- openclaw 网关是一个 **Node.js 长驻进程**，跑在用户机器上，提供 WebSocket RPC。
- **Mac 上原生跑**（LaunchAgent，`openclaw onboard --install-daemon` / `openclaw gateway install`）。**没有 WSL**。
- 装：`curl -fsSL https://openclaw.ai/install.sh | bash`（Mac/Linux 通用脚本）。

### 连接
- 地址：`ws://localhost:<port>/ws`，默认端口 **18789**。
- 认证：一个**网关 token**（setup 时生成，存于设备身份目录）。
- 握手：网关发 `connect.challenge`（带 nonce）→ 客户端用 token 签名 → 发 `connect` 请求
  （`minProtocol:4, maxProtocol:4`，role/scopes）→ 配对。
- 首次连接可能要求**设备配对审批**：`openclaw devices approve <request-id>`（Mac app setup 一般自动做）。

### 我们用到的 RPC 方法
| 方法 | 用途 |
|---|---|
| `config.get` | 读整份配置（返回里有 `config`/`baseHash`/`hash`/`raw`） |
| `config.patch` | **写配置的唯一正确方式**（见下黄金法则） |
| `skills.search` | ClawHub 技能搜索（公共/公司市场，可搜到=可装） |
| `skills.detail` | 技能详情（slug → owner/version/tags） |
| `skills.install` | 装技能（`{source:"clawhub", slug}`） |
| `skills.status` | 已装技能状态 |
| `skills.upload.begin` / `chunk` / `commit` | 上传本地技能到公司市场（分块） |

> 不要用 `config.set`——本网关版本会拒（见下）。

---

## 3. 配置写入黄金法则（全部是踩坑换来的，务必遵守）

> ⚠️ **更新（2026-06）**：本节写于性能优化之前。现在 Windows 端**UI 的配置读写已改为直接读写
> `~/.openclaw/openclaw.json` 文件**（网关 file-watch + 后台异步 reload，瞬时返回，绕开 config.patch
> 的同步 ~4.5s reload 和 config.get 的 ~0.5-1.2s）。**Mac 端 UI 也应优先直读直写文件**。
> 下面的 config.patch 法则仍有效（语义、`null` 删键、baseHash 都对），但现在只是**备选/强一致场景**用，
> 不再是"唯一正确方式"。完整说明见 **[`mac-port-recent-updates.md`](./mac-port-recent-updates.md) 第 1 节**。

### 规则 1：用 `config.patch`，不要用 `config.set`
- `config.set {path, value}` 会被网关拒：`invalid config.set params: must have required property 'raw'`。
- 正确：`config.patch { raw: <JSON merge patch 字符串>, baseHash: <from config.get> }`。

### 规则 2：config.patch 是 RFC 7396 JSON Merge Patch
- 对象**递归合并**；
- `null` = **删键**；
- 缺省的键**保留**（不会清掉）；
- 改数组要整体替换（或用 replacePaths，简单场景直接整体写）。

### 规则 3：叶子字段不能塞 null（会被 schema 拒，整 patch 回滚）
- 错误示例：写 stdio MCP 时塞 `{command, args, transport:null, url:null}` → 网关校验失败 → 整个 patch 回滚 → 看起来"没反应"。
- 正确：**只写实际有的字段**。stdio MCP 就只写 `{command, args, env?}`，不要画蛇添足补 null。
- （整键 null 删键是合法的，比如删一个 MCP 条目 `mcp.servers.<name>: null`；被拒的是**对象内部的叶子 null**。）

### 规则 4：config.patch 有限流（每分钟配额）
- 多个相关改动**合并成 ONE patch**（一次 config.patch），别连发。
- 报错 `rate limit exceeded for config.patch; retry after Ns` 是网关侧限流，等 N 秒。

### 规则 5：先拿 baseHash 再 patch
```
GET config.get → 取 baseHash（优先 baseHash，其次 hash，都没有就 SHA256(raw 字符串)）
→ config.patch { raw, baseHash }
```
带 baseHash 是乐观锁；不带也能写但风险高。

### 规则 6：配置路径跟「网关版本」走，不跟 OS 走
- 当前钉版 **2026.6.5**。路径表见下节。
- 升版本时路径可能变（例如老版 MCP 在 `plugins.entries.acpx.config.mcpServers`，新版在 `mcp.servers`）。
  升级后要重新核对，改路径常量即可——这是轻量维护，不是重写。

---

## 4. 关键配置路径表（网关 2026.6.5）

### 模型
- Provider：`models.providers.<id>` = `{ api, baseUrl, apiKey, models: [{id, name}, ...] }`
- 默认模型：`agents.defaults.model.primary` = `"<provider>/<modelId>"`（字符串，带斜杠）
- 允许清单：`agents.defaults.models."<provider>/<modelId>"` = `{}`（空对象；键名含斜杠）
  - 删除某模型：`agents.defaults.models."<provider>/<modelId>": null`

### MCP（重点，这块踩坑最多）
- **正确路径：`mcp.servers.<name>`**
- stdio：`{ command, args?, env? }` —— **不带 transport 字段**（stdio 就是省略 transport）
- http：`{ transport: "sse" | "streamable-http", url }`
- **错误路径：`plugins.entries.acpx.config.mcpServers`** —— 那是给 ACP 外部 agent 会话（Codex/Claude Code）注入 MCP 用的，且 acpx 未安装时被忽略。别往那写。
- **验证**：`openclaw mcp list` 读的就是 `mcp.servers`（核对过 openclaw 源码 `dist/mcp-config-BWapYmhD.js:70` → `sourceConfig.mcp?.servers`）。

### 技能
- 允许上传归档安装：`skills.install.allowUploadedArchives` = `true`（上传本地技能前必须开）

---

## 5. 企业功能规格（对标 Windows 端实现）

> Windows 端 C# 实现在 `penfick/openclaw-windows-node` 的 `src/OpenClaw.Tray.WinUI/Pages/`，
> 目录 `Models/`、`Skills/`、`Mcp/`。逻辑可对标，UI 用 Swift 重写。

### 5.1 模型管理（对标 `Pages/Models/`）
- **默认模型**：级联选择（先选 Provider → 再选该 Provider 的 Model），不是一个大下拉。
- **Provider 卡片列表**：展示已配置 provider（api 类型、baseUrl、模型列表），可编辑。
- **添加 Provider 弹框**：
  - 步骤 1：内置 Provider 选择网格（anthropic/openai/google/openrouter/moonshot/deepseek/zai/qwen/ollama/vllm/custom 等，带 icon）；
  - 步骤 2：填凭据（名称/BaseURL/APIKey/ModelID），内置 provider 预填 BaseURL/Model；可点"获取可用模型"拉模型列表；
  - 确认：单次 config.patch 同时写 provider + allowlist +（首个 provider 时）设默认。
- 内置目录源：`Pages/Models/ProviderCatalog.cs`（Windows，14 个 provider 定义，可平移）。

### 5.2 技能（对标 `Pages/Skills/`）
- 三 tab：**已安装 / 公司市场 / 公共市场**。
- **卡片网格**展示（名称/作者/版本/摘要），统一卡片宽度。
- **公共市场**：走网关 `skills.search`（ClawHub，搜得到就能装），默认关键词 browse + 客户端分页；**不要**用 skills.sh 的 /api/search（搜得到但装不上）。
- **安装**：`skills.install {source:"clawhub", slug}`；装完**重新读 config**（权威判定已装），即时刷新已装/公司市场状态。
- **已装检测**：按 slug 在 skills.status 结果里匹配（注意 source 字段不能区分来源，有边缘误匹配是固有限制）。
- **上传到公司市场**：metadata 表单（slug/name/desc/version）→ 打包本地技能目录成 zip → `skills.upload.begin/chunk/commit`。
  - 上传需带 OA UserInfo（authorId/displayName/deptId/deptName）。
  - **打包**：Mac 上用 Foundation 的 ZIP（`NSFileCoordinator`+`zip` 命令，或 AppleArchive/Compression）——**不要**像 Windows 那样绕 wsl.exe（Mac 没 WSL，也没那个 UNC 问题）。
- **详情页**：`skills.detail {slug}` → owner/latestVersion/tags；三构造函数支持 ClawHub 拉取 / 本地元数据 / 先 ClawHub 后降级本地。

### 5.3 MCP（对标 `Pages/Mcp/`）
- **我的服务器 tab**：卡片网格展示已配置（`mcp.servers`）；"＋添加"按钮弹框增删改（名称/类型/命令/参数/URL/env）。
  - 编辑时**锁定类型**（merge-patch 没法干净替换子对象，切类型=删了重加）。
  - 删除有二次确认。
- **市场 tab**：npm registry 搜索 + 精选 catalog + 一键安装（写 `mcp.servers.<name> = {command:"npx", args:[...]}`）。
  - 点安装**即时反馈**：卡片变"正在安装…"并禁用 → 装完重读 config 标"已安装"。
  - 装完**刷新"我的服务器"**（跨控件事件）。
- MCP 配置助手逻辑（CleanEntry、迁移）见 Windows `Pages/Mcp/McpConfig.cs`。

### 5.4 OA 集成 / Dify
- OA：用于技能上传的作者身份（UserInfo）。具体接口/字段以 Windows 实现为准（`Pages/` 下搜 OA/CompanySkills）。
- Dify：Windows 提交里含 Dify，具体以 Windows 代码为准。
- ⚠️ 这两块本会话细节较少，**Mac 实现前先读 Windows 对应代码确认规格**。

---

## 6. WSL 相关——Mac 没有，别踩

- Windows 端有些代码绕 WSL（技能上传打包用 `wsl.exe+python3`、保活 `wsl -- sleep infinity`、UNC 读不到等）。
- **Mac 全部不需要**：原生 Unix，文件直接读，进程直接起。
- 如果照搬 Windows 逻辑遇到 wsl.exe / `\\wsl.localhost` / `/mnt/c` —— 那是 Windows 专属 workaround，Mac 用原生等价物替换。

---

## 7. 样式 / UX 定下来的约定（保持一致）

- 卡片网格（统一宽度，市场/已装一致）。
- 增删改走**弹框**，不内联表单。
- 操作有**即时反馈**（按钮变"正在…"/禁用，状态栏文字）。
- 错误用 `FriendlyConfigError` 把网关限流/报错翻成人话（"网关配置写入限流，请等待 N 秒后重试"）。
- 按钮字体/padding 统一（FontSize 12 量级）。

---

## 8. 验证方法

```bash
openclaw --version          # 确认 2026.6.5
openclaw doctor             # 配置体检
openclaw gateway status     # 网关在跑
openclaw mcp list           # MCP 配置读 mcp.servers（装了应该列出）
openclaw config get         # 看整份配置
```
功能验证：聊天里让 agent 实际调用装好的 MCP/技能（光看 list 不够，要真用）。

---

## 9. 给 Mac agent 的开工建议

1. `git clone https://github.com/openclaw/openclaw`，进 `apps/macos/`，跑通官方 app（`scripts/restart-mac.sh`）。
2. 读 `apps/macos/Sources/OpenClaw/AppState.swift` 等，搞清现有网关连接、UI 组织。
3. 通读本文档第 2-4 节（协议 + 配置黄金法则）——这是不可商量的红线。
4. 按第 5 节功能规格，逐个对标 Windows `penfick/openclaw-windows-node` 的 C# 实现，用 Swift 重写。
5. 每个功能做完，用第 8 节方法在聊天里实测（不只是 UI 显示对，要真能用）。
6. 遇到网关行为不符合预期，优先查 openclaw 源码（`~/.openclaw/tools/node-v*/lib/node_modules/openclaw/dist/`）确认版本对应的真实约定。

---

## 附：Windows 侧关键文件索引（对标用）

| 功能 | Windows 文件 |
|---|---|
| 配置 patch 助手 | `Pages/ModelsPage.xaml.cs`（ReadBaseHashAsync/WritePatchAsync/BuildNestedPatch/FriendlyConfigError，internal static） |
| MCP 配置助手 | `Pages/Mcp/McpConfig.cs`（mcp.servers 路径、CleanEntry、迁移） |
| MCP 弹框 | `Pages/Mcp/McpServerDialog.xaml.cs` |
| 技能市场客户端 | `Pages/Skills/SkillHubClient.cs`（gateway skills.search） |
| 技能上传 | `Pages/Skills/SkillUploader.cs`（Windows 用 wsl.exe，Mac 换原生 zip） |
| Provider 目录 | `Pages/Models/ProviderCatalog.cs` |
| 添加 Provider 弹框 | `Pages/Models/AddProviderDialog.xaml.cs` |
