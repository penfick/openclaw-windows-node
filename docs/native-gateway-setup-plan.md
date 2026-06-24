# 方案 1：Windows 端 setup 改装原生 Gateway（去 WSL）改造计划

> 目标：把 fork 的 setup 从「装 WSL 里的网关」改成「装原生 Windows 网关」，让普通用户无 WSL 门槛。
> 保留全部二开功能（模型/技能/MCP/OA/Dify）和官方 Hub 的可升级性。
> 本计划基于对 `openclaw-windows-node` 现状的逐文件核查。

---

## 0. 为什么可行（架构前提）

这个 fork **就是官方 Windows Hub 本体**（README: "🦞 OpenClaw Windows Hub"，作者 Scott Hanselman & Molty）。
它的架构天然分两层：

| 层 | 是否耦合 WSL | 切原生要动吗 |
|---|---|---|
| **RPC 客户端 + 全部 UI + 配对 + 连接管理 + 设备身份**（`OpenClaw.Shared`、`OpenClaw.Connection`、`OpenClaw.Tray.WinUI/Pages`） | **完全不耦合**——只认 `ws://localhost:18789` + token | **不动，原样复用** |
| **Setup 安装流水线**（`OpenClaw.SetupEngine`，`ICommandRunner.RunInWslAsync`） | 强耦合 WSL | 换成原生步骤 |
| **运行时看门狗**（`WslGatewayController`） | 强耦合 WSL | 加 `NativeGatewayController` |

**结论：这是改造 setup 引擎，不是重写。** 二开功能代码（模型/技能/MCP/OA/Dify）零改动（仅技能上传打包要去 wsl.exe，见 §4.3）。

官方原生 Windows 路径是被支持的一等公民（`platforms/windows.md`：
*"Native Windows CLI and Gateway flows are supported and continue to improve."*）：
```powershell
iwr -useb https://openclaw.ai/install.ps1 | iex   # 原生装
openclaw gateway install                            # 注册 Windows 计划任务（自启）
```

---

## 1. 现状：WSL 架构（要替换的部分）

### 1.1 Setup 流水线（`SetupSteps.cs` + `SetupPipeline.cs:41-64`）
18 步，WSL 相关：
- `PreflightWslStep`（`SetupSteps.cs:455`）——检测 WSL≥2.4.4、虚拟化。
- `CreateWslInstanceStep`（`:649`）——`wsl --install --distribution Ubuntu-24.04 --name OpenClawGateway --location <path> --no-launch`，建 app 专属发行版。
- `ConfigureWslInstanceStep`（`:868`）——写 `/etc/wsl.conf`（systemd/interop/automount 锁定）、`useradd openclaw`、建 Linux 目录。
- `ValidateWslLockdownStep`（`:943`）——校验 wsl.conf 锁定不变量。
- `InstallCliStep`（`:1124`）——`curl install-cli.sh | bash -s -- --version 2026.6.5` 在 WSL 里装；装到 `/home/openclaw/.openclaw/bin`。
- `ConfigureGatewayStep`（`:1258`）——`openclaw config set`（gateway.mode/port/bind/auth.token/reload/nodes…）在 WSL bash 里跑。
- `InstallGatewayServiceStep`（`:1419`）——`openclaw gateway install --force` → systemd user service。
- `StartGatewayStep`（`:1443`）——`openclaw gateway start`（systemd）+ `ss -tlnp` 端口检查 + WSL 内 `curl` 健康轮询。
- `MintBootstrapTokenStep`（`:1598`）——`openclaw qr --json` 在 WSL。
- `PairOperatorStep`/`PairNodeStep`（`:1663`/`…`）——WS 连接 + `openclaw devices/nodes approve` 在 WSL。
- `VerifyEndToEndStep`（`:2416`）——`openclaw gateway status --json` 在 WSL。
- `StartKeepaliveStep`（`:2737`）——`wsl.exe -- sleep infinity` 保活 WSL2 VM。
- `CleanupStaleDistroStep`（`:216`）——`wsl --unregister`。

关键常量：`SetupContext.DistroName="OpenClawGateway"`、`GatewayPort=18789`、`EffectiveGatewayUrl=ws://localhost:18789`。
版本钉：`GatewayLkgVersion`（`DefaultInstallUrl=https://openclaw.ai/install-cli.sh`、`LkgVersion=2026.6.5`）。

### 1.2 运行时看门狗（`WslGatewayController.cs`）
- `WslGatewayControlCommandBuilder.Build`（`:36`）→ `bash -lc "export PATH=... && openclaw gateway <start|stop|restart>"`。
- `ConnectionPage.xaml.cs:1748` 直接 `new WslGatewayController(...).RunAsync(distro, action)`（**无接口**，被 UI 直接依赖）。
- `WslGatewayKeepAliveService.cs`——`wsl.exe -d <distro> -- sleep infinity` 保活。

