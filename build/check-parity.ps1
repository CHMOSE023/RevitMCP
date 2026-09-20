<#
.SYNOPSIS
    校验并生成「与 revit-bridge-addin 的能力对照」文档。

.DESCRIPTION
    映射表写在这个脚本里（$Mapping），文档由它生成。
    校验做三件事，任何一件不过就报错退出：

      1. bridge 源码里的每一条命令，映射表里都有；
      2. 映射表里的每一条，bridge 源码里都还在（对方删了命令要能发现）；
      3. 映射到的每个工具名，RevitMCP 源码里确实存在（改了工具名要能发现）。

    第 3 条是重点。工具改名之后对照表照样能生成，只是内容全错——
    那种文档比没有更危险，因为它看起来仍然是对的。

.PARAMETER BridgeSource
    revit-bridge-addin 的 src 目录。默认 D:\aec-model-bridge\packages\revit-bridge-addin\src

.PARAMETER Output
    生成的文档路径。默认 docs\bridge-parity.md

.PARAMETER CheckOnly
    只校验，不写文件。CI 里用这个。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\check-parity.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\check-parity.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [string] $BridgeSource = 'D:\aec-model-bridge\packages\revit-bridge-addin\src',
    [string] $Output,
    [switch] $CheckOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $repoRoot 'docs\bridge-parity.md' }

# ==================== 映射表 ====================
#
# Tool 为 $null 表示「明确不做」，Note 里写清楚为什么。

