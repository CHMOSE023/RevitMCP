# 与 revit-bridge-addin 的能力对照

> 本文档由 `build/check-parity.ps1` 生成并校验，改了工具名会当场对不上。

`revit-bridge-addin` 共 **112** 条命令（其中 7 条在它那边是 `not_implemented` 桩）。
RevitMCP 用 **69** 个工具覆盖其中 **110** 条；**2** 条明确不做，理由在表里写着。

命令数比工具数多，是因为本项目**按几何形态与操作语义归并**：
`move` / `copy` / `rotate` / `mirror` 在 Revit 里是同一个 `ElementTransformUtils`
对整批构件施加一个变换，拆成四个工具只会让模型多记四个名字、
让同一套参数校验和失败模式重复四遍。

所有归并后的工具都收数组、走单个事务（撤销栈里只占一步）、长度一律用毫米。

表里这 110 条**每一条都有实测走到**：`workflows/m10-parity.ps1` 把 68 个工具全部调了一遍，
96 项检查，包括负例（该被拒绝的必须被拒绝）。覆盖率由插件的审计日志核算，不靠人数。

---

| bridge 命令 | RevitMCP 工具 | 说明 |
|---|---|---|
| `revit.get_view_templates` | `revit_list_view_templates` | 附带每个样板被多少视图套着 |
| `revit.get_warnings` | `revit_get_warnings` | 按种类归组 |
| `revit.get_worksets` | `revit_list_worksets` | **附带开关状态**——关闭的工作集里的构件查不到，审计前必看 |
| `revit.get_type_parameters` | `revit_get_element_parameters` | `includeTypeParameters: true` |
| `revit.get_selection` | `revit_get_selection` |  |
| `revit.get_sheet_info` | `revit_get_sheet_contents` | 视口、图签、修订、可写参数名 |
| `revit.get_snapshot_delta` | `revit_get_model_changes` | 带上上次的 token |
| `revit.list_levels` | `revit_list_levels` |  |
| `revit.list_project_parameters` | `revit_list_project_parameters` |  |
| `revit.list_shared_parameters` | `revit_list_project_parameters` | `includeUnboundShared: true` |
| `revit.list_families` | `revit_list_families` |  |
| `revit.health` | — | **不需要**。MCP 的 `initialize` 与 `tools/list` 本身就是健康检查；服务状态在 Ribbon 上 |
| `revit.invoke_method` | `revit_invoke_api` | `action: call`，重载歧义时列出候选签名而不是随便挑一个 |
| `revit.list_elements_by_category` | `revit_query_elements` |  |
| `revit.get_mep_systems` | `revit_list_mep_systems` | **bridge 这条是 not_implemented 桩** |
| `revit.get_parameter_value` | `revit_get_element_parameters` | 用 `nameContains` 过滤 |
| `revit.get_phase_filters` | `revit_list_phases` | 在 `filters` 字段里 |
| `revit.get_link_instances` | `revit_list_links` | 每个链接的实例 ID 都在 `instanceIds` 里 |
| `revit.get_element_type` | `revit_get_element_parameters` | `includeTypeParameters: true`；要挑类型用 revit_list_types |
| `revit.get_elements_by_type` | `revit_query_elements` |  |
| `revit.get_group_members` | `revit_list_groups` | `includeMembers: true` |
| `revit.get_room_boundary` | `revit_list_rooms` | 边界在房间信息里。**bridge 这条是 not_implemented 桩** |
| `revit.get_rvt_links` | `revit_list_links` | 含载入状态与可直接查询的 `linkedDocumentId` |
| `revit.get_schedule_data` | `revit_read_schedule` |  |
| `revit.get_revision_sequences` | `revit_list_revisions` | 附带每个修订下的云线数 |
| `revit.get_phases` | `revit_list_phases` | 附带每个阶段创建/拆除了多少构件 |
| `revit.get_project_location` | `revit_get_project_location` | **bridge 这条是 not_implemented 桩** |
| `revit.get_render_settings` | — | **未实现**。bridge 这条也是桩——Revit API 不提供渲染设置的读写入口，两边都做不到 |
| `revit.rotate_element` | `revit_transform_elements` | `operation: rotate`，角度用度 |
| `revit.save_document` | `revit_save_document` | 另存为需要显式路径，覆盖需要 `overwrite: true` |
| `revit.set_element_material` | `revit_set_element_parameters` | 材质参数是 ElementId 类型，`value` 传材质 ID（用 revit_list_materials 查） |
| `revit.replace_family_type` | `revit_change_element_types` | `fromTypeId` + `toTypeId` |
| `revit.relinquish_all` | `revit_sync_to_central` | `action: relinquish` |
| `revit.render_3d_view` | `revit_export_image` | **bridge 这条的实现本身就是图片导出**，它自己的返回值里也这么写着 |
| `revit.renumber_sheets` | `revit_update_sheets` | **编号互换或循环移位时自动走两阶段**，不会中途撞号失败 |
| `revit.tag_all_in_view` | `revit_tag_all_in_view` | 默认跳过已有标记；标不了的构件会被数出来 |
| `revit.ungroup` | `revit_group_elements` | `operation: ungroup` |
| `revit.unpin_element` | `revit_set_elements_pinned` | `pinned: false` |
| `revit.sync_to_central` | `revit_sync_to_central` | `confirm` 是必填布尔值，不是「超限才要」的可选项 |
| `revit.set_parameter_value` | `revit_set_element_parameters` | 批量签名 |
| `revit.set_selection` | `revit_set_selection` |  |
| `revit.set_type_parameter` | `revit_set_type_parameters` | 回执给出实际波及的构件数，规模闸也按它算 |
| `revit.move_element` | `revit_transform_elements` | `operation: move` |
| `revit.open_document` | `revit_open_document` | `action: open`，支持分离方式打开 |
| `revit.pin_element` | `revit_set_elements_pinned` | `pinned: true` |
| `revit.mirror_element` | `revit_transform_elements` | `operation: mirror`，`keepOriginal` 控制留不留原件 |
| `revit.list_sheets` | `revit_list_views` | `viewType: DrawingSheet` |
| `revit.list_titleblocks` | `revit_list_types` | `category: OST_TitleBlocks` |
| `revit.list_views` | `revit_list_views` |  |
| `revit.populate_titleblock` | `revit_update_sheets` | `parameters` 写图纸本身，`titleBlockParameters` 写图签实例 |
| `revit.reflect_get` | `revit_invoke_api` | `action: get` |
| `revit.reflect_set` | `revit_invoke_api` | `action: set` |
| `revit.place_window` | `revit_create_point_based_elements` | `category: OST_Windows` |
| `revit.place_door` | `revit_create_point_based_elements` | `category: OST_Doors`，可自动找宿主墙 |
| `revit.place_family_instance` | `revit_create_point_based_elements` | 任意可载入族 |
| `revit.place_viewport_on_sheet` | `revit_add_views_to_sheet` | 回读实际位置并检查有没有落到纸外 |
| `revit.create_floor_plan_view` | `revit_create_views` | `viewType: floorPlan` |
| `revit.create_foundation` | `revit_create_point_based_elements` | `category: OST_StructuralFoundation`，结构类型自动推断为 Footing |
| `revit.create_grid` | `revit_create_datums` | `kind: grid`，给 `arcPoint` 就是弧形轴 |
| `revit.create_floor` | `revit_create_surface_based_elements` | `category: OST_Floors` |
| `revit.create_conduit` | `revit_create_mep_curves` | `kind: conduit` |
| `revit.create_dimension` | `revit_create_annotations` | `kind: dimension`。Reference 解析按面优先、退回整构件时会警告 |
| `revit.create_duct` | `revit_create_mep_curves` | `kind: duct` |
| `revit.create_pipe` | `revit_create_mep_curves` | `kind: pipe` |
| `revit.create_project_parameter` | `revit_create_project_parameter` | 见下方「参数定义」一节的说明 |
| `revit.create_revision_cloud` | `revit_create_annotations` | `kind: revisionCloud`。**bridge 这条是 not_implemented 桩** |
| `revit.create_new_document` | `revit_open_document` | `action: new` |
| `revit.create_group` | `revit_group_elements` | `operation: create` |
| `revit.create_level` | `revit_create_datums` | `kind: level`，**默认连楼层平面一起建** |
| `revit.create_material` | `revit_create_materials` | 批量；强烈建议 `copyFromId`，空白新建的材质没有物理与外观资源 |
| `revit.calculate_material_quantities` | `revit_calculate_material_quantities` | 多了阶段过滤与「有几个构件根本没有用量」的提示 |
| `revit.change_element_type` | `revit_change_element_types` | 批量；还可以用 `fromTypeId` 按原类型整批换 |
| `revit.check_clashes` | `revit_check_clashes` | 多了跨链接模型与阶段过滤 |
| `revit.batch_set_parameters_by_filter` | `revit_batch_set_parameters` | `updates[].filter`，支持类别/名称/类型/标高/阶段/设计选项/参数值 |
| `revit.apply_view_template` | `revit_apply_view_template` | 批量套/取消，样板与视图类型不匹配会整批拒绝 |
| `revit.batch_create_sheets_from_csv` | `revit_create_sheets` | 批量签名。CSV 解析由调用方做——把文件格式塞进工具会让它长出一堆与 Revit 无关的参数 |
| `revit.batch_set_parameters` | `revit_batch_set_parameters` | `updates` 里每条可以改不同构件的不同参数 |
| `revit.create_beam` | `revit_create_line_based_elements` | `category: OST_StructuralFraming` |
| `revit.create_cable_tray` | `revit_create_mep_curves` | `kind: cableTray`。**bridge 这条是 not_implemented 桩** |
| `revit.create_column` | `revit_create_point_based_elements` | `category: OST_StructuralColumns`，结构类型自动推断为 Column |
| `revit.create_3d_view` | `revit_create_views` | `viewType: threeD`，可选透视 |
| `revit.close_document` | `revit_close_document` | 关活动文档时自动先切到另一个已保存的文档 |
| `revit.convert_to_group` | `revit_group_elements` | `operation: create` |
| `revit.copy_element` | `revit_transform_elements` | `operation: copy` |
| `revit.export_navisworks` | `revit_export_documents` | `format: nwc`；没装 Navisworks 导出器会明确告知 |
| `revit.export_pdf_by_sheet_set` | `revit_export_documents` | `format: pdf`。**需要 Revit 2022+**，更早版本 API 没有 PDF 导出 |
| `revit.export_schedules` | `revit_export_schedules` | 导多张时自动按明细表名区分文件 |
| `revit.export_image` | `revit_export_image` |  |
| `revit.execute_python` | `revit_execute_script` | **用 C# 而非 Python**，理由见下方「逃生舱」一节 |
| `revit.export_dwg_by_view` | `revit_export_documents` | `format: dwg` |
| `revit.export_ifc_with_settings` | `revit_export_documents` | `format: ifc`，支持版本与基本量 |
| `revit.get_element_bounding_box` | `revit_get_element_geometry` | 包围盒是它的一部分 |
| `revit.get_element_geometry` | `revit_get_element_geometry` | **不返回网格**——网格对模型没有用，只会吃光上下文 |
| `revit.get_element_parameters` | `revit_get_element_parameters` | 同时给原始值与带单位的显示值 |
| `revit.get_document_info` | `revit_get_document_info` |  |
| `revit.extract_snapshot` | `revit_get_model_changes` | 省略 `since` 即取基线 token。**不落盘全模型快照**，理由见下 |
| `revit.get_categories` | `revit_list_categories` | 只列实际有构件的类别并给出数量 |
| `revit.get_design_options` | `revit_list_design_options` | 附带每个选项里的构件数与主模型规模 |
| `revit.create_shared_parameter` | `revit_create_project_parameter` | API 只能造共享参数，两条命令在 Revit 里本就是同一件事 |
| `revit.create_sheet` | `revit_create_sheets` | 批量签名 |
| `revit.create_tag` | `revit_create_annotations` | `kind: tag` |
| `revit.create_section_view` | `revit_create_views` | `viewType: section`。变换按右手系构造，见下方说明 |
| `revit.create_roof` | `revit_create_surface_based_elements` | `category: OST_Roofs` |
| `revit.create_room` | `revit_create_rooms` | 回执直接给出面积，面积为 0 会点出「没围合」 |
| `revit.create_schedule` | `revit_create_schedule` |  |
| `revit.delete_sheet` | `revit_delete_elements` | 图纸就是一种视图，走同一个删除工具 |
| `revit.duplicate_sheet` | `revit_duplicate_sheets` | `contents` 对应 Revit 自己的四种复制语义；2021 及以下退回手工复制并在回执里注明 |
| `revit.edit_family` | `revit_open_document` | `action: editFamily`；内建族与系统族会给出说得清的拒绝 |
| `revit.delete_element` | `revit_delete_elements` | 先在子事务里真删一次拿到连带影响，回滚后要求 confirm |
| `revit.create_text_note` | `revit_create_annotations` | `kind: textNote`，宽度越界会提前给出允许范围 |
| `revit.create_text_type` | `revit_duplicate_type` | 从已有文字类型复制并改参数。**bridge 这条是 not_implemented 桩** |
| `revit.create_wall` | `revit_create_line_based_elements` | `category: OST_Walls` |

