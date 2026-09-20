# M10 路线图 · 建模能力

> 目标形态：让模型**建得出、改得动、存得住、出得了图**。
> 状态：**M10-A 已完成并在 Revit 2019 实测通过**（2026-09-17），B / C 待做。
> 上游是 M9（跨文档批量审计）。工具数 28 → 32（17 只读 + 15 写）。
> 依据：2026-09-16 的一次完整建模实测（见 §0）。

---

## 0. 排序依据：这次实测卡在哪

沿用 M6~M9 的做法——**排序依据不是 Revit API 的分类，而是"当前卡在哪"**。

M9 之后做了一次完整实测：给一张建筑参考图（10 m × 8 m 两层小办公楼，附平面 / 立面 / 剖面
与标题栏参数），要求只通过 MCP 把它建出来。结果是 9 次写调用建成 96 个图元，
主体几何、房间划分、门窗布置与参考图一致，11 个房间全部成功围合。

**体量模型这一关过了。** 暴露的是它后面的一切：

1. **存不住。** 96 个图元建完，没有任何工具能落盘，只能请用户去 Revit 里按 Ctrl+S。
   一个能写模型却不能保存的 MCP，自动化链条是断的。
2. **改不动。** 二层的 12 道墙、13 樘门窗全部靠手写坐标重打了一遍——
   这在 Revit 里本是一次"与选定标高对齐粘贴"。没有任何图元变换能力。
3. **没有骨架。** 项目至今没有"屋面"这条标高线，屋面板靠"标高 2 + 3300 偏移"吊着。
   所有创建工具都吃 `levelId`，而这个 ID 只能从样板里现成的标高取。
4. **高度全靠手算。** 墙只有 `height`（无连接高度），没有顶部约束。
   为了让内墙不穿板，手工算出 3450 / 3150 这种数；为了让外皮准确落在 10000×8000，
   手工把外墙中心线内偏了 100。算错不会报错，只会静默差 100。
5. **出的图没法看。** 8 张导出图每张 70~90% 是白的——`revit_export_image` 没有取景控制，
   导的是视图当前范围。而这个工具的定位是"截图能落盘、能进报告、能进 PR"。

**验收方式沿用 M6 定下的规矩**：每个阶段的验收定成"一个能跑通的真实工作流"，
而不是工具清单打勾，并把那条工作流固化成脚本留在 `workflows/` 下。
M10 的三个阶段各对应一条。

---

## 1. 里程碑拆分

| 阶段 | 内容 | 验收工作流 |
|---|---|---|
| **M10-A 骨架与落盘** ✅ | 保存、标高与轴网、标高约束体系、带洞面构件 | [`workflows/m10-authoring.ps1`](../workflows/m10-authoring.ps1)：新建屋面标高 → 按外皮尺寸建墙并顶到标高 → 建带梯井的板 → 存盘 |
| **M10-B 复用与材质** | 图元变换、材质发现与赋予、载入族 | `workflows/m10-reuse.ps1`：建一层 → 跨标高复制出二层 → 外墙赋石材 → 载入并放置一个族 |
| **M10-C 出图** | 导出取景、创建视图、视图属性 | `workflows/m10-presentation.ps1`：建剖面视图 → 设着色 + 详细程度 → 导出四立面与剖面，每张构件占比达标 |

三个阶段可以独立交付。A 是硬阻塞，B 决定效率与质量，C 决定交付物能不能看。

---

## 2. M10-A · 骨架与落盘

### 2.1 `revit_save_document` / `revit_save_document_as`

**这一条最便宜也最刺眼。**

实现上有一个坑：**Revit 不允许在打开的事务里保存文档**。好在框架里已经有现成机制——
`McpToolAttribute.WithoutTransaction`（`revit_activate_view` 用的就是它），
语义正好是"受写保护管辖但不开事务"。直接复用，不需要动执行管线。

| 工具 | `Destructive` | 说明 |
|---|---|---|
| `revit_save_document` | `false` | 保存当前文档。覆盖的是用户自己的文件，但这正是用户要的 |
| `revit_save_document_as` | `true` | 另存。路径**不由调用方决定**，照 `ExportPaths` 的先例约束在配置目录下 |

`save_as` 的路径约束沿用 `revit_export_image` 那套判据：能写文件和能写任意磁盘位置，
不是同一件事，中间那道闸不能省。

### 2.2 `revit_create_levels` / `revit_create_grids`

标高是所有创建工具的入参基础，三个 `create_*` 工具全都吃 `levelId`。
没有它，模型只能在样板给定的几条标高之间腾挪。

```jsonc
// create_levels
{ "levels": [ { "name": "屋面", "elevationMm": 6900 } ], "confirm": false }
```