$Mapping = @(
    @{ Command = 'revit.apply_view_template';               Tool = 'revit_apply_view_template';           Note = '批量套/取消，样板与视图类型不匹配会整批拒绝' }
    @{ Command = 'revit.batch_create_sheets_from_csv';      Tool = 'revit_create_sheets';                 Note = '批量签名。CSV 解析由调用方做——把文件格式塞进工具会让它长出一堆与 Revit 无关的参数' }
    @{ Command = 'revit.batch_set_parameters';              Tool = 'revit_batch_set_parameters';          Note = '`updates` 里每条可以改不同构件的不同参数' }
    @{ Command = 'revit.batch_set_parameters_by_filter';    Tool = 'revit_batch_set_parameters';          Note = '`updates[].filter`，支持类别/名称/类型/标高/阶段/设计选项/参数值' }
    @{ Command = 'revit.calculate_material_quantities';     Tool = 'revit_calculate_material_quantities'; Note = '多了阶段过滤与「有几个构件根本没有用量」的提示' }
    @{ Command = 'revit.change_element_type';               Tool = 'revit_change_element_types';          Note = '批量；还可以用 `fromTypeId` 按原类型整批换' }
    @{ Command = 'revit.check_clashes';                     Tool = 'revit_check_clashes';                 Note = '多了跨链接模型与阶段过滤' }
    @{ Command = 'revit.close_document';                    Tool = 'revit_close_document';                Note = '关活动文档时自动先切到另一个已保存的文档' }
    @{ Command = 'revit.convert_to_group';                  Tool = 'revit_group_elements';                Note = '`operation: create`' }
    @{ Command = 'revit.copy_element';                      Tool = 'revit_transform_elements';            Note = '`operation: copy`' }
    @{ Command = 'revit.create_3d_view';                    Tool = 'revit_create_views';                  Note = '`viewType: threeD`，可选透视' }
    @{ Command = 'revit.create_beam';                       Tool = 'revit_create_line_based_elements';    Note = '`category: OST_StructuralFraming`' }
    @{ Command = 'revit.create_cable_tray';                 Tool = 'revit_create_mep_curves';             Note = '`kind: cableTray`。**bridge 这条是 not_implemented 桩**' }
    @{ Command = 'revit.create_column';                     Tool = 'revit_create_point_based_elements';   Note = '`category: OST_StructuralColumns`，结构类型自动推断为 Column' }
    @{ Command = 'revit.create_conduit';                    Tool = 'revit_create_mep_curves';             Note = '`kind: conduit`' }
    @{ Command = 'revit.create_dimension';                  Tool = 'revit_create_annotations';            Note = '`kind: dimension`。Reference 解析按面优先、退回整构件时会警告' }
    @{ Command = 'revit.create_duct';                       Tool = 'revit_create_mep_curves';             Note = '`kind: duct`' }
    @{ Command = 'revit.create_floor';                      Tool = 'revit_create_surface_based_elements'; Note = '`category: OST_Floors`' }
    @{ Command = 'revit.create_floor_plan_view';            Tool = 'revit_create_views';                  Note = '`viewType: floorPlan`' }
    @{ Command = 'revit.create_foundation';                 Tool = 'revit_create_point_based_elements';   Note = '`category: OST_StructuralFoundation`，结构类型自动推断为 Footing' }
    @{ Command = 'revit.create_grid';                       Tool = 'revit_create_datums';                 Note = '`kind: grid`，给 `arcPoint` 就是弧形轴' }
    @{ Command = 'revit.create_group';                      Tool = 'revit_group_elements';                Note = '`operation: create`' }
    @{ Command = 'revit.create_level';                      Tool = 'revit_create_datums';                 Note = '`kind: level`，**默认连楼层平面一起建**' }
    @{ Command = 'revit.create_material';                   Tool = 'revit_create_materials';              Note = '批量；强烈建议 `copyFromId`，空白新建的材质没有物理与外观资源' }
    @{ Command = 'revit.create_new_document';               Tool = 'revit_open_document';                 Note = '`action: new`' }
    @{ Command = 'revit.create_pipe';                       Tool = 'revit_create_mep_curves';             Note = '`kind: pipe`' }
    @{ Command = 'revit.create_project_parameter';          Tool = 'revit_create_project_parameter';      Note = '见下方「参数定义」一节的说明' }
    @{ Command = 'revit.create_revision_cloud';             Tool = 'revit_create_annotations';            Note = '`kind: revisionCloud`。**bridge 这条是 not_implemented 桩**' }
    @{ Command = 'revit.create_roof';                       Tool = 'revit_create_surface_based_elements'; Note = '`category: OST_Roofs`' }
    @{ Command = 'revit.create_room';                       Tool = 'revit_create_rooms';                  Note = '回执直接给出面积，面积为 0 会点出「没围合」' }
    @{ Command = 'revit.create_schedule';                   Tool = 'revit_create_schedule';               Note = '' }
    @{ Command = 'revit.create_section_view';               Tool = 'revit_create_views';                  Note = '`viewType: section`。变换按右手系构造，见下方说明' }
    @{ Command = 'revit.create_shared_parameter';           Tool = 'revit_create_project_parameter';      Note = 'API 只能造共享参数，两条命令在 Revit 里本就是同一件事' }
    @{ Command = 'revit.create_sheet';                      Tool = 'revit_create_sheets';                 Note = '批量签名' }
    @{ Command = 'revit.create_tag';                        Tool = 'revit_create_annotations';            Note = '`kind: tag`' }
    @{ Command = 'revit.create_text_note';                  Tool = 'revit_create_annotations';            Note = '`kind: textNote`，宽度越界会提前给出允许范围' }
    @{ Command = 'revit.create_text_type';                  Tool = 'revit_duplicate_type';                Note = '从已有文字类型复制并改参数。**bridge 这条是 not_implemented 桩**' }
    @{ Command = 'revit.create_wall';                       Tool = 'revit_create_line_based_elements';    Note = '`category: OST_Walls`' }
    @{ Command = 'revit.delete_element';                    Tool = 'revit_delete_elements';               Note = '先在子事务里真删一次拿到连带影响，回滚后要求 confirm' }
    @{ Command = 'revit.delete_sheet';                      Tool = 'revit_delete_elements';               Note = '图纸就是一种视图，走同一个删除工具' }
    @{ Command = 'revit.duplicate_sheet';                   Tool = 'revit_duplicate_sheets';              Note = '`contents` 对应 Revit 自己的四种复制语义；2021 及以下退回手工复制并在回执里注明' }
    @{ Command = 'revit.edit_family';                       Tool = 'revit_open_document';                 Note = '`action: editFamily`；内建族与系统族会给出说得清的拒绝' }
    @{ Command = 'revit.execute_python';                    Tool = 'revit_execute_script';                Note = '**用 C# 而非 Python**，理由见下方「逃生舱」一节' }
    @{ Command = 'revit.export_dwg_by_view';                Tool = 'revit_export_documents';              Note = '`format: dwg`' }
    @{ Command = 'revit.export_ifc_with_settings';          Tool = 'revit_export_documents';              Note = '`format: ifc`，支持版本与基本量' }
    @{ Command = 'revit.export_image';                      Tool = 'revit_export_image';                  Note = '' }
    @{ Command = 'revit.export_navisworks';                 Tool = 'revit_export_documents';              Note = '`format: nwc`；没装 Navisworks 导出器会明确告知' }
    @{ Command = 'revit.export_pdf_by_sheet_set';           Tool = 'revit_export_documents';              Note = '`format: pdf`。**需要 Revit 2022+**，更早版本 API 没有 PDF 导出' }
    @{ Command = 'revit.export_schedules';                  Tool = 'revit_export_schedules';              Note = '导多张时自动按明细表名区分文件' }
    @{ Command = 'revit.extract_snapshot';                  Tool = 'revit_get_model_changes';             Note = '省略 `since` 即取基线 token。**不落盘全模型快照**，理由见下' }
    @{ Command = 'revit.get_categories';                    Tool = 'revit_list_categories';               Note = '只列实际有构件的类别并给出数量' }
    @{ Command = 'revit.get_design_options';                Tool = 'revit_list_design_options';           Note = '附带每个选项里的构件数与主模型规模' }
    @{ Command = 'revit.get_document_info';                 Tool = 'revit_get_document_info';             Note = '' }
    @{ Command = 'revit.get_element_bounding_box';          Tool = 'revit_get_element_geometry';          Note = '包围盒是它的一部分' }
    @{ Command = 'revit.get_element_geometry';              Tool = 'revit_get_element_geometry';          Note = '**不返回网格**——网格对模型没有用，只会吃光上下文' }
    @{ Command = 'revit.get_element_parameters';            Tool = 'revit_get_element_parameters';        Note = '同时给原始值与带单位的显示值' }
    @{ Command = 'revit.get_element_type';                  Tool = 'revit_get_element_parameters';        Note = '`includeTypeParameters: true`；要挑类型用 revit_list_types' }
    @{ Command = 'revit.get_elements_by_type';              Tool = 'revit_query_elements';                Note = '' }
    @{ Command = 'revit.get_group_members';                 Tool = 'revit_list_groups';                   Note = '`includeMembers: true`' }
    @{ Command = 'revit.get_link_instances';                Tool = 'revit_list_links';                    Note = '每个链接的实例 ID 都在 `instanceIds` 里' }
    @{ Command = 'revit.get_mep_systems';                   Tool = 'revit_list_mep_systems';              Note = '**bridge 这条是 not_implemented 桩**' }
    @{ Command = 'revit.get_parameter_value';               Tool = 'revit_get_element_parameters';        Note = '用 `nameContains` 过滤' }
    @{ Command = 'revit.get_phase_filters';                 Tool = 'revit_list_phases';                   Note = '在 `filters` 字段里' }
    @{ Command = 'revit.get_phases';                        Tool = 'revit_list_phases';                   Note = '附带每个阶段创建/拆除了多少构件' }
    @{ Command = 'revit.get_project_location';              Tool = 'revit_get_project_location';          Note = '**bridge 这条是 not_implemented 桩**' }
    @{ Command = 'revit.get_render_settings';               Tool = $null;                                 Note = '**未实现**。bridge 这条也是桩——Revit API 不提供渲染设置的读写入口，两边都做不到' }
    @{ Command = 'revit.get_revision_sequences';            Tool = 'revit_list_revisions';                Note = '附带每个修订下的云线数' }
    @{ Command = 'revit.get_room_boundary';                 Tool = 'revit_list_rooms';                    Note = '边界在房间信息里。**bridge 这条是 not_implemented 桩**' }
    @{ Command = 'revit.get_rvt_links';                     Tool = 'revit_list_links';                    Note = '含载入状态与可直接查询的 `linkedDocumentId`' }
    @{ Command = 'revit.get_schedule_data';                 Tool = 'revit_read_schedule';                 Note = '' }
    @{ Command = 'revit.get_selection';                     Tool = 'revit_get_selection';                 Note = '' }
    @{ Command = 'revit.get_sheet_info';                    Tool = 'revit_get_sheet_contents';            Note = '视口、图签、修订、可写参数名' }
    @{ Command = 'revit.get_snapshot_delta';                Tool = 'revit_get_model_changes';             Note = '带上上次的 token' }
    @{ Command = 'revit.get_type_parameters';               Tool = 'revit_get_element_parameters';        Note = '`includeTypeParameters: true`' }
    @{ Command = 'revit.get_view_templates';                Tool = 'revit_list_view_templates';           Note = '附带每个样板被多少视图套着' }
    @{ Command = 'revit.get_warnings';                      Tool = 'revit_get_warnings';                  Note = '按种类归组' }
    @{ Command = 'revit.get_worksets';                      Tool = 'revit_list_worksets';                 Note = '**附带开关状态**——关闭的工作集里的构件查不到，审计前必看' }
    @{ Command = 'revit.health';                            Tool = $null;                                 Note = '**不需要**。MCP 的 `initialize` 与 `tools/list` 本身就是健康检查；服务状态在 Ribbon 上' }
    @{ Command = 'revit.invoke_method';                     Tool = 'revit_invoke_api';                    Note = '`action: call`，重载歧义时列出候选签名而不是随便挑一个' }
    @{ Command = 'revit.list_elements_by_category';         Tool = 'revit_query_elements';                Note = '' }
    @{ Command = 'revit.list_families';                     Tool = 'revit_list_families';                 Note = '' }
    @{ Command = 'revit.list_levels';                       Tool = 'revit_list_levels';                   Note = '' }
    @{ Command = 'revit.list_project_parameters';           Tool = 'revit_list_project_parameters';       Note = '' }
    @{ Command = 'revit.list_shared_parameters';            Tool = 'revit_list_project_parameters';       Note = '`includeUnboundShared: true`' }
    @{ Command = 'revit.list_sheets';                       Tool = 'revit_list_views';                    Note = '`viewType: DrawingSheet`' }
    @{ Command = 'revit.list_titleblocks';                  Tool = 'revit_list_types';                    Note = '`category: OST_TitleBlocks`' }
    @{ Command = 'revit.list_views';                        Tool = 'revit_list_views';                    Note = '' }
    @{ Command = 'revit.mirror_element';                    Tool = 'revit_transform_elements';            Note = '`operation: mirror`，`keepOriginal` 控制留不留原件' }
    @{ Command = 'revit.move_element';                      Tool = 'revit_transform_elements';            Note = '`operation: move`' }
    @{ Command = 'revit.open_document';                     Tool = 'revit_open_document';                 Note = '`action: open`，支持分离方式打开' }
    @{ Command = 'revit.pin_element';                       Tool = 'revit_set_elements_pinned';           Note = '`pinned: true`' }
    @{ Command = 'revit.place_door';                        Tool = 'revit_create_point_based_elements';   Note = '`category: OST_Doors`，可自动找宿主墙' }
    @{ Command = 'revit.place_family_instance';             Tool = 'revit_create_point_based_elements';   Note = '任意可载入族' }
    @{ Command = 'revit.place_viewport_on_sheet';           Tool = 'revit_add_views_to_sheet';            Note = '回读实际位置并检查有没有落到纸外' }
    @{ Command = 'revit.place_window';                      Tool = 'revit_create_point_based_elements';   Note = '`category: OST_Windows`' }
    @{ Command = 'revit.populate_titleblock';               Tool = 'revit_update_sheets';                 Note = '`parameters` 写图纸本身，`titleBlockParameters` 写图签实例' }
    @{ Command = 'revit.reflect_get';                       Tool = 'revit_invoke_api';                    Note = '`action: get`' }
    @{ Command = 'revit.reflect_set';                       Tool = 'revit_invoke_api';                    Note = '`action: set`' }
    @{ Command = 'revit.relinquish_all';                    Tool = 'revit_sync_to_central';               Note = '`action: relinquish`' }
    @{ Command = 'revit.render_3d_view';                    Tool = 'revit_export_image';                  Note = '**bridge 这条的实现本身就是图片导出**，它自己的返回值里也这么写着' }
    @{ Command = 'revit.renumber_sheets';                   Tool = 'revit_update_sheets';                 Note = '**编号互换或循环移位时自动走两阶段**，不会中途撞号失败' }
    @{ Command = 'revit.replace_family_type';               Tool = 'revit_change_element_types';          Note = '`fromTypeId` + `toTypeId`' }
    @{ Command = 'revit.rotate_element';                    Tool = 'revit_transform_elements';            Note = '`operation: rotate`，角度用度' }
    @{ Command = 'revit.save_document';                     Tool = 'revit_save_document';                 Note = '另存为需要显式路径，覆盖需要 `overwrite: true`' }
    @{ Command = 'revit.set_element_material';              Tool = 'revit_set_element_parameters';        Note = '材质参数是 ElementId 类型，`value` 传材质 ID（用 revit_list_materials 查）' }
    @{ Command = 'revit.set_parameter_value';               Tool = 'revit_set_element_parameters';        Note = '批量签名' }
    @{ Command = 'revit.set_selection';                     Tool = 'revit_set_selection';                 Note = '' }
    @{ Command = 'revit.set_type_parameter';                Tool = 'revit_set_type_parameters';           Note = '回执给出实际波及的构件数，规模闸也按它算' }
    @{ Command = 'revit.sync_to_central';                   Tool = 'revit_sync_to_central';               Note = '`confirm` 是必填布尔值，不是「超限才要」的可选项' }
    @{ Command = 'revit.tag_all_in_view';                   Tool = 'revit_tag_all_in_view';               Note = '默认跳过已有标记；标不了的构件会被数出来' }
    @{ Command = 'revit.ungroup';                           Tool = 'revit_group_elements';                Note = '`operation: ungroup`' }
    @{ Command = 'revit.unpin_element';                     Tool = 'revit_set_elements_pinned';           Note = '`pinned: false`' }
)

