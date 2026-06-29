# Windows 端近期改动 → Mac 二开指导

> 给 Mac 端二开看。`mac-port-handoff.md` 写在以下改动**之前**，其中「config.patch 是写配置的唯一正确方式」的黄金法则
> **已被推翻**——本文档是补充与纠正。Mac 用的是同一个 Node 网关，这些 OS 无关的结论 1:1 适用。
> 对应仓库：`penfick/openclaw-windows-node`（C#/WinUI）。Mac 侧代码零复用（Swift），但**结论照搬**。

---

## TL;DR（最重要的一条）

**别用 `config.patch` / `config.get` RPC 做 UI 的配置读写——直接读写 `~/.openclaw/openclaw.json` 文件。**
网关用 chokidar **file-watch** 这个文件，外部写入后 ~1s 内检测并**后台异步热重载**。
所以 UI 直读直写文件可以**瞬时返回**，不被网关同步 reload（写 ~4.5s、读 ~0.5-1.2s）卡住。
这是 Windows 端模型页/MCP 页从"明显卡"到"秒开"的关键，Mac 端照做即可。

---

## 1. 直读 / 直写 openclaw.json（绕开 config.patch / config.get RPC）

### 背景：为什么 RPC 慢
- `config.patch`：网关把「写配置 + 同步热重载」打包在一次 RPC 里。patch 要等 reload 完成（重初始化受影响子系统）才返回：
  - 切默认模型这类轻量变更：reload 几十毫秒。
  - **新增/改 provider（带 apiKey）或 MCP 服务器**：reload ~1-4.5s。RPC 阻塞 UI。
- `config.get`：读配置也要 ~0.5-1.2s，且在网关 reload 期间会排队 → 曾观测到 7s 尖峰。
- `models.list view:"all"`：缓存失效时网关**逐个 provider 调远端 /models API 重枚举** → 11-13s。

### 关键发现：网关 file-watch openclaw.json（实测 + 源码核实）
- 源码：openclaw dist `server-reload-handlers-d99vbE44.js`，`chokidar.watch(opts.watchPath, { awaitWriteFinish:{stabilityThreshold:200} })`，监听 add/change/unlink。
- 默认开：`gateway.reload.mode = "hot"`。
- 外部写文件 → ~1s 内 `config change detected; evaluating reload` → 按受影响子系统后台异步 `hot reload applied`。
- **writer-intent 机制**：config.patch 内部写带「已 inline reload」intent → watcher 跳过（防双重重载）；**外部直写无 intent → 正常触发 reload**。所以直写完全可行。
- 校验：reload 时做 schema 校验，**未知 key 会被拒**（`config reload skipped (invalid config)`）→ 只能写合法字段。

### Windows 端怎么做的（照搬的范本）
新建 `Services/OpenClawConfigFile.cs`（Mac 侧 Swift 等价：一个 `OpenClawConfigFile` 帮手）：

**写**（替代 config.patch）—— `MergePatchAsync(patch)`：
1. 读最新 `~/.openclaw/openclaw.json` → JsonObject。
2. **RFC 7396 合并**：对象递归合并、标量/数组替换、**`null` 删键**、缺失键不变。（和 config.patch 语义完全一致，所以原来构造 patch 的代码原样复用。）
3. **原子写**：写 `<path>.tmp-<guid>` → `File.Move(overwrite:true)`（NTFS 同卷原子；Mac 用 `rename(2)` 同样原子）。防与网关自身写并发撞。
4. IOException 重试几次（网关也在写这个文件）。
5. 写完立即返回 UI，不等 reload。

**读**（替代 config.get）—— `ReadRootElementAsync()`：
1. `FileShare.ReadWrite | Delete` 打开（允许网关持锁并发读）。
2. 解析成 JsonElement / 克隆根（`JsonDocument.Parse` → `RootElement.Clone()`，离开 using 后仍可用）。
3. 直接返回**配置根**（注意：config.get RPC 把配置包在 `"config"` 字段下；文件本身就是根，无需 unwrap）。

**models.list**（10s+ 的重枚举）：
- Models 页卡片**只依赖 config 的 allowlist**（`agents.defaults.models`），**不需要 models.list**。
- 所以：卡片先用 config 直读渲染（瞬时），`models.list view:"all"` 挪到**后台** fire-and-forget，回来再合并到 provider 目录（给"添加模型"选择器用），合并在 UI 线程。

### Mac 端指导
- 同样建一个直读直写 `~/.openclaw/openclaw.json` 的 Swift 帮手（Foundation `FileManager` + `JSONSerialization` / `Codable`）。
- 写用 `FileManager` 写临时文件 + `replaceItem(at:)`（原子）。
- 合并语义照 RFC 7396（`NSJSONSerialization` 拿到 `NSMutableDictionary` 后递归 merge：dict 合并、`NSNull` 删键、其它替换）。
- 模型页/MCP 页的加载和保存都走文件，**不调 `config.get`/`config.patch`**。
- 慢的 `models.list` 放后台，UI 先用 config 渲染。
- Mac 配置路径同样 `~/.openclaw/openclaw.json`（网关默认）。