轴网优先级略低于标高（不影响几何正确性，影响的是出图与可读性），但实现成本很接近，
建议同批做掉。两者都走 `RequireBatch` 的批量原子语义，与现有创建工具保持一致。

### 2.3 标高约束体系（扩展现有工具，不是新工具）

**本阶段技术价值最高的一项**，对应实测缺口 3 和 4。

现状：

- `CreateLineBasedTool` 的墙只有 `height`，是无连接高度
- `CreatePointBasedTool` 的自由实例路径把 `StructuralType.NonStructural` **写死**了，
  且只有 `baseOffset` 没有顶部约束

后者的后果值得单说：传 `OST_StructuralColumns` 调用**会成功**，但建出来的是一根
非结构的、按类型默认高度立着的、不随层高联动的柱子。这不是"能建结构柱只是文档没写"，
而是"能建出一个看起来像柱子的东西"——比失败更坏的结果，和当初拒绝"没有宿主就放个自由门窗"
是同一类判断。

要加的：

| 工具 | 新增字段 | 说明 |
|---|---|---|
| `create_line_based` | `topLevelId` + `topOffsetMm` | 与 `height` 互斥，给了就用约束，二者都不给才落到默认 3000 |
| `create_line_based` | `locationLineRef` | 核心层中心 / 面层外表面 / 核心层外表面… |
| `create_point_based` | `topLevelId` + `topOffsetMm` | 柱的顶部约束 |
| `create_point_based` | 按 category 映射 `StructuralType` | 柱→`Column`，梁→`Beam`，其余保持 `NonStructural` |

`locationLineRef` 单独说一句：这次为了外皮准确，是调用方在心算偏移。
这类"算错了不会报错、只会静默差 100"的事，正是应该由工具承担的——
和 M6 决定让工具回填默认值并通过 `warnings` 说出来，是同一条原则。

### 2.4 `create_surface_based` 支持 `innerLoops`

`SurfaceBoundary` 目前只有 `OuterLoop`。这次为了留梯井，二层楼板被拆成 4 块，
代价是 4 条"楼板重叠"警告 + 2 条"无法创建分析模型"，而且梯井位置一改就要重建 4 块板。

比单独做 `create_openings` 便宜得多：`BuildBoundary` 的闭合校验与最小段数校验已经写好，
加一层循环复用即可，每个内环走同一套校验。

真正的洞口工具（墙洞、竖井、面洞口）留到 M11——**带洞楼板先解决 80% 的场景**。

### 2.5 实现记录

代码已落，六版本矩阵全编译，测试 215 → 224，
**2026-09-17 在 Revit 2019 上实测通过**（`workflows/m10-authoring.ps1` 全绿）。

三处与上面的规划不同。前两处是实现时发现规划想简单了，第三处是实测才暴露的：

#### **[M10-A 已修订]** 定位线改成"先建、再问 Revit、再挪"

原计划是按墙厚把调用方给的线换算成中心线再交给 `Wall.Create`。
换算要用外法线，而外法线可以写成 `方向 × Z` 这样一个叉乘——**问题出在符号上**。
推导给出的是"逆时针给一圈线、外表面朝外"，而 Revit 社区通行的说法是
"顺时针画墙外表面朝外"；两者对不上，且本机没有 Revit 可以当场验证。

猜错的代价恰好是这个功能要消灭的那一类：每面外墙静默偏半个墙厚，方向还正好反。
所以改成不猜——先按中心线把墙建出来，`Regenerate` 之后读
`Wall.Orientation`（Revit 自己给出的外表面法线），再用
`ElementTransformUtils.MoveElement` 整体挪到位。无论内部约定是哪一种都对。

代价是每面需要定位线的墙多一次重生成。**用一点性能换掉一个没法在本机验证的假设，值。**
见 [`WallReference.cs`](../src/RevitMCP.Addin/Tools/WallReference.cs)。

> **实测回填。** 200 厚墙围 10000 × 8000，量一圈墙的包围盒：
> 顺时针给定位线得 10000 × 8000，逆时针得 10400 × 8400。
>
> 也就是说当初推导出的符号**确实是反的**，而工具因为没依赖它、一次就对了。
> 第一版工作流按错误的推导逆时针给了轮廓，于是量出 10400——
> **错的是验收脚本，被工具量了出来**，这正是"验收要量回来、不能调用没报错就算过"的意义。
> 对外的说法现在是实测结论：**外轮廓顺时针给**。

#### **[M10-A 已修订]** 内环在 2022 前后是两条实现路径

`Floor.Create`（2022+）和 `Ceiling.Create` 原生收多个 `CurveLoop`，洞是轮廓的一部分；
而 `NewFloor`（≤2021）和 `NewFootPrintRoof`（**所有版本**）只收一个轮廓，
洞只能建完之后用 `NewOpening` 单独开。