# ==================== 从源码提取 ====================

function Get-BridgeCommands {
    param([string] $Root)

    if (-not (Test-Path $Root)) {
        throw "找不到 bridge 源码目录：$Root`n用 -BridgeSource 指定它的位置。"
    }

    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($file in Get-ChildItem -Path $Root -Filter *.cs -Recurse) {
        $text = Get-Content $file.FullName -Raw -Encoding UTF8
        foreach ($m in [regex]::Matches($text, '\[BridgeCommand\("([^"]+)"')) {
            [void] $names.Add($m.Groups[1].Value)
        }
    }

    return @($names | Sort-Object)
}

function Get-McpTools {
    param([string] $Root)

    $names = New-Object System.Collections.Generic.HashSet[string]
    foreach ($file in Get-ChildItem -Path $Root -Filter *.cs -Recurse) {
        $text = Get-Content $file.FullName -Raw -Encoding UTF8
        foreach ($m in [regex]::Matches($text, 'McpTool\("([a-z_]+)"')) {
            [void] $names.Add($m.Groups[1].Value)
        }
    }

    return @($names | Sort-Object)
}

# ==================== 校验 ====================

$commands = Get-BridgeCommands -Root $BridgeSource
$tools = Get-McpTools -Root (Join-Path $repoRoot 'src\RevitMCP.Addin')

