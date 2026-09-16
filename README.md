# RevitMCP

把当前打开的 Revit 文档暴露给 MCP 客户端（Claude Code / Claude Desktop）的插件框架。
**纯 C# 单进程**——MCP 服务直接跑在 Revit 进程内，没有额外的桥接进程。

支持 **Revit 2019 – 2024**。架构设计见 [docs/architecture.md](docs/architecture.md)。

> 当前进度：**M5 完成**。SSE 进度通知、工具调用审计、多实例发现都已就位。
> 4 个只读工具 + 2 个写工具可用。

---

## 快速开始

```bash
# 构建某个版本（不需要本机安装 Revit，Revit API 从 NuGet 取参考程序集）
powershell -ExecutionPolicy Bypass -File build/build-all.ps1 -RevitYears 2024

# 安装到当前用户的插件目录（无需管理员权限；安装前请先关闭 Revit）
powershell -ExecutionPolicy Bypass -File build/install.ps1 -RevitYear 2024
```

启动 Revit，功能区应出现 **RevitMCP** 选项卡。

接入 Claude Code（端口与令牌见 Revit 面板上的「复制接入命令」按钮）：

```bash
claude mcp add --transport http revit http://127.0.0.1:7801/mcp --header "Authorization: Bearer <token>"
```

```bash
# 跑不依赖 Revit 的测试（协议 33 + 调度与工具框架 87 + HTTP/MCP 端到端 54）
dotnet test RevitMCP.sln -c "Debug R24"
```

卸载：`powershell -ExecutionPolicy Bypass -File build/install.ps1 -RevitYear 2024 -Uninstall`

---

## 版本矩阵

配置名形如 `Debug R24` / `Release R19`，R 后的年份同时决定目标框架、Revit API 包版本与条件编译符号。
矩阵定义在 [Directory.Build.props](Directory.Build.props)，新增版本只需改那一处。

| Revit | 配置 | 目标框架 | Revit API 包 |
|---|---|---|---|
| 2019 | `R19` | net47 | 2019.2.11 |
| 2020 | `R20` | net47 | 2020.2.60 |
| 2021 | `R21` | net48 | 2021.1.50 |
| 2022 | `R22` | net48 | 2022.1.80 |
| 2023 | `R23` | net48 | 2023.1.90 |
| 2024 | `R24` | net48 | 2024.3.60 |

产物落在 `artifacts/<年份>/`（Debug 为 `artifacts/<年份>-debug/`）。

---

## 项目结构

```
src/RevitMCP.Protocol    自带 JSON 实现 + JSON-RPC 2.0 + MCP 方法分发   ← 不依赖 Revit
src/RevitMCP.Transport   TcpListener 迷你 HTTP + MCP over HTTP 粘合层    ← 不依赖 Revit
src/RevitMCP.Tooling     调度队列、[McpTool] 注册、Schema 生成、执行管线   ← 不依赖 Revit
src/RevitMCP.Addin       Revit 插件入口、Ribbon、ExternalEvent 接线、工具 ← 唯一引用 Revit API
```

## 现有工具

| 工具 | 作用 |
|---|---|
| `revit_get_document_info` | 当前文档标题、路径、活动视图、写入模式是否开启 |
| `revit_list_categories` | 模型中实际存在构件的类别及数量（查询前先用它确认类别名）|
| `revit_query_elements` | 按类别查构件，返回 ID / 名称 / 类型 / 标高 |
| `revit_get_element_parameters` | 批量读参数，同时给出原始值与带单位的显示值 |
| `revit_set_element_parameters` | 批量改同一个参数，全有全无（写）|
| `revit_create_wall` | 按起止点建一面直墙，坐标用毫米（写）|

前四个只读，写保护关闭时也能用；后两个会改模型，需要用户在 Ribbon 上开启写入。

### 写工具的三条保证