两条路的模型结果不一样：后者会多出 `Opening` 构件，按类别查楼板时看不见它们。
这个差异瞒不住也不该瞒——`SurfaceCompat` 把开了几个洞原样报上来，
工具层转成一条警告说清楚。屋顶在任何版本上都走后一条路。

#### **[M10-A 实测修订]** 开洞前后各要一次 `Regenerate`

规划里以为 `NewFloor` + `NewOpening` 是顺理成章的两步。实测第一次跑，带洞楼板
**整个事务回滚**，而且回滚得毫无线索——工具只回了一句
"事务未能提交（状态：RolledBack），模型未被修改"。

隔离出来的原因：`NewFloor` 刚返回时楼板**还没有几何**，
拿它当宿主开洞，Revit 既不抛异常、也不走失败预处理器，
而是一路拖到事务提交时才回滚。

所以 `Punch` 前后各加一次 `Regenerate`：

- 前面那次让宿主真的成为一个能被开洞的面；
- 后面那次把开洞本身的失败**逼进事务里暴露**，否则错误信息永远说不出是哪一步崩的。

顺带确认：`NewOpening` 自己处理内环绕向，顺时针逆时针都能开出洞来。

#### 实测结果

2026-09-17，Revit 2019，`RevitMCPDemo.rvt`：