$mapped = @{}
foreach ($row in $Mapping) { $mapped[$row.Command] = $row }

$problems = @()

foreach ($c in $commands) {
    if (-not $mapped.ContainsKey($c)) { $problems += "bridge 有这条命令，映射表里没有：$c" }
}

foreach ($row in $Mapping) {
    if ($commands -notcontains $row.Command) {
        $problems += "映射表里有，bridge 源码里已经没有了：$($row.Command)"
    }
    if ($row.Tool -and ($tools -notcontains $row.Tool)) {
        $problems += "映射到了不存在的工具：$($row.Command) → $($row.Tool)"
    }
}

if ($problems.Count -gt 0) {
    Write-Host "映射表与源码对不上：" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  $p" -ForegroundColor Red }
    exit 1
}

$covered = @($Mapping | Where-Object { $_.Tool })
$skipped = @($Mapping | Where-Object { -not $_.Tool })

Write-Host "bridge 命令 $($commands.Count) 条  ·  RevitMCP 工具 $($tools.Count) 个" -ForegroundColor Green
Write-Host "  覆盖 $($covered.Count) 条，明确不做 $($skipped.Count) 条" -ForegroundColor Green

if ($CheckOnly) { exit 0 }

# ==================== 生成文档 ====================

$sb = New-Object System.Text.StringBuilder
function Add-Line { param([string] $Text = '') [void] $sb.AppendLine($Text) }