### 1.3 抽象现状
- `IWslCommandRunner`（`WslCommandRunner.cs:15`）——封装 wsl.exe，方法 `RunInWslAsync(distro, cmd)` 是一等公民，**WSL 假设 baked 进接口**。
- `ICommandRunner`（`CommandRunner.cs:10`）——setup 用，`RunInWslAsync` 也是接口方法。
- **没有 `IGatewayController` 接口**——`WslGatewayController` 是具体类。
- 连接层（`IGatewayOperatorConnector`/`IWindowsNodeConnector`）已 host-agnostic。

---

## 2. 目标架构（原生）

- 网关：原生 Windows 进程，`install.ps1` 装，`openclaw gateway install` 注册**计划任务**自启（计划任务被拒→Startup 文件夹兜底）。
- 连接：仍是 `ws://localhost:18789` + token（不变）。
- 看门狗：`NativeGatewayController`（包装 `openclaw.exe gateway start/stop` 或管计划任务）。
- 健康检查：C# `HttpClient` GET `http://127.0.0.1:18789/`（替掉 WSL 内 curl/ss/systemctl/journalctl）。
- 默认 setup 走原生；WSL 作为 Advanced 选项保留（§6 回退）。

---

## 3. 可复用（不动）清单

- `OpenClawGatewayClient`（`OpenClaw.Shared`，4235 行，零 WSL 引用）——整个 RPC/聊天/配置/配对。
- `GatewayUrlHelper`、`GatewayClientFactory`、`GatewayConnectionManager`、`GatewayRegistry`、`DeviceIdentity`、`CredentialResolver`。
- `GatewayService`（事件分发，无进程监管）。
- 全部 UI：`Pages/*`（模型/技能/MCP/OA/Dify/聊天/设置/连接页的 WS 连接路径）。
- `ConfigureGatewayStep` 的**意图**（那批 `openclaw config set` 的 key 列表）可移植——只换执行 wrapper。

---

## 4. 改动清单

### 4.1 Setup 流水线改造（`SetupEngine`）

**新增 `Runtime` 模式**：`SetupConfig.GatewayRuntime = "wsl" | "native"`（默认 native）。`DistroName`/`BaseDistro`/Wsl 段变条件化。

**删掉的步骤**（WSL 专属，原生无对应）：
- `ConfigureWslInstanceStep`、`ValidateWslLockdownStep`、`StartKeepaliveStep`、`CleanupStaleDistroStep`。

**替换的步骤**：

| 旧步骤 | 新步骤（原生） |
|---|---|
| `PreflightWslStep` | `PreflightNativeStep`：检测是否已装 openclaw（PATH/计划任务/安装目录）、Node 可用性、端口 18789 空闲。 |
| `CreateWslInstanceStep` + `InstallCliStep` | `InstallNativeCliStep`：跑 `iwr https://openclaw.ai/install.ps1 \| iex`（或 vendoring），钉 LKG 版本。`GatewayLkgVersion` 加原生 Windows install URL。 |
| `ConfigureGatewayStep` | 同样的 `openclaw config set` key 列表，但作为**原生 Windows 进程**跑（不再 bash-in-WSL）。 |
| `InstallGatewayServiceStep` | `openclaw gateway install` 注册** Windows 计划任务**（依赖网关 CLI 支持原生服务安装）。 |
| `StartGatewayStep` | 启动原生进程/计划任务；C# `HttpClient` 轮询 `http://127.0.0.1:18789/`（接受 200/401/403，2s 间隔，90s 超时）。 |
| `MintBootstrapTokenStep`/`PairOperator`/`PairNode` 的 approve 调用 | `openclaw devices/nodes approve` 作为原生进程跑（WS 配对本身已 host-agnostic）。 |
| `VerifyEndToEndStep` | `openclaw gateway status --json` 原生跑。 |

**执行抽象**：`ICommandRunner` 加 `RunNativeAsync(args)`（或泛化成 `RunOpenClawAsync(string[] args)`），各 step 按当前 Runtime 走 WSL 或原生。

### 4.2 看门狗改造（`Tray.WinUI/Services`）

- **抽接口** `IGatewayController { StartAsync/StopAsync/RestartAsync/StatusAsync }`。
- `WslGatewayController` 实现一份（现有逻辑）。
- 新增 `NativeGatewayController`：包装 `openclaw.exe gateway start|stop|restart`（或操作计划任务）。
- `ConnectionPage.xaml.cs:96` 改为按 `GatewayRecord.Runtime` 解析实现（工厂）。
- `setup-state.json` + `GatewayRecord` 加 `Runtime`/`Kind` 字段（`"wsl"`/`"native"`）。
- `WslKeepAlivePolicy.ShouldStart` 对 native **短路**（原生不需要保活 VM）。

