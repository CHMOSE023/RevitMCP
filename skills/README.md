# skills/

给**使用**这套 MCP 服务的 Agent 看的指引，不是给开发这个插件的人看的。

两者要分清楚：

- **开发 RevitMCP 插件**（改 C# 代码、加工具、跑 `dotnet test`）→ 看仓库根目录的 `README.md` 与 `docs/`。
- **通过 RevitMCP 建模**（调 `revit_*` 工具把模型建出来）→ 就是这里的指引。

## revit-modeling

```
revit-modeling/
├── SKILL.md                            入口：八条硬规则 + 阶段表
└── references/
    ├── modeling-sequence.md            依赖图、任务分支、批次大小
    ├── validation-checklist.md         每类构件建完要量什么
    ├── recovery-guide.md               失败后先判断"执行了没有"，按错误码处置
    └── version-limitations.md          已修复的坑（别再绕）与仍存在的限制
```

内容来源是 2026-09 的两轮真实建模实测：每一条规则都对应一次
"调用成功、警告为 0、几何是错的"。

---

## 怎么加载：**服务自己发**，不用装

这几个文件在编译时嵌进 `RevitMCP.Addin.dll`（见 `src/RevitMCP.Addin/RevitMCP.Addin.csproj`
里的 `EmbeddedResource`），服务通过 MCP 接口直接发出去。**客户端什么都不用装。**

这么做不是图省事：装漏了没人发现——Agent 照样能调工具，只是按自己的习惯乱来；
装旧了更糟——它会对着**已经修好的缺陷**执行补救动作，而一切看起来都在正常工作。
指引跟着 DLL 走，就永远和工具的实际行为是同一个版本。

### 三条路，按客户端能力自动收敛

| 路径 | 怎么用 | 适合谁 |
|---|---|---|
| **工具**（主）| `revit_get_modeling_guide`，`section` 取 `overview` / `sequence` / `validation` / `recovery` / `limitations` | 所有客户端。`tools/list` 人人都加载，这是唯一"Agent 一定看得见"的路 |
| **资源** | `resources/list` → `resources/read`，URI 形如 `revitmcp://guide/overview` | 支持 MCP 资源的客户端。更省上下文：不占工具清单 |
| **开场白** | `initialize` / `server/discover` 返回的 `instructions` 第一句就指向指引 | 会把 instructions 放进系统提示的客户端，等于默认就知道有这份东西 |

三条路读到的是同一份内容（同一批嵌入文件），不存在版本漂移。

### 还想按文件装？

如果你的客户端有自己的 skill 机制、希望在**连上 MCP 之前**就带着这份指引，
也可以把它当普通 skill 目录装（`SKILL.md` 保留了 frontmatter 正是为此）：

```powershell
$dst = "$env:USERPROFILE\.claude\skills\revit-modeling"
New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
Copy-Item -Recurse -Force "skills\revit-modeling" $dst
```

但要记住这份副本**不会**随插件升级而更新——升级插件后请重新拷贝，
或者干脆只用 MCP 那条路。

---

## 与代码的关系

`references/version-limitations.md` 里"已修复"那张表**必须跟着代码走**。
修好一个坑就把它从"仍然存在"挪过去并注明日期——
不挪的后果是 Agent 永远在执行不必要的补救动作，而这比留着 bug 更难发现：
一切看起来都在正常工作。

改这里的 Markdown 就等于改服务发出去的指引，重新构建插件即可生效，
**不需要**在别处同步副本。