Add-Line '# 与 revit-bridge-addin 的能力对照'
Add-Line ''
Add-Line '> 本文档由 `build/check-parity.ps1` 生成并校验，改了工具名会当场对不上。'
Add-Line ''
Add-Line "``revit-bridge-addin`` 共 **$($commands.Count)** 条命令（其中 7 条在它那边是 ``not_implemented`` 桩）。"
Add-Line "RevitMCP 用 **$($tools.Count)** 个工具覆盖其中 **$($covered.Count)** 条；**$($skipped.Count)** 条明确不做，理由在表里写着。"
Add-Line ''
Add-Line '命令数比工具数多，是因为本项目**按几何形态与操作语义归并**：'
Add-Line '`move` / `copy` / `rotate` / `mirror` 在 Revit 里是同一个 `ElementTransformUtils`'
Add-Line '对整批构件施加一个变换，拆成四个工具只会让模型多记四个名字、'
Add-Line '让同一套参数校验和失败模式重复四遍。'
Add-Line ''
Add-Line '所有归并后的工具都收数组、走单个事务（撤销栈里只占一步）、长度一律用毫米。'
Add-Line ''
Add-Line '表里这 110 条**每一条都有实测走到**：`workflows/m10-parity.ps1` 把 68 个工具全部调了一遍，'
Add-Line '96 项检查，包括负例（该被拒绝的必须被拒绝）。覆盖率由插件的审计日志核算，不靠人数。'
Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line '| bridge 命令 | RevitMCP 工具 | 说明 |'
Add-Line '|---|---|---|'