| 要验的事 | 结果 |
|---|---|
| `create_levels` 建得出、查得到 | ✅ |
| `create_grids` 建得出，**轴号重名整批回滚** | ✅ 拒绝信息带上了 Revit 原话 |
| 墙 `topLevelId` 顶部约束 | ✅ 包围盒高 7000，分毫不差 |
| `locationLineRef` 外表面定位 | ✅ 包围盒正好 10000 × 8000 |
| 结构柱 `StructuralType.Column` + 顶部约束 | ✅ 柱高 3600 = 两标高之差 |
| 带洞楼板 `innerLoops` | ✅ 面积 72.8 ㎡ = 80 − 7.2 |
| `save_document` 不带 confirm 不落盘 | ✅ |
| `save_document_as` 扩展名白名单与目录边界 | ✅ 拒绝 `.png`、拒绝 `..\` |

尚未实测：`save_document` 带 confirm 的真实写盘、`save_document_as` 的真实另存
（前者覆盖用户的 .rvt，后者会把活动文档切到副本上），以及
2022+ 上 `Floor.Create` 原生多环那条路径——R19 走的是 `NewOpening` 那条。

---

## 3. M10-B · 复用与材质

### 3.1 `revit_transform_elements`

对应实测缺口 2，是整个 M10 里投入产出比最高的一项。

**做成一个工具带 `operation` 字段**，不要拆成 `move` / `copy` / `rotate` / `mirror` / `array`
五个工具。它们共享同一套骨架——解析 ID 集合 → 校验可变换 → 调 `ElementTransformUtils`
→ 批量原子提交——拆开就是五份重复的参数校验、单位换算和错误信息，
正是 M6 重构 `create_wall` 时否掉的那条路。

```jsonc
{
  "operation": "copy",           // move | copy | rotate | mirror | array
  "elementIds": ["...", "..."],
  "targetLevelId": "...",        // copy 专用：与选定标高对齐粘贴
  "translationMm": { "x": 0, "y": 0, "z": 0 },
  "confirm": false
}
```

其中必须做好的是 **copy + `targetLevelId`**（`ElementTransformUtils.CopyElements` 配标高偏移），
对应 Revit 里的"与选定标高对齐粘贴"。这一个动作就能把本次二层的重复劳动清零。

### 3.2 材质：两件小事，不是一个大工具

材质是与参考图观感差距最大的一项（参考图是浅色石材 + 竖向木格栅 + 深色铝框，
模型全是样板默认灰）。但它的解法不是一个 `set_material` 大工具，而是两件小事：

**(a) `revit_list_materials`——这是真正的阻塞点。**

`Material` 派生自 `Element` 而非 `ElementType`，所以 `ListTypesTool` 的
`.WhereElementIsElementType()` 拿不到它；`query_elements` 的 `OfCategory` 路径对材质也不可靠。
**当前工具集里材质是完全不可见的**——即使 `ParameterWriter` 已经支持写 `ElementId` 参数，
调用方也拿不到那个 ID。先把发现补上。

**(b) `duplicate_type` 的 `layers` 支持 `materialName`。**

墙 / 板的材质在 `CompoundStructure` 的层里，不是实例参数，
所以 `set_element_parameters` 天然够不着。而 `DuplicateTypeTool.ApplyThickness`
**已经在操作 `CompoundStructure` 了**——顺着加一个按层设材质是自然的扩展，不需要新工具。
它现有的"把改了哪一层说出来"的输出约定同样适用。

### 3.3 `revit_load_family`

项目里没有的族根本用不了。这次只能在样板自带的 4 个门型、12 个窗型里挑，
落地大玻璃、横向长窗都做不出来。

安全上照 `ExportPaths` 的先例办：**不接受任意路径**，只在配置的族库目录 +
Revit 自带库里按名字解析。理由和 `export_image` 一样——能载入族和能读任意磁盘文件，
不该是同一件事。

---

## 4. M10-C · 出图

### 4.1 `revit_export_image` 加取景

对应实测缺口 5，改动最小但影响每一次导出。

`ExportImageInput` 目前只有 `viewId / fileName / pixelWidth / imageType`，
导出的是视图当前范围，于是 8 张图每张 70~90% 是白的。
多模态模型读一张 80% 是空白的图，等于把有效分辨率也砍掉 80%。

加 `fitToElements`（导出前对视图做一次 ZoomToFit），并把现有的 `visibleElementCount`
往前推一步：**构件占画面比例过低时挂一条 `warnings`**，
和"为 0 说明导出的是一张空图"那条提示是同一个思路。

### 4.2 `revit_create_views` + 视图属性

现在只能用样板已有视图，后果是：拿不到剖面图（参考图有 1-1 剖面）、
平面立面全是隐藏线（样板里恰好有一个着色三维视图，才侥幸拿到一张着色图）、
立面裁剪框把屋顶切掉且改不了。

| 工具 | 覆盖 |
|---|---|
| `revit_create_views` | 剖面、立面、三维相机、平面 |
| 视图属性（并入 `set_element_parameters` 或独立工具） | 显示样式、详细程度、视图范围、裁剪框开关与范围 |

视图属性放哪里需要在实现时定：视图是 `Element`，理论上 `set_element_parameters` 能写，
但显示样式、裁剪框这类不是普通参数，多半要独立工具。**实现前先在 Revit 里试一遍**，
不要照 API 文档拍脑袋——这条在 `DuplicateTypeTool` 关于 `CanLayerWidthBeNonZero` 的注释里
已经有过一次教训。

---

## 5. 暂不做，及为什么

### 5.1 楼梯与栏杆 → M11 之后

这次实测是拿 17 块楼板拼的踏步，既脏又不可编辑、无栏杆、无上下行标注。
直觉上它该排在很前面。**但它的技术成本被低估了，判断是降到 M11 之后。**

Revit 的楼梯 API 要走 `StairsEditScope`，而 `StairsEditScope.Start()`
**要求当前没有打开的事务**——它自己管理事务。这和 `RevitWriteScope` 把每个写工具
整个包进一个 `Transaction` 的模型**直接冲突**。

要做楼梯，得先在执行管线上开出第三类工具。现在只有两类：

| 类别 | 标志 | 事务 |
|---|---|---|
| 只读 | `ReadOnly = true` | 无 |
| 事务内 | 默认 | 框架开一个 `Transaction` 包住 |
| ~~自管理事务~~ | **不存在** | 工具自己开自己提交 |

`WithoutTransaction` 不等价——它是"不要事务"，而楼梯是"要自己开"，
两者在回滚语义上完全不同：`WriteScope` 现在承诺"工具失败就当它没发生过"，
自管理事务的工具没法由框架兑现这个承诺。

这是一个执行管线的架构改动，不是加一个工具。**建议等 M10 三个阶段落完、
标高约束体系稳定之后再动**——那时也更清楚"自管理事务"这一类到底还有谁需要，
免得为楼梯一个用例开一条抽象。

### 5.2 其余待排

按当前判断的次序：幕墙网格 / 竖梃控制 → `create_dimensions` / `tags` / `text`
→ `join_geometry` 与墙顶附着 → 洞口与竖井 → 地形与场地构件 → 屋面找坡。

---

## 6. 非目标

- **不做"看图建模"**。参考图的读取与拆解是客户端（模型）的事，
  MCP 只负责把拆解结果落进 Revit。工具层不引入任何图像处理。
- **不做方案生成 / 自动布局**。工具给的是能力，不是设计决策。
- **不追求出施工图**。M10-C 的目标是"导出的图能看、能进报告"，不是"能盖章"。

---

## 7. 工具数量预估

| | M9 | M10-A 后（当前） | M10 全部完成后 |
|---|---|---|---|
| 只读 | 17 | 17 | 18（`list_materials`） |
| 写 | 11 | 15（+save ×2、levels、grids） | 18（+transform、load_family、create_views） |
| **合计** | **28** | **32** | **36** |

另有 5 个现有工具的扩展：`create_line_based`、`create_point_based`、
`create_surface_based`、`duplicate_type`、`export_image`——
**扩展多于新增，这是个好迹象**：说明 M6 按几何形态抽象的那次重构，形状是对的。