### 4.3 技能上传去 wsl.exe（`Pages/Skills/`）

- 现状：`SkillsPage.xaml.cs:513` 一带用 `wsl.exe -d <distro> python3` 打 zip + base64（因为 `\\wsl.localhost\` UNC 读不到）。
- 原生后：网关在 Windows、技能源目录在 Windows，直接 **C# `System.IO.Compression.ZipFile.CreateFromDirectory(dir, zipPath)`** 打包，读 bytes 分块上传。
- 删掉 wsl.exe/python3/base64 整段——**是简化**。

### 4.4 配置（`GatewayLkgVersion.cs`）

- 加原生 Windows install URL（`https://openclaw.ai/install.ps1`）+ 版本钉（仍 2026.6.5，跟 WSL 一致）。
- 配置路径（`mcp.servers`/`models.providers.*` 等）**不变**——跟版本走，不跟 OS 走。

---

## 5. 关键决策点

| 决策 | 建议 |
|---|---|
| 网关装哪 | `%LOCALAPPDATA%\OpenClawTray\openclaw`（跟 install.ps1 默认对齐，用户级免管理员） |
| 自启 | 计划任务（`openclaw gateway install`）+ Startup 文件夹兜底（官方已实现） |
| 端口 | 保持 **18789**（跟现有一致，避免改连接配置） |
| 健康检查 | C# `HttpClient`，`http://127.0.0.1:18789/`，200/401/403 都算活 |
| 配置目录 | 原生网关的 state dir（`%APPDATA%\...\.openclaw` 或 install.ps1 默认），跟网关对齐 |

---

## 6. 风险与回退

1. **原生网关成熟度**：官方说 *"continue to improve"*。开干前先在 Windows 上 `iwr install.ps1 | iex` + `openclaw doctor` 验证：模型/技能/MCP/ACP agent 功能覆盖是否够你的场景。
2. **生态兼容 gaps**：方案 1 的已知代价——Linux-only 的 MCP/技能/agent 在原生 Windows 可能跑不起来。这是用「无 WSL」换来的，用户已接受。文档里给用户预期。
3. **保留 WSL 回退**：`GatewayRecord.Runtime` 可切；setup 默认 native，**Advanced 里保留 WSL 选项**给需要 Linux 兼容的用户。两套 setup 都留着（工量最大但最稳）。

---

## 7. 实施顺序（建议，风险递增）

1. **抽 `IGatewayController` + `Runtime` 字段**（不动现有 WSL 流程，纯重构，先保证不回归）。
2. **实现 `NativeGatewayController` + 原生 setup steps**（与 WSL 并存，`GatewayRuntime=native` 走新路径）。
3. **手动原生装一遍验证**：`install.ps1` + `openclaw gateway install` + `openclaw doctor` + 实测模型/技能/MCP。
4. **setup 默认切 native**，WSL 留 Advanced。
5. **技能上传换 C# ZipFile**。
6. **全量回归**：模型配置/技能三市场/MCP 增删改市场/OA/Dify + 原生网关 + 自启 + 重启。

---

## 8. 验证清单

- [ ] 原生装：`install.ps1` 成功，`openclaw --version`=2026.6.5，`openclaw doctor` 无致命项。
- [ ] 网关自启：重启 Windows 后 `openclaw gateway status` 在跑（计划任务）。
- [ ] app 连上：tray 图标绿，Command Center 显示 connected/paired。
- [ ] 模型：加 provider → 聊天下拉出现 → 切换生效 → 删除生效（config.patch 正确）。
- [ ] MCP：装 Puppeteer → `openclaw mcp list` 列出 → 聊天能调（**这是原生最大卖点：操作本地 Windows 浏览器**）。
- [ ] 技能：市场装/公司市场装/上传本地（C# ZipFile 打包）/详情/启用禁用。
- [ ] OA/Dify：按 Windows 规格回归。
- [ ] WSL 回退：Advanced 选 WSL 仍能装（保住兜底）。

---

## 9. 工作量预估

- 抽象层（`IGatewayController` + `Runtime` + 工厂）：小。
- 原生 setup steps（~5 新 + 改 wrapper）：中（主要在 `InstallNativeCliStep` 的 install.ps1 编排 + 健康轮询）。
- `NativeGatewayController`：小-中。
- 技能上传换 ZipFile：小（简化）。
- 验证 + WSL 回退并存：中。
- **整体：中等。集中在 SetupEngine + Services，UI/RPC/二开功能不碰。**