---

## 本项目额外提供、bridge 没有的

| 能力 | 为什么需要 |
|---|---|
| `revit_list_documents` + 所有只读工具的 `documentId` | 一个 Revit 能同时开十几个模型。bridge 的命令全部写死 `ActiveUIDocument`，做不了跨文档批量审计 |
| `revit_get_project_units` | 工具单位与项目显示单位是否一致。不一致时读数会被误解 |
| `revit_list_schedulable_fields` | 某类别做明细表能选哪些字段。字段名随项目语言变化，猜不出来 |
| `revit_list_materials` | 设材质要先有材质 ID |
| `revit_list_groups` | 含同类型实例数——改一个组会联动改到几个 |
| `revit_activate_view` | 切换活动视图。它受写保护管辖但不开事务，是工具的第三类 |
| `revit_query_elements` 的 `withinBox` / `near` | 空间过滤，走 Revit 的空间索引 |
| `revit_query_elements` 的 `phaseId` / `designOptionId` | 改造与多方案项目统计的正确性前提 |
| `revit_get_element_parameters` 的 `structuralType` | 它不是参数，属性面板上看不到。一根结构行为缺失的柱子只有这个字段能认出来 |

## 几处刻意的不同

### 结构类型不能一律填 NonStructural

bridge 的 `create_column` / `create_foundation` 各自传了正确的 `StructuralType`，
而本项目的 `revit_create_point_based_elements` 早期把它硬编码成了 `NonStructural`。
那会得到一个**看起来对、但没有结构行为也没有分析模型**的构件——
结构专业的明细表、荷载与分析全会漏掉它，而模型上完全看不出异样。
现在按类别推断（结构柱→Column、梁→Beam、基础→Footing），也可以用 `structuralType` 显式指定。

