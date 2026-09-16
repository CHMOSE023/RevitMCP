# RevitMCP

把当前打开的 Revit 文档暴露给 MCP 客户端（Claude Code / Claude Desktop）的插件框架。
**纯 C# 单进程**——MCP 服务直接跑在 Revit 进程内，没有额外的桥接进程。

支持 **Revit 2019 – 2024**。架构设计见 [docs/architecture.md](docs/architecture.md)。

> 当前进度：**M3 完成**。工具框架（`[McpTool]` 反射注册 + JSON Schema 自动生成 + 执行管线）
> 已就位，4 个只读工具可用。写工具的事务管线在 M4。

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
# 跑不依赖 Revit 的测试（协议 33 + 调度与工具框架 58 + HTTP/MCP 端到端 42）
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

全部只读。写工具在 M4。

## 写一个新工具

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

就这些。线程编组、参数绑定与校验、Schema 生成、超时、序列化、错误映射全由管线处理，
`OnStartup` 时自动扫描注册。失败时抛 `ToolFailureException(McpDomainError.XXX, "原因")`。

前三个项目刻意不依赖 Revit API，这不只是洁癖：**整条 HTTP + MCP 通路能在没装 Revit 的机器上
端到端测试**，CI 因此能覆盖大部分逻辑。

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
| `%LOCALAPPDATA%\RevitMCP\instances\revit-<pid>.json` | 多实例发现：客户端据此知道该连哪个端口 |

---

## 开发注意

- **Ribbon 上的「写入」默认关闭。** 关闭时所有写工具返回 `WRITE_DISABLED`，只有只读工具可用。
- **`.ps1` 脚本必须存为 UTF-8 with BOM。** Windows PowerShell 5.1 会把无 BOM 的脚本按系统 ANSI 码页读取，
  中文会变成乱码并导致语法错误。
- **Revit API 差异只允许出现在 `src/RevitMCP.Addin/Compat/`。** 其他地方一律走那里的兼容方法，
  例如 `ElementId` 在 2024 起由 Int32 变为 Int64。
- **一切 Revit API 调用必须经 `RevitDispatcher.InvokeAsync` 编组到主线程。** 从 HTTP 线程直接碰
  `Document` 轻则抛异常、重则崩 Revit。
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
| M4 | 事务管线、失败预处理、对话框拦截、写保护 | 待开始 |
| M5 | SSE 进度通知、审计日志、多实例发现完善 | 待开始 |
