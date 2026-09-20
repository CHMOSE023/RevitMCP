# RevitMCP

把当前打开的 Revit 文档暴露给 MCP 客户端（Claude Code / Claude Desktop）的插件框架。
**纯 C# 单进程**——MCP 服务直接跑在 Revit 进程内，没有额外的桥接进程。

支持 **Revit 2019 – 2024**。
架构设计见 [docs/architecture.md](docs/architecture.md)；
与 `revit-bridge-addin` 的逐条能力对照见 [docs/bridge-parity.md](docs/bridge-parity.md)，
它由 [`build/check-parity.ps1`](build/check-parity.ps1) 生成并校验——
工具改了名、或者对方增删了命令，脚本会当场报错而不是悄悄生成一份看起来仍然正确的文档。

> 当前进度：**M10 建模能力**——让模型建得出、改得动、存得住、出得了图。
> 路线与依据见 [docs/roadmap-m10.md](docs/roadmap-m10.md)。
>
> · **M10-A 骨架与落盘** ✅ 已在 Revit 2019 实测通过：保存与另存、标高与轴网、
>   标高约束体系（墙/柱顶到标高、墙按外皮定位）、带洞楼板。
> · **M10-B 复用与材质**、**M10-C 出图** 的主体已随能力补齐一并落地：
>   图元变换与编组、材质、创建视图与视图样板、注释与标注、交付导出、
>   协同与链接、阶段与设计选项、参数定义、MEP、碰撞检查。
> · 路线图里点名的两项已补上（见下面的 M10-E）：`revit_load_family` 载入族、
>   取景控制（独立成 `revit_set_view_extent`，理由见该工具的说明）。
>
> **M10-D 正确性修复**（按《源码评估与改进报告》第一阶段）：
> 楼板不再忽略 `levelId`（竖向约束写完读回来核对，对不上就整批回滚）、
> 柱的底部偏移真的写进去、立面改用立面标记创建、
> 房间/空间/面积标记走各自的专用 API、参数写入不再在项目单位与内部单位之间静默回退、
> 写操作支持 `expectedDocumentId` 前置校验、`action: "new"` 支持一步建出可写的新项目、
> 被忽略的 Revit 警告带上构件 ID、认不出来的模态对话框不再替用户点确定、
> Origin 白名单按 URI 比对。逐条见 [docs/fix-log-m10d.md](docs/fix-log-m10d.md)。
>
> **M10-E 能力补齐**（报告 F14 里我实测撞到的三条）：
> `revit_load_family` 载入族（此前缺族时纯 MCP 客户端无法自救）、
> `revit_set_view_extent` 视图取景（此前导出的图多半是空白）、
> 房间可设上限标高（此前一律是 Revit 默认的 2438.4 毫米）。逐条见 [docs/fix-log-m10d.md](docs/fix-log-m10d.md#m10-e-能力补齐报告-f14-的前三条)。
>
> 共 72 个工具：34 个只读 + 38 个写（含 2 个默认关闭的逃生舱）。
> 与 `revit-bridge-addin` 的逐条对照见 [docs/bridge-parity.md](docs/bridge-parity.md)。

---

## 快速开始

```bash
# 构建某个版本（不需要本机安装 Revit，Revit API 从 NuGet 取参考程序集）
powershell -ExecutionPolicy Bypass -File build/build-all.ps1 -RevitYears 2024

# 安装到当前用户的插件目录（无需管理员权限；安装前请先关闭 Revit）
powershell -ExecutionPolicy Bypass -File build/install.ps1 -RevitYear 2024
```

启动 Revit，功能区应出现 **RevitMCP** 选项卡。

接入 Claude Code（端口与令牌见 Revit 面板上「接入信息 → Claude Code 命令」）：

```bash
claude mcp add --transport http revit http://127.0.0.1:7801/mcp --header "Authorization: Bearer <token>"
```

本服务是标准 **MCP over HTTP**，不限于某一种客户端。读 `mcpServers` 配置的客户端
（Claude Desktop、Cline、Continue 等）用「接入信息 → JSON 配置」，复制到自己的配置里即可：

```json
{
  "mcpServers": {
    "revit": {
      "type": "http",
      "url": "http://127.0.0.1:7801/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

```bash
# 跑不依赖 Revit 的测试（协议 33 + 工具框架 139 + 工具契约 147 + HTTP/MCP 端到端 54）
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

72 个工具：**34 个只读 + 38 个写**（含 2 个默认关闭的逃生舱）。只读工具在「浏览模型」下也能用；写工具需要用户在 Ribbon 上切到「修改模型」。

### 查看模型

| 工具 | 作用 |
|---|---|
| `revit_get_document_info` | 当前文档标题、路径、活动视图、写入模式是否开启 |
| `revit_list_documents` | 这个 Revit 里打开的所有文档。批量审计从它开始 |
| `revit_get_project_units` | 项目的长度/面积/体积显示单位，以及它与工具单位是否一致 |
| `revit_get_project_location` | 经纬度、时区、测量点偏移、正北角。导 IFC/NWC 前核对它 |
| `revit_list_categories` | 模型中实际存在构件的类别及数量（查询前先用它确认类别名）|
| `revit_list_types` | 按类别列出族类型及其 ID、厚度、使用数量——建模前靠它挑规格 |
| `revit_list_families` | 已载入的族及其类型数。建模前确认目标族在不在项目里 |
| `revit_list_levels` | 标高的 ID、名称、高程，建模时的 `levelId` 从这里来 |
| `revit_query_elements` | 按类别/空间/阶段/设计选项查构件，返回 ID / 名称 / 类型 / 标高 |
| `revit_get_element_parameters` | 批量读参数，同时给出原始值与带单位的显示值 |
| `revit_get_element_geometry` | 包围盒、定位线/点、朝向。**不返回网格** |
| `revit_get_warnings` | Revit 自己记录的模型警告，按种类归组——质检闭环的地基 |
| `revit_check_clashes` | 硬碰撞检查（实体真的相交），支持跨链接模型 |
| `revit_list_rooms` | 房间及其面积（㎡）、周长、边界。面积为 0 直接点出"没围合" |
| `revit_list_materials` | 材质及其 ID、类别、颜色。设材质用的就是这里的 ID |
| `revit_calculate_material_quantities` | 按材质汇总体积与面积，数字与明细表一致 |
| `revit_list_groups` | 组实例及其成员数、同类型实例数（改一个会联动几个）|
| `revit_list_views` | 视图与图纸及其类型、比例、所在图纸 |
| `revit_list_view_templates` | 视图样板及其适用的视图类型 |
| `revit_list_view_family_types` | 视图族类型及其 ID——`revit_create_views` 的 `viewFamilyTypeId` 从这里来 |
| `revit_get_sheet_contents` | 图纸上放了哪些视图、用什么图签、可写参数名有哪些 |
| `revit_list_revisions` | 修订序列及其编号、日期、发布状态 |
| `revit_read_schedule` | 把明细表读成表格数据 |
| `revit_list_schedulable_fields` | 某类别做明细表能选哪些字段（字段名随项目语言，别猜）|
| `revit_list_worksets` | 工作集及其开关状态。**关闭的工作集里的构件查不到** |
| `revit_list_links` | 链接模型及其载入状态。里面的构件要用 `linkedDocumentId` 才查得到 |
| `revit_list_phases` | 阶段序列。改造项目统计前必看，否则"现有"和"新建"会一起数 |
| `revit_list_design_options` | 设计选项。多方案模型统计前必看 |
| `revit_list_project_parameters` | 已绑定的项目参数及其类型、绑定方式、绑定到哪些类别 |
| `revit_list_mep_systems` | MEP 系统与系统类型。建管线前查 `systemTypeId` |
| `revit_get_model_changes` | 自上次 token 以来新增/修改/删除了什么。增量同步用 |
| `revit_get_selection` | 用户此刻在 Revit 里选中了什么 |
| `revit_export_image` | 把视图或图纸导成图片落盘，返回完整路径 |
| `revit_export_documents` | 导 DWG / DXF / PDF / IFC / NWC |
| `revit_export_schedules` | 把明细表导成 CSV / TSV / TXT |

### 改模型

| 工具 | 作用 |
|---|---|
| `revit_create_line_based_elements` | 按定位线批量建墙、梁 |
| `revit_create_point_based_elements` | 按插入点批量建门、窗、柱、基础、家具 |
| `revit_create_surface_based_elements` | 按闭合边界批量建楼板、屋顶、天花 |
| `revit_create_mep_curves` | 按定位线批量建风管、水管、线管、桥架 |
| `revit_create_datums` | 建轴网与标高（标高默认连楼层平面一起建）|
| `revit_create_rooms` | 按点批量建房间，可设上限标高，回执给出面积与实际高度 |
| `revit_load_family` | 载入 .rfa 族文件，返回族与它的全部类型 ID。**样板里没有的族只能靠它** |
| `revit_create_materials` | 建材质。**尽量从已有材质复制**，空白新建的没有物理与外观资源 |
| `revit_create_project_parameter` | 建项目参数并绑定到类别（API 只能造共享参数，见下）|
| `revit_transform_elements` | 平移 / 复制 / 旋转 / 镜像 |
| `revit_set_elements_pinned` | 钉住 / 解钉。轴网标高移不动时先查它 |
| `revit_group_elements` | 打组 / 打散 |
| `revit_change_element_types` | 换构件类型，可按 ID 也可按原类型整批换 |
| `revit_duplicate_type` | 复制族类型并改厚度/参数——「类型属性」里那个「复制」按钮 |
| `revit_set_type_parameters` | 批量改类型参数。回执给出实际波及的构件数 |
| `revit_set_element_parameters` | 批量改同一个参数，全有全无 |
| `revit_batch_set_parameters` | 按条件匹配 + 一次写多个参数的完整形态 |
| `revit_create_views` | 建平面 / 剖面 / 立面 / 三维视图 |
| `revit_apply_view_template` | 批量套 / 取消视图样板 |
| `revit_set_view_extent` | 把视图取景收到构件上（三维含剖切框）。**出图前用它**，否则图里多半是空白 |
| `revit_create_sheets` | 批量建图纸 |
| `revit_duplicate_sheets` | 复制图纸（含视图与详图，语义见回执的 `method`）|
| `revit_update_sheets` | 改图纸编号、名称、图签。**成批改号自动走两阶段防撞** |
| `revit_add_views_to_sheet` | 把视图或明细表摆到图纸上，回读实际位置并检查越界 |
| `revit_create_schedule` | 按类别建明细表，选字段、排序 |
| `revit_create_annotations` | 建文字、标记、尺寸标注、修订云线 |
| `revit_tag_all_in_view` | 在视图里按类别批量标记 |
| `revit_activate_view` | 切换活动视图（改界面不改模型，见下） |
| `revit_delete_elements` | 删除构件，先预览连带影响再确认 |
| `revit_open_document` | 打开文件 / 从样板新建 / 打开族来编辑 |
| `revit_save_document` | 保存或另存为 |
| `revit_close_document` | 关闭文档（关活动文档时自动先切走）|
| `revit_sync_to_central` | 同步到中心文件 / 放弃编辑权。`confirm` 必填 |

### 逃生舱（默认关闭）

| 工具 | 作用 |
|---|---|
| `revit_invoke_api` | 反射调用任意 Revit API |
| `revit_execute_script` | 编译并执行一段 C# 代码 |

这两个绕开了本项目其余部分的全部保证——没有单位换算、没有校验、没有规模闸。
它们**不跟着 Ribbon 上的写入开关走**，需要在 `config.json` 里单独把 `escapeHatchEnabled`
设为 true 并重启 Revit。开着时 Ribbon 上的操作模式按钮会一直显示警示色。

理由是它们的影响面根本不止于"改模型"：能读写任何文件、发任何网络请求。
把这种能力和"我要建一面墙"放在同一个开关下面，那个开关就失去意义了。

**为什么是 C# 不是 Python。** 本项目全程零第三方依赖——Revit 把所有插件加载进同一个
AppDomain 且不应用插件自己的绑定重定向（架构 §2 的 C2），引入 IronPython 就必须
同时引入 ILRepack 那一整套内联化设施。而 .NET Framework 自带的 `CSharpCodeProvider`
提供同样的"执行任意代码"能力、零依赖，脚本里用的还是 Revit API 本身的类型，
不需要跨语言的类型映射。

### 工具分三类，不是两类

`ReadOnly` 一个标志曾经同时管着两件事——要不要受写保护管辖、要不要开事务。
`revit_activate_view` 把这两件事撑开了：它该受管辖（会改变其他工具的答案），
却**不能**开事务（Revit 不允许在事务打开时切换活动视图）。

| 工具改的是 | 写保护 | 事务 | 例子 |
|---|---|---|---|
| 模型 | 管 | 开 | 建墙、删构件、改参数 |
| Revit 的界面状态 | 管 | 不开 | `revit_activate_view` |
| 只是屏幕高亮 | 不管 | 不开 | `revit_set_selection` |

判据是**会不会改变别人的答案**：切换活动视图会改变 `activeViewOnly` 查询和"导出当前视图"的结果，
而选择集不会。`revit_export_image` 虽然往磁盘写文件，但不改模型也不改别的工具的答案，
所以仍是只读——只读的质检流程恰恰最需要截图，要求先开修改模式才能截个图说不通。

### 构件怎么指代：ElementId 与 uniqueId

两种写法在**所有**接受构件 ID 的地方都能用：

| | 形如 | 适用范围 |
|---|---|---|
| `ElementId` | `"225318"` | 短，读着顺。**只在这一个文档的这一次会话里有效** |
| `uniqueId` | `"c0326e0e-…-0000d2d5"` | Revit 维护的 GUID，跨会话、跨重启稳定，导出 IFC 后也对得上 |

查询回执两个都给。**要跨会话留存的东西一律存 uniqueId**——
审计报告里的问题清单、交付物里的构件索引，用 ElementId 记下来，
第二天拿出来会指向完全不同的构件，而且看起来完全正常。

### 一个 Revit 能同时开十几个模型

批量审计就是冲着它们去的。**Revit 允许对任何打开的文档做只读查询，不必先切成活动文档**——
切过去会打断用户正在看的东西，而查询本不该有这种副作用。

所以只读工具都有一个可选的 `documentId`（来自 `revit_list_documents`）：省略就是活动文档，
给了就查那一个。

**写操作一律只作用于活动文档。** 让模型去改一个用户根本没在看的文档，
风险和收益完全不成比例。

跨文档时无意义的概念会被明确拒绝，而不是给个看似合理的错答案：
`activeViewOnly` 对别的文档谈不上（活动视图属于整个 Revit，只存在于活动文档里），
`list_views` 的"是否活动视图"一列在跨文档时一律 false。

### 族：能用，但造不了

| | 能否 | 为什么 |
|---|---|---|
| 放置族实例（门窗家具梁） | 能 | |
| 查类型、读写参数 | 能 | |
| **复制类型并改厚度/参数** | 能 | `revit_duplicate_type` |
| 把 .rfa 载入项目 | 不能 | 没做。要做得先解决族库目录的路径闸，同 `export_image` |
| 造族（.rfa 本身） | 不做 | 族文档与项目文档是两套 API 语义，要做就单独一套工具集 |

复制类型这件事 M6 时刻意没做，理由是"按尺寸动态建类型会往用户的类型库里塞垃圾"。
那个担心仍然成立，所以工具**不提供"给我一面 250 厚的墙"这种模糊入口**：
必须显式指定从哪个类型复制、新类型叫什么名字。
类型库是用户的资产，新增什么、叫什么，该是明确的决定而不是副作用。

**厚度不是一个能直接写的参数。** Revit 的类型属性里它是灰的，由层构造算出来。
所以 `thicknessMm` 改的是某一层的宽度：结构层优先，同级取厚的，
面层保持原样——一面 138 厚的隔墙要变 200，加厚的该是龙骨而不是两侧石膏板。

**改完一定要看返回的 `layers`。** 它列出每层的功能、厚度、材质，并标出加厚落在哪层。
这个字段不是锦上添花：实测中它当场揪出一个 bug——加厚错误地落在了石膏板上，
**而总厚度 200 完全正确**，只看 `thicknessMm` 永远发现不了。

### 建模工具为什么按几何形态分，而不按构件类型

三个 `create` 工具覆盖绝大部分建模需求，具体建什么由 `category` + `typeId` 决定。
按构件类型一个个加工具的话，门、窗、梁、板各写一遍参数校验、单位换算、默认值回填、
错误信息——而它们之间真正的差异只有"调哪个 Revit API"这一行。

**签名一律收数组。** 建一圈墙请一次调用传完：一次调用 = 一个事务 = 撤销栈一步。
逐面调用建 11 面墙，用户要按 11 次 Ctrl+Z。

**尺寸由类型决定，不在实例上改。** 参数里没有 `thickness` / `width` 这类字段——
要 200 厚的墙，用 `revit_list_types` 按 `thicknessMm` 挑类型。
按尺寸动态建类型会污染项目的类型库，那是用户的资产，不该由模型随手增删。
传了不存在的字段会当场报错并列出可用参数，不会被静默忽略。

### 写工具的三条保证

- **一个调用 = 一个事务 = 撤销栈里的一步**，命名 `MCP: <工具名>`，用户看得懂也能单步撤销。
  批量建 200 面墙同样只是一步。
- **失败必回滚**，模型回到调用前的样子。工具自己抛的失败和没人预料到的异常一视同仁。
- **被吞掉的警告一定说出来**。事务里的 Revit 警告会被自动忽略（否则弹出的模态框会把
  Revit 和服务一起卡死），但每一条都会出现在返回结果的 `warnings` 字段里。
  静默吞警告比弹框更危险——模型和用户都不会知道刚才发生过什么。

影响构件数超过 `maxElementsPerWrite`（默认 500）时返回 `CONFIRMATION_REQUIRED`，
要模型带 `confirm: true` 重来。这道闸防的不是"想改 600 个"，而是"以为在改 6 个、
实际匹配到 600 个"。

### 删除为什么总要确认两次

删一面墙，墙上的门窗会跟着没——**连带删除**是删除工具最容易伤人的地方，
而 Revit 没有提供任何预演接口，依附关系（墙→门窗→标记）也无法靠遍历可靠推断。

`revit_delete_elements` 的办法是：在 `SubTransaction` 里真删一次，
记下 Revit 报告的完整影响面，然后回滚。子事务的回滚不进撤销栈，对用户完全不可见。
第一次调用因此能给出一份**真实的**清单——包括你没点名、但会被连带删掉的那些——
再要求带 `confirm: true` 重来。

代价是删除操作做了两遍。换来的是"确认"这两个字不再是走过场。

### 导出文件落在哪

`revit_export_image` 是唯一会在模型之外留下痕迹的工具，**路径不由调用方决定**：
它只收文件名，一律落在导出目录下（默认 `%LOCALAPPDATA%\RevitMCP\exports\`，
可用配置里的 `exportDirectory` 改）。带路径分隔符、`..`、盘符、非图片扩展名的写法一律拒绝。

这不是防"模型会使坏"，是防**模型被喂了坏数据**——文件名很可能来自它刚读过的某个构件名、
某段用户输入。写坏用户的文件不可逆，而限制目录几乎不损失可用性：
位置固定，用户和 Claude Code 都知道去哪儿找。

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

**`warnings` 这个字段名归管线所有，工具的输出 DTO 不要占用它。**
撞名时管线会把自己的提示改挂到 `serverWarnings`（而不是覆盖你的数据），
但那会让模型面对两个不确定的字段名。`revit_get_warnings` 因此把自己的分组叫 `groups`。

前三个项目刻意不依赖 Revit API，这不只是洁癖：**整条 HTTP + MCP 通路能在没装 Revit 的机器上
端到端测试**，CI 因此能覆盖大部分逻辑。

### 工具契约

`tests/RevitMCP.Addin.Tests` 对**每一个**工具、**每一个**参数做同一组断言：
描述不能空、判别式参数的取值必须进 schema 的 `enum`、描述里提到的工具名必须真实存在、
长度参数必须写单位、只读工具不能同时声明破坏性……

这些性质单看任何一个工具都显然成立，问题出在"每一个"上。68 个工具、900 多个参数，
靠人肉扫一遍能扫出什么，实测过一次：21 个判别式参数的取值只写在中文描述里、
schema 上是个裸 string，是手工审计才发现的。

它用 `MetadataLoadContext` 把 Addin 当**数据**读，不加载 Revit——
试过直接反射加载，工具类的基类 `McpTool<UIApplication, …>` 会去解析 RevitAPIUI，
而它又引用 AdWindows / UIFramework 等 30 个只存在于 Revit 安装目录里的程序集。
读元数据没有这个问题，而契约要检查的恰好只有元数据。

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
2026-09-16 11:42:31.882 [AUDIT] revit_create_line_based_elements [写] 被拒/WRITE_DISABLED · 0ms · elements=[5 项]
2026-09-16 11:43:07.402 [AUDIT] revit_delete_elements [写] 失败/CONFIRMATION_REQUIRED · 88ms · elementIds=["234871"]
```

审计要回答的是"模型到底被动过什么"，所以：

- **被拒的也记。** 一串被写保护拒掉的写请求本身就是值得看见的信号。
- **`REVIT_BUSY` 记「被拒」，`TIMEOUT` 记「失败」。** 前者模型没被碰过，后者可能改了一半——
  事后翻日志时这两者绝不能混为一谈。
- **入参只记摘要。** 500 个 ID 原样写进日志等于没写。
- **绕过 `logLevel`。** 把日志级别调高不该让审计悄悄消失，那恰恰是最需要它的时候。

## 用企业标准复核模型

M9 回答的是这个问题：**手里有一份院里的建模标准，怎么让它自动复核模型？**

```
standards/示例企业建模标准.md     ← 给人看的标准（自然语言条款）
standards/示例企业建模标准.json   ← 给机器跑的规则（条款编号一一对应）
standards/Audit.ps1              ← 检查器（10 种规则类型）
m9-audit.ps1 / m9-batch.ps1      ← 单个 / 批量复核，产出 Markdown 报告
```

```bash
# 复核当前模型
powershell -File workflows/m9-audit.ps1 -Standard workflows/standards/示例企业建模标准.json

# 把 Revit 里打开的所有文档都过一遍，汇总出"哪条规则最常被违反"
powershell -File workflows/m9-batch.ps1 -Standard workflows/standards/示例企业建模标准.json
```

**规则是数据，不是代码。** JSON 进版本库——能 diff、能 review、能进 PR、能按项目分支。
这和本项目拒绝内建数据库是同一条理由：与其维护一个会过期的黑盒，
不如让规则以纯文本的形式活在它该在的地方。

三条从实践里长出来的做法：

**违规必须点名到构件 ID。** 一句"命名不规范"没人能据此动手。

**规则自己崩了算不合格，不算通过。** 它没能证明模型是好的——
"没查出问题"和"没查"是两回事。

**验不了的条款要写明为什么，别假装能验。** 示例标准末尾专门有一节列出来：
"需在《警告说明表》中备案"涉及模型之外的文档；"随模型交付"是流程概念而不是模型状态。
一份标准里有多少条真能机检，本身就是有价值的信息。

还有一条是试跑之后才知道的：**标准要在自己的模型上校准**。
示例里条款 2.2 原本写"每个建筑标高都必须有楼层平面视图"，
在官方样例模型上一跑，Foundation / Ceiling / Roof Line 全被判违规——
它们确实标着"建筑楼层"，但本来就不出平面图。
**条款不是被规则推翻的，是被真实模型推翻的。**

## 工作流脚本

`workflows/` 下是把里程碑的验收标准固化成的可执行脚本。它们同时是回归测试和演示素材——
工具清单打勾很容易，能不能干完一件活是另一回事。

| 脚本 | 内容 |
|---|---|
| [`m6-closed-loop.ps1`](workflows/m6-closed-loop.ps1) | 建一圈墙（故意建错一面）→ 靠警告发现 → 预览后删掉 → 复查干净 → 选中交回用户 |
| [`m7-space.ps1`](workflows/m7-space.ps1) | 围出两间房 → 读面积与边界 → 查"房间里有什么" → 用警告与包围盒判断两面墙是否打架 |
| [`m8-delivery.ps1`](workflows/m8-delivery.ps1) | 建图纸摆视图 → 导出 PNG → 验证路径闸 → 质检 → 写出一份带截图的 Markdown 报告 |
| [`m9-audit.ps1`](workflows/m9-audit.ps1) | 把一份企业标准跑在一个模型上，产出合规报告 |
| [`m9-batch.ps1`](workflows/m9-batch.ps1) | 把同一份标准跑遍所有打开的文档，汇总出"哪条规则最常被违反" |
| [`m10-parity.ps1`](workflows/m10-parity.ps1) | 把 **68 个工具全部**在真实模型上调一遍，96 项检查逐项记分。验的是**覆盖面**而不是某一条闭环——bridge 的 110 条映射命令全部有实测走到 |
| [`McpClient.ps1`](workflows/McpClient.ps1) | 连接与调用辅助：自动发现本机实例、读取令牌、走 modern era 无状态调用 |

```bash
# 需要 Revit 正在运行、RevitMCP 服务已启动、操作模式已切到「修改模型」
powershell -ExecutionPolicy Bypass -File workflows/m6-closed-loop.ps1
```

脚本默认在 (50000, 50000) 附近作业并在结束时清理干净，可以反复运行；
加 `-KeepWalls` 可以保留结果去 Revit 里亲眼看。

> **写 PowerShell 客户端的人一定会踩的坑**：`ConvertTo-Json` 默认只展开 2 层，
> 而建模工具的入参是 `elements[].locationLine.p0.x`。用默认深度会把嵌套对象
> 序列化成 `"System.Collections.Hashtable"` 这种字符串，**而且不报错**。
> 一律带 `-Depth 10`。

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
| `%LOCALAPPDATA%\RevitMCP\exports\` | `revit_export_image` 的落脚点，可用配置里的 `exportDirectory` 改 |

---

## 开发注意

- **Ribbon 上的操作模式默认是「浏览模型」。** 此时所有写工具返回 `WRITE_DISABLED`，只有只读工具可用；
  要让模型能改，用户得手动切到「修改模型」。
- **`.ps1` 脚本必须存为 UTF-8 with BOM。** Windows PowerShell 5.1 会把无 BOM 的脚本按系统 ANSI 码页读取，
  中文会变成乱码并导致语法错误。
- **Revit API 差异只允许出现在 `src/RevitMCP.Addin/Compat/`。** 其他地方一律走那里的兼容方法，
  例如 `ElementId` 在 2024 起由 Int32 变为 Int64。
- **一切 Revit API 调用必须经 `RevitDispatcher.InvokeAsync` 编组到主线程。** 从 HTTP 线程直接碰
  `Document` 轻则抛异常、重则崩 Revit。
- **写工具里不要自己 `new Transaction`。** 管线已经开好了，再开一个会直接抛异常。
  需要多步且要对外表现为一步撤销时，用 `SubTransaction`。
- **Revit API 的 `out` 引用类型参数，一律先 `new` 一个再传。** 例如
  `NewFootPrintRoof(..., out ModelCurveArray mapping)`：不先 `new` 就抛
  `ArgumentNullException`，而且消息只有一句 "Value cannot be null."，不说是哪个参数。
  原因是 Revit API 是 C++/CLI 包装，`&` 参数实际是 tracking reference，
  被调用方先读传入的句柄再赋值。SDK 示例全都这么写，只是从不说为什么。
- **几何能表达的就别用参数表达。** 梁的「标高偏移」在常见族上是 Revit 算出来的、只读，
  写进去会静默失败；把高度做进定位线里就没有这个问题。参数可能只读，几何不会。
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
| M6 | 建模工具按几何形态重构（批量签名）、模型警告、删除、类型与标高发现、选择集、项目单位 | ✅ 完成 |
| M7 | 房间、几何最小集、空间过滤、文档身份 | ✅ 完成 |
| M8 | 视图与图纸、导出图片、明细表读写、工具行为提示 | ✅ 完成 |
| M9 | 跨文档批量审计、企业标准可执行化 | ✅ 完成 |

M6 起每个阶段的验收标准都是**一条能跑通的真实工作流**，而不是工具清单打勾——
M6 是"建错 → 自己发现 → 自己删掉 → 重建，全程不用人按 Ctrl+Z"。
那条工作流固化在 [workflows/m6-closed-loop.ps1](workflows/m6-closed-loop.ps1)，
它同时是回归测试和演示素材。

详细路线、参数设计与外部方案对照见
[docs/architecture.md §13](docs/architecture.md)。