**这个缺陷从模型外部本来是看不见的**：实测比对过同一族分别以 NonStructural 与 Column 建出的两根柱子，
29 个实例参数一字不差。所以 `revit_get_element_parameters` 专门把 `structuralType` 单独交了出来。

### 剖面视图的变换必须是右手系

bridge 的 `create_section_view` 把 `BasisZ` 拼成了 `(-dy, dx, 0)`，
而 `BasisX × BasisY` 算出来是 `(dy, -dx, 0)`——差一个负号，构成左手系。
本项目一律用 `BasisX.CrossProduct(BasisY)` 算 `BasisZ`，
并且剖面范围由 `bottomMm` / `topMm` / `depthMm` 给出，而不是硬编码的 ±10 英尺。

### 参数定义：API 造不出「非共享」的项目参数

Revit 的参数绑定需要一个 `Definition`，而 API 能拿到的 `Definition` 只有共享参数文件里的那种。
UI 上那个「项目参数（非共享）」选项在 API 里没有对应入口。
所以 `revit_create_project_parameter` 实际做的是：往共享参数文件里写一条定义，再绑定到项目。
**共享参数文件是项目之外的一个文件**，交付时漏掉它，别人打开项目会看到参数但对不上——
回执里因此一定会给出它的路径。

### 快照：不落盘全模型