- **一个调用 = 一个事务 = 撤销栈里的一步**，命名 `MCP: <工具名>`，用户看得懂也能单步撤销。
- **失败必回滚**，模型回到调用前的样子。工具自己抛的失败和没人预料到的异常一视同仁。
- **被吞掉的警告一定说出来**。事务里的 Revit 警告会被自动忽略（否则弹出的模态框会把
  Revit 和服务一起卡死），但每一条都会出现在返回结果的 `warnings` 字段里。
  静默吞警告比弹框更危险——模型和用户都不会知道刚才发生过什么。

影响构件数超过 `maxElementsPerWrite`（默认 500）时返回 `CONFIRMATION_REQUIRED`，
要模型带 `confirm: true` 重来。这道闸防的不是"想改 600 个"，而是"以为在改 6 个、
实际匹配到 600 个"。

## 写一个新工具

`ReadOnly = true` 的工具直接执行；去掉它就是写工具，管线会自动套上事务与两道防线。

```csharp
[McpTool("revit_do_something", Title = "做点什么", Description = "给模型看的说明。", ReadOnly = true)]
public sealed class DoSomethingTool : RevitTool<DoSomethingInput, DoSomethingOutput>
{
    public override DoSomethingOutput Execute(DoSomethingInput input, ToolExecutionContext<UIApplication> context)
    {
        var document = RequireDocument(context);   // 此处已在主线程且具备 API context
        ...
    }
}

public sealed class DoSomethingInput
{
    [McpParam("要处理的类别", Required = true)]
    public string Category { get; set; }

    [McpParam("上限，默认 100")]     // int? → 可选；int → 必填
    public int? Limit { get; set; }
}
```

就这些。线程编组、事务、参数绑定与校验、Schema 生成、超时、序列化、错误映射全由管线处理，
`OnStartup` 时自动扫描注册。失败时抛 `ToolFailureException(McpDomainError.XXX, "原因")`——
写工具抛出时事务会回滚，不必自己收拾。

想让模型看见某个提示（比如"你没指定标高，我用了标高 1"），
往 `context.Warnings` 里 `Add` 一句即可，管线会并进输出的 `warnings` 字段。

前三个项目刻意不依赖 Revit API，这不只是洁癖：**整条 HTTP + MCP 通路能在没装 Revit 的机器上
端到端测试**，CI 因此能覆盖大部分逻辑。

## 进度通知

长操作（批量改几百个构件之类）会通过 SSE 推 `notifications/progress`，
免得客户端把一个正常的慢操作当成卡死。

**发不发进度由客户端决定**：请求的 `params._meta.progressToken` 给了就发，
响应转成 `text/event-stream`；没给就还是单个 `application/json` 响应。
规范如此规定，也正好省掉"该不该用 SSE"这个判断——没人要就不发。

工具侧只有一行：

```csharp
foreach (var element in elements)
{
    // ...
    ProgressTicker.Tick(context.Progress, ++done, total, "已修改");
}
```

`context.Progress` 永远不为 null（没人听时是空实现），节流到每 25 个一条。
上报从不阻塞：工具在 Revit 主线程上跑，通知在 HTTP 线程上写，中间隔着一条队列。

## 审计

每次 `tools/call` 在日志里留一行，成功、失败、被拒都记：

```
2026-09-16 11:42:03.117 [AUDIT] revit_set_element_parameters [写] 成功 · 214ms · 影响 3 个构件 · 1 条警告 · elementIds=["198749","234869",…共 3 项], parameterName="注释"
2026-09-16 11:42:31.882 [AUDIT] revit_create_wall [写] 被拒/WRITE_DISABLED · 0ms · startX=0, startY=0, endX=6000, endY=0
```

审计要回答的是"模型到底被动过什么"，所以：

- **被拒的也记。** 一串被写保护拒掉的写请求本身就是值得看见的信号。
- **`REVIT_BUSY` 记「被拒」，`TIMEOUT` 记「失败」。** 前者模型没被碰过，后者可能改了一半——
  事后翻日志时这两者绝不能混为一谈。
