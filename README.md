# RevitMCP

把当前打开的 Revit 文档暴露给 MCP 客户端（Claude Code / Claude Desktop）的插件框架。
**纯 C# 单进程**——MCP 服务直接跑在 Revit 进程内，没有额外的桥接进程。

支持 **Revit 2019 – 2024**。架构设计见 [docs/architecture.md](docs/architecture.md)。

> 当前进度：**M0 骨架完成**。插件可加载、Ribbon 可用、配置与日志已就绪；
> 传输层与协议方法在 M1 接入（此前服务不接受连接，界面文案已注明）。

---

## 快速开始

```bash
# 构建某个版本（不需要本机安装 Revit，Revit API 从 NuGet 取参考程序集）
powershell -ExecutionPolicy Bypass -File build/build-all.ps1 -RevitYears 2024

# 安装到当前用户的插件目录（无需管理员权限；安装前请先关闭 Revit）
powershell -ExecutionPolicy Bypass -File build/install.ps1 -RevitYear 2024
```

启动 Revit，功能区应出现 **RevitMCP** 选项卡。

```bash
# 跑不依赖 Revit 的单元测试
dotnet test tests/RevitMCP.Protocol.Tests/RevitMCP.Protocol.Tests.csproj -c "Debug R24"
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
src/RevitMCP.Protocol    JSON-RPC 2.0 + MCP 消息 + 自带 JSON 实现   ← 不依赖 Revit
src/RevitMCP.Transport   TcpListener 迷你 HTTP + SSE               ← 不依赖 Revit（M1）
src/RevitMCP.Tooling     [McpTool] 注册、Schema 生成、执行管线      ← 不依赖 Revit（M3）
src/RevitMCP.Addin       Revit 插件入口、Ribbon、调度器、工具实现    ← 唯一引用 Revit API
```

前三个项目刻意不依赖 Revit API：它们能脱离 Revit 直接跑单元测试，
这也是 CI 上能覆盖大部分逻辑的原因。

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
- 安装前必须关闭 Revit，否则 DLL 被占用。`install.ps1` 会主动检查并拒绝。

---

## 里程碑

| | 内容 | 状态 |
|---|---|---|
| M0 | 解决方案骨架、版本矩阵、Ribbon、配置、日志、安装脚本 | ✅ 完成 |
| M1 | TcpListener HTTP + JSON-RPC + `initialize`/`ping` | 待开始 |
| M2 | `RevitDispatcher`（ExternalEvent 线程编组）+ 超时语义 | 待开始 |
| M3 | 工具框架 + 首批只读工具 | 待开始 |
| M4 | 事务管线、失败预处理、对话框拦截、写保护 | 待开始 |
| M5 | SSE 进度通知、审计日志、多实例发现完善 | 待开始 |