bridge 的 `extract_snapshot` 把整个模型序列化成文件。本项目的 `revit_get_model_changes`
改为订阅 Revit 的 `DocumentChanged` 事件，只回答「从上次的 token 起改了什么」。
理由是全模型快照的代价随模型大小线性增长，而「完整状态」这个问题
`revit_query_elements` 本来就能回答，没必要再存一份必然会过期的副本。

记录容量有限（每文档 2 万条），超出后旧记录会被丢弃，此时回执的 `incomplete` 为 true——
**这一位必须看**，它意味着拿到的改动列表是残缺的。

### 逃生舱：C# 而不是 Python，且单独一道闸

`revit_invoke_api` 与 `revit_execute_script` 绕开了本项目其余部分的全部保证——
没有单位换算、没有校验、没有规模闸。它们的影响面根本不止于「改模型」：
能读写任何文件、发任何网络请求。所以它们**不跟着 Ribbon 上的写入开关走**，
需要在 `config.json` 里单独把 `escapeHatchEnabled` 设为 true。

用 C# 而非 Python，是因为本项目全程零第三方依赖：Revit 把所有插件加载进同一个 AppDomain
且不应用插件自己的绑定重定向（架构 §2 的 C2），引入 IronPython 就必须同时引入
ILRepack 那一整套内联化设施。而 .NET Framework 自带的 `CSharpCodeProvider` 提供同样的
「执行任意代码」能力、零依赖。

### 版本覆盖

bridge 主要针对 Revit 2024。本项目覆盖 2019–2024 六个版本，
版本差异全部收在 `src/RevitMCP.Addin/Compat/` 下：

| 差异 | 换代版本 | 收在哪 |
|---|---|---|
| `ElementId` 由 Int32 变 Int64 | 2024 | `ElementIdCompat` |
| `Floor.Create` / `Ceiling.Create` 取代 `NewFloor` | 2022 | `SurfaceCompat` |
| `GetTaggedLocalElementIds` 取代 `TaggedLocalElementId` | 2022 | `TagCompat` |
| `ViewSheet.Duplicate` 才出现 | 2022 | `SheetCompat` |
| `PDFExportOptions` 才出现 | 2022 | `PdfExportCompat` |
| `ForgeTypeId` 取代 `ParameterType` / `BuiltInParameterGroup` | 2022 | `ParameterDefinitionCompat` |

用不了的能力会给出说得清的拒绝（如 2019 导 PDF），而不是悄悄降级产出一个残缺的结果。