foreach ($row in ($Mapping | Sort-Object Command)) {
    $cell = if ($row.Tool) { '`' + $row.Tool + '`' } else { '—' }
    Add-Line "| ``$($row.Command)`` | $cell | $($row.Note) |"
}

Add-Line ''
Add-Line '---'
Add-Line ''
Add-Line '## 本项目额外提供、bridge 没有的'
Add-Line ''
Add-Line '| 能力 | 为什么需要 |'
Add-Line '|---|---|'
Add-Line '| `revit_list_documents` + 所有只读工具的 `documentId` | 一个 Revit 能同时开十几个模型。bridge 的命令全部写死 `ActiveUIDocument`，做不了跨文档批量审计 |'
Add-Line '| `revit_get_project_units` | 工具单位与项目显示单位是否一致。不一致时读数会被误解 |'
Add-Line '| `revit_list_schedulable_fields` | 某类别做明细表能选哪些字段。字段名随项目语言变化，猜不出来 |'
Add-Line '| `revit_list_materials` | 设材质要先有材质 ID |'
Add-Line '| `revit_list_groups` | 含同类型实例数——改一个组会联动改到几个 |'
Add-Line '| `revit_activate_view` | 切换活动视图。它受写保护管辖但不开事务，是工具的第三类 |'
Add-Line '| `revit_query_elements` 的 `withinBox` / `near` | 空间过滤，走 Revit 的空间索引 |'
Add-Line '| `revit_query_elements` 的 `phaseId` / `designOptionId` | 改造与多方案项目统计的正确性前提 |'
Add-Line '| `revit_get_element_parameters` 的 `structuralType` | 它不是参数，属性面板上看不到。一根结构行为缺失的柱子只有这个字段能认出来 |'
Add-Line ''
Add-Line '## 几处刻意的不同'
Add-Line ''
Add-Line '### 结构类型不能一律填 NonStructural'
Add-Line ''
Add-Line 'bridge 的 `create_column` / `create_foundation` 各自传了正确的 `StructuralType`，'
Add-Line '而本项目的 `revit_create_point_based_elements` 早期把它硬编码成了 `NonStructural`。'
Add-Line '那会得到一个**看起来对、但没有结构行为也没有分析模型**的构件——'
Add-Line '结构专业的明细表、荷载与分析全会漏掉它，而模型上完全看不出异样。'
Add-Line '现在按类别推断（结构柱→Column、梁→Beam、基础→Footing），也可以用 `structuralType` 显式指定。'
Add-Line ''
Add-Line '**这个缺陷从模型外部本来是看不见的**：实测比对过同一族分别以 NonStructural 与 Column 建出的两根柱子，'
Add-Line '29 个实例参数一字不差。所以 `revit_get_element_parameters` 专门把 `structuralType` 单独交了出来。'
Add-Line ''
Add-Line '### 剖面视图的变换必须是右手系'
Add-Line ''
Add-Line 'bridge 的 `create_section_view` 把 `BasisZ` 拼成了 `(-dy, dx, 0)`，'
Add-Line '而 `BasisX × BasisY` 算出来是 `(dy, -dx, 0)`——差一个负号，构成左手系。'
Add-Line '本项目一律用 `BasisX.CrossProduct(BasisY)` 算 `BasisZ`，'
Add-Line '并且剖面范围由 `bottomMm` / `topMm` / `depthMm` 给出，而不是硬编码的 ±10 英尺。'
Add-Line ''
Add-Line '### 参数定义：API 造不出「非共享」的项目参数'
Add-Line ''
Add-Line 'Revit 的参数绑定需要一个 `Definition`，而 API 能拿到的 `Definition` 只有共享参数文件里的那种。'
Add-Line 'UI 上那个「项目参数（非共享）」选项在 API 里没有对应入口。'
Add-Line '所以 `revit_create_project_parameter` 实际做的是：往共享参数文件里写一条定义，再绑定到项目。'
Add-Line '**共享参数文件是项目之外的一个文件**，交付时漏掉它，别人打开项目会看到参数但对不上——'
Add-Line '回执里因此一定会给出它的路径。'
Add-Line ''
Add-Line '### 快照：不落盘全模型'
Add-Line ''
Add-Line 'bridge 的 `extract_snapshot` 把整个模型序列化成文件。本项目的 `revit_get_model_changes`'
Add-Line '改为订阅 Revit 的 `DocumentChanged` 事件，只回答「从上次的 token 起改了什么」。'
Add-Line '理由是全模型快照的代价随模型大小线性增长，而「完整状态」这个问题'
Add-Line '`revit_query_elements` 本来就能回答，没必要再存一份必然会过期的副本。'
Add-Line ''
Add-Line '记录容量有限（每文档 2 万条），超出后旧记录会被丢弃，此时回执的 `incomplete` 为 true——'
Add-Line '**这一位必须看**，它意味着拿到的改动列表是残缺的。'
Add-Line ''
Add-Line '### 逃生舱：C# 而不是 Python，且单独一道闸'
Add-Line ''
Add-Line '`revit_invoke_api` 与 `revit_execute_script` 绕开了本项目其余部分的全部保证——'
Add-Line '没有单位换算、没有校验、没有规模闸。它们的影响面根本不止于「改模型」：'
Add-Line '能读写任何文件、发任何网络请求。所以它们**不跟着 Ribbon 上的写入开关走**，'
Add-Line '需要在 `config.json` 里单独把 `escapeHatchEnabled` 设为 true。'
Add-Line ''
Add-Line '用 C# 而非 Python，是因为本项目全程零第三方依赖：Revit 把所有插件加载进同一个 AppDomain'
Add-Line '且不应用插件自己的绑定重定向（架构 §2 的 C2），引入 IronPython 就必须同时引入'
Add-Line 'ILRepack 那一整套内联化设施。而 .NET Framework 自带的 `CSharpCodeProvider` 提供同样的'
Add-Line '「执行任意代码」能力、零依赖。'
Add-Line ''
Add-Line '### 版本覆盖'
Add-Line ''
Add-Line 'bridge 主要针对 Revit 2024。本项目覆盖 2019–2024 六个版本，'
Add-Line '版本差异全部收在 `src/RevitMCP.Addin/Compat/` 下：'
Add-Line ''
Add-Line '| 差异 | 换代版本 | 收在哪 |'
Add-Line '|---|---|---|'
Add-Line '| `ElementId` 由 Int32 变 Int64 | 2024 | `ElementIdCompat` |'
Add-Line '| `Floor.Create` / `Ceiling.Create` 取代 `NewFloor` | 2022 | `SurfaceCompat` |'
Add-Line '| `GetTaggedLocalElementIds` 取代 `TaggedLocalElementId` | 2022 | `TagCompat` |'
Add-Line '| `ViewSheet.Duplicate` 才出现 | 2022 | `SheetCompat` |'
Add-Line '| `PDFExportOptions` 才出现 | 2022 | `PdfExportCompat` |'
Add-Line '| `ForgeTypeId` 取代 `ParameterType` / `BuiltInParameterGroup` | 2022 | `ParameterDefinitionCompat` |'
Add-Line ''
Add-Line '用不了的能力会给出说得清的拒绝（如 2019 导 PDF），而不是悄悄降级产出一个残缺的结果。'

$dir = Split-Path -Parent $Output
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

[IO.File]::WriteAllText($Output, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host "已生成 $Output" -ForegroundColor Green