- **入参只记摘要。** 500 个 ID 原样写进日志等于没写。
- **绕过 `logLevel`。** 把日志级别调高不该让审计悄悄消失，那恰恰是最需要它的时候。

## 协议支持

同时服务 MCP 的两代形态（规范允许 dual-era 服务端）：

| era | 版本 | 形态 |
|---|---|---|
| modern | `2026-07-28` | 无状态；版本与能力随每个请求的 `_meta` 传递；`server/discover`；无会话、无 GET SSE |
| legacy | `2025-11-25` / `2025-06-18` / `2025-03-26` | `initialize` 握手建立会话 |

判定依据是消息体里有没有 `_meta["io.modelcontextprotocol/protocolVersion"]`。
之所以不能只看 `MCP-Protocol-Version` 头——2025-06-18 起的 legacy 客户端同样会发这个头。

**零第三方运行时依赖。** 产物只有上述 4 个 DLL。Revit 把所有插件加载进同一个 AppDomain
且不应用插件自身的绑定重定向，任何外部包都是潜在的版本冲突源——包括 Newtonsoft.Json，
所以协议层自带了一个最小 JSON 实现。理由详见架构文档 §4。

---

## 运行时文件

| 路径 | 用途 |
|---|---|
| `%APPDATA%\RevitMCP\config.json` | 端口、访问令牌、写入开关等 |
| `%LOCALAPPDATA%\RevitMCP\logs\revit-<pid>.log` | 每进程一个日志文件 |
| `%LOCALAPPDATA%\RevitMCP\instances\revit-<pid>.json` | 多实例发现：端口、活动文档、写入开关；进程退出时删除，启动时清理残留 |

---

## 开发注意

- **Ribbon 上的「写入」默认关闭。** 关闭时所有写工具返回 `WRITE_DISABLED`，只有只读工具可用。
- **`.ps1` 脚本必须存为 UTF-8 with BOM。** Windows PowerShell 5.1 会把无 BOM 的脚本按系统 ANSI 码页读取，
  中文会变成乱码并导致语法错误。
- **Revit API 差异只允许出现在 `src/RevitMCP.Addin/Compat/`。** 其他地方一律走那里的兼容方法，
  例如 `ElementId` 在 2024 起由 Int32 变为 Int64。
- **一切 Revit API 调用必须经 `RevitDispatcher.InvokeAsync` 编组到主线程。** 从 HTTP 线程直接碰
  `Document` 轻则抛异常、重则崩 Revit。
- **写工具里不要自己 `new Transaction`。** 管线已经开好了，再开一个会直接抛异常。
  需要多步且要对外表现为一步撤销时，用 `SubTransaction`。
- **`DialogBoxShowing` 的解绑必须走 `try/finally`。** 漏解绑的后果不是这次调用出错，
  而是此后用户自己操作 Revit 时的正常对话框也被悄悄吃掉——那会被当成"Revit 坏了"。
- **`REVIT_BUSY` 和 `TIMEOUT` 不是一回事**，不要合并：前者保证模型没被碰过，后者意味着操作已经
  跑起来、模型可能已变。模型会据此决定要不要重试。
- 安装前必须关闭 Revit，否则 DLL 被占用。`install.ps1` 会主动检查并拒绝。

---

## 里程碑

| | 内容 | 状态 |
|---|---|---|
| M0 | 解决方案骨架、版本矩阵、Ribbon、配置、日志、安装脚本 | ✅ 完成 |
| M1 | TcpListener HTTP + JSON-RPC + dual-era 握手 + Origin/Bearer 校验 | ✅ 完成 |
| M2 | `DispatchQueue` + `RevitDispatcher` 线程编组、双重超时语义、首个工具 | ✅ 完成 |
| M3 | `[McpTool]` 注册、Schema 生成、执行管线 + 4 个只读工具 | ✅ 完成 |
| M4 | 事务管线、失败预处理、对话框拦截、写保护、规模阈值 + 2 个写工具 | ✅ 完成 |
| M5 | SSE 进度通知、审计日志、多实例发现完善 | ✅ 完成 |