### ⚠️ 纠正 `mac-port-handoff.md` 的黄金法则
旧文档说「config.patch 是写配置的唯一正确方式」——**过时**。现在：**UI 配置读写首选直读直写文件**（快）；config.patch 仅在需要网关**强一致校验/原子提交**的少数场景才用。两者写出的 patch 语义（RFC 7396）相同，可以无缝切换。

---

## 2. GatewaySupervisor（网关自愈）

### 问题
Windows 端网关跑成 Scheduled Task，**无重启策略**（`RestartCount=0`）。网关进程退出（config hot-reload 后偶发干净退出、崩溃等）后，没人拉起 → 所有重连失败，直到手动 `openclaw gateway start`。

### Windows 端方案
`Services/GatewaySupervisor.cs`：后台 `PeriodicTimer` 每 20s TCP 探活 `127.0.0.1:<port>`；探不到就 `openclaw gateway start` 拉起 + 重连 operator client。关键防坑：
- **`openclaw gateway start` 会杀掉在跑的网关重启**——所以不能在网关**启动中**（端口未 bind）就 start，否则 boot-loop。用双阈值：首次死亡只等 10s grace；start 后等满 90s boot 窗口才允许再 start。
- 探针把 `ws://localhost:port` 的 `localhost` 归一化成 `127.0.0.1`（网关只 bind IPv4 loopback；`ConnectAsync("localhost")` 先解析 IPv6 `::1` 会假阴性 → 误杀健康网关）。
- 用户手动 Stop 时 `NotifyUserStopped()` 暂停监督、Start 时恢复。

### Mac 端指导
- **Mac 网关跑成 LaunchAgent**，自带 `KeepAlive`（崩溃/退出自动拉起）——**大概率不需要自己写 supervisor**，配好 LaunchAgent 的 `KeepAlive=true` + `ThrottleInterval` 即可。
- 如果 LaunchAgent 不够（比如要探活 + 自动重连 WS），再照 Windows 的双阈值 + IPv4 探针思路写一个轻量监督。
- 探针同样注意 IPv4 归一化。

---

## 3. 天音助手（DifyPage）重设计

自建 Dify 知识库的聊天页。原版很糙（inline 配置、"向 Dify 知识库提问"、消息全左无头像、think 和答案混在一起、导航丢记录）。重设计后：

### 拆分
- **配置独立子页**：服务地址 + API key 从聊天页挪到「天音 → 天音设置」子导航项。聊天页只管问答。
- **去品牌**：所有可见文案的 "Dify" → "天音知识库"（代码层名字 `DifyPage`/`DifyClient`/IPC `dify:*` 保留，只改用户可见字符串）。
- **聊天 UI**：用户靠右（主色气泡 + 头像）/ AI 靠左（卡片气泡 + 头像）；`<think>…</think>` 解析成可折叠"思考过程"块（**流式时也实时分离**——关键：未闭合的 `<think>` 要当成进行中推理，否则流式期间 think 显示成大字答案、结束才变小）。
- **持久化**：消息 + conversationId 存本地 JSON（`dify-chat.json`），导航离开再回来不丢。
- **清空按钮**。

### Mac 端指导
- Dify 集成是**独立于网关**的：直接 SSE 调 Dify `/v1/chat-messages`（不走 openclaw 网关）。配置存 Mac 的 secure storage（Keychain）。
- think 分离的正则逻辑（`<think>…</think>`，处理未闭合标签）跨平台照搬。
- 流式 + 持久化的 UX 模式照搬。

---

## 4. 原生安装（Windows native vs WSL）

Windows 端从 WSL 改成原生 Windows 网关（`install.ps1` → npm 全局 openclaw，config 在 `%USERPROFILE%\.openclaw\openclaw.json`，服务用 Scheduled Task）。

### Mac 端指导
- Mac **本来就是原生**（LaunchAgent，无 WSL 等价物），这部分无需对标——Mac 网关原生跑，配置同样 `~/.openclaw/openclaw.json`。

---

## 5. 相关 commit（按时间倒序，main 分支）

| commit | 说明 |
|---|---|
| `22a55bd` | read openclaw.json directly for Models/MCP lists（读路径绕开 config.get） |
| `2994379` | drop ReadBaseHashAsync + baseHash（清掉直写后无用的 config.get） |
| `0124c5a` | write openclaw.json directly, skip config.patch reload（写路径绕开 config.patch） |
| `5733541` | 天音助手 UI 修复（测试连接加速 + 流式 think 字号） |
| `a519f21` | 天音助手重设计（设置子页 + 聊天 UI + 持久化） |
| `37712b1` | wizard crash + WSL 步骤过滤 + GatewaySupervisor |

关键文件：`Services/OpenClawConfigFile.cs`（直读直写核心）、`Services/GatewaySupervisor.cs`（自愈）、`Pages/DifyPage.xaml(.cs)` + `Pages/DifySettingsPage.xaml(.cs)`（天音助手）、`Pages/ModelsPage.xaml.cs`（Models 直读直写 + 后台 models.list）。

---

## 给 Mac 侧的落地优先级
1. **先做直读直写 openclaw.json**（第 1 节）——收益最大、OS 无关、可直接照搬。读完这份就去改 Mac 的模型页/MCP 页配置读写。
2. 确认 LaunchAgent `KeepAlive` 配好（第 2 节）——大概率免开发。
3. 天音助手 UI（第 3 节）按需。
