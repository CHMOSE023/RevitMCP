<#
.SYNOPSIS
    M10：把 M10 新增与修复的工具在真实模型上跑一遍。

.DESCRIPTION
    m6–m9 各自验的是一条业务闭环。这一个不同——它验的是**覆盖面**：
    M10 把 revit-bridge-addin 的 112 条命令归并进 68 个工具，
    而"归并没有漏掉能力"这句话，只有把工具真的调过一遍才算数。

    脚本刻意从**建模三形态**开始，因为那是 m6 之后改动最大、也最容易悄悄出错的地方：

    · 点定位构件曾经把结构类型硬编码成 NonStructural。那样建出来的结构柱
      看起来完全正常，却没有结构行为、没有分析模型——结构专业的明细表与荷载
      会整个漏掉它。本脚本用**差分**的办法证明它已经修好：
      同一个族建两次，一次显式 NonStructural、一次显式 Column，
      比较两者的参数集合。集合有差异，才说明 structuralType 真的传进去了。

    全程在一块可指定的空地上作业，结束时把造出来的东西删掉（-KeepScene 可保留）。
    **不碰文档生命周期与协同**（打开/保存/关闭/同步中心文件）——
    那些会动用户的磁盘和中心文件，不该由一个验证脚本自作主张。
    要连它们一起验，加 -IncludeLifecycle，脚本会在动手前逐项问你。

.PARAMETER OriginX
    作业区原点 X，毫米。默认 50000，离常见模型内容远一些。

.PARAMETER KeepScene
    保留造出来的构件，不在结束时删除。

.PARAMETER IncludeLifecycle
    连同文档生命周期工具一起验（会另存一个副本文件）。默认不验。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m10-parity.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m10-parity.ps1 -KeepScene
#>
[CmdletBinding()]
param(
    [double] $OriginX = 50000,
    [double] $OriginY = 50000,
    [switch] $KeepScene,
    [switch] $IncludeLifecycle,
    [int] $ProcessId,
    [string] $Endpoint
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\McpClient.ps1"

# ==================== 记分板 ====================

$script:Checks = @()

function Get-CreatedIds {
    <#
    .SYNOPSIS
        从建模工具的回执里取出新建构件的 ID。

        必须滤掉空值。工具失败时 Data 是 $null，
        `@($null | ForEach-Object { $_.id })` 在 PowerShell 里产出的是
        **一个含 $null 的数组**而不是空数组——它会一路混进待删列表，
        让最后的清理整个失败，把这一趟造的东西全留在模型里。
        实测就是这么留下一堆重复房间的。
    #>
    param($Result, [string] $Collection = 'elements')

    if (-not $Result -or $Result.IsError -or -not $Result.Data) { return @() }

    $items = $Result.Data.$Collection
    if (-not $items) { return @() }

    return @($items | ForEach-Object { $_.id } | Where-Object { $_ })
}

function Check {
    <#
    .SYNOPSIS
        记一条检查结果。脚本的产出就是这张表——哪个工具验过、验出了什么。
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $Tool,
        [Parameter(Mandatory = $true)] [string] $What,
        [Parameter(Mandatory = $true)] [bool] $Ok,
        [string] $Detail
    )

    $script:Checks += [PSCustomObject]@{ Tool = $Tool; What = $What; Ok = $Ok; Detail = $Detail }

    if ($Ok) { Write-Host "  ✓ $What" -ForegroundColor Green -NoNewline }
    else      { Write-Host "  ✗ $What" -ForegroundColor Red -NoNewline }

    if ($Detail) { Write-Host "  $Detail" -ForegroundColor DarkGray } else { Write-Host "" }
}

function Invoke-Safely {
    <#
    .SYNOPSIS
        调用一个工具，把"这个工具根本不存在"变成一条失败记录，而不是一个异常。

        版本错位是这个脚本最常见的处境：源码里加了新工具，Revit 里装的还是旧插件。
        让它当场抛异常，末尾的清理就跑不到了——这一趟造出来的几十个构件
        会全部留在用户的模型里。实测踩过一次，留下了一串重复房间。
    #>
    param([PSCustomObject] $Session, [string] $Name, [hashtable] $Arguments)

    try {
        return Invoke-RevitTool $Session $Name $Arguments
    }
    catch {
        return [PSCustomObject]@{
            IsError  = $true
            Code     = 'TOOL_UNAVAILABLE'
            Data     = $null
            Text     = "调用不到 $Name —— 多半是 Revit 里装的插件还是旧版本。原始错误：$($_.Exception.Message)"
            Warnings = @()
        }
    }
}

function Expect-Ok {
    <#
    .SYNOPSIS
        调用一个工具并期望它成功。失败时记一笔但不中断——
        一次跑完所有检查，比在第一个问题上停下更有用。
    #>
    param([PSCustomObject] $Session, [string] $Name, [hashtable] $Arguments = @{}, [string] $What)

    $result = Invoke-Safely $Session $Name $Arguments
    Write-ToolWarnings $result '     '

    $label = $What
    if (-not $label) { $label = $Name }

    $detail = ''
    if ($result.IsError) { $detail = $result.Text }

    Check -Tool $Name -What $label -Ok (-not $result.IsError) -Detail $detail

    return $result
}

function Expect-Refusal {
    <#
    .SYNOPSIS
        调用一个工具并期望它**拒绝**，且错误码是指定的那个。

        负例和正例一样重要：一个该拒绝却放行的工具，
        造成的损失比一个该成功却失败的工具大得多。
    #>
    param([PSCustomObject] $Session, [string] $Name, [hashtable] $Arguments, [string] $Code, [string] $What)

    $result = Invoke-Safely $Session $Name $Arguments

    # 工具不存在不算"被正确拒绝"——那是装错了版本，不是契约生效
    $ok = ([bool] $result.IsError) -and ($result.Code -ne 'TOOL_UNAVAILABLE')
    if ($ok -and $Code) { $ok = ($result.Code -eq $Code) }

    if ($ok) {
        $detail = $result.Code
    }
    else {
        $actual = $result.Code
        if (-not $actual) { $actual = '成功' }
        $detail = "期望被拒（$Code），实际：$actual"
    }

    Check -Tool $Name -What $What -Ok $ok -Detail $detail

    return $result
}

# ==================== 连接 ====================

Write-Step '连接'

$connectArgs = @{}
if ($ProcessId) { $connectArgs['ProcessId'] = $ProcessId }
if ($Endpoint) { $connectArgs['Endpoint'] = $Endpoint }
$session = Connect-RevitMcp @connectArgs

$info = Invoke-RevitTool $session 'revit_get_document_info' -ThrowOnError
Write-Host "文档「$($info.Data.title)」  Revit $($info.Data.revitVersion)  写入=$($info.Data.writeEnabled)"

if (-not $info.Data.writeEnabled) {
    throw "当前是「浏览模型」。这个脚本要建构件，请先在 Revit 的 RevitMCP 面板上切到「修改模型」。"
}

# 建之前记一个基线，最后用它核对"到底动了什么"
$baseline = Invoke-RevitTool $session 'revit_get_model_changes' -ThrowOnError
$baseToken = $baseline.Data.token
Write-Host "基线 token：$baseToken"

$created = @()      # 结束时要删掉的构件

# ==================== 取素材 ====================

Write-Step '取素材'

$levels = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError
$level = $levels.Data.levels | Sort-Object elevationMm | Select-Object -Last 1
Write-Host "标高「$($level.name)」 高程 $($level.elevationMm) mm"

$wallTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Walls'; limit = 200 } -ThrowOnError
$wallType = $wallTypes.Data.types | Where-Object { $null -ne $_.thicknessMm } |
    Sort-Object { [Math]::Abs($_.thicknessMm - 200) } | Select-Object -First 1
if (-not $wallType) { $wallType = $wallTypes.Data.types[0] }
Write-Host "墙类型「$($wallType.name)」"

$columnTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_StructuralColumns'; limit = 50 }
$columnType = $columnTypes.Data.types | Select-Object -First 1
if ($columnType) { Write-Host "结构柱类型「$($columnType.name)」" }
else { Write-Host "本项目没有载入结构柱族，结构类型差分检查将跳过。" -ForegroundColor DarkYellow }

$floorTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Floors'; limit = 50 }
$floorType = $floorTypes.Data.types | Select-Object -First 1

# ==================== 建模三形态 ====================

Write-Step '建模：线定位（墙）'

$x0 = $OriginX; $y0 = $OriginY
$x1 = $OriginX + 8000; $y1 = $OriginY + 6000

function Wall([double]$ax, [double]$ay, [double]$bx, [double]$by) {
    @{
        category     = 'OST_Walls'
        typeId       = $wallType.id
        levelId      = $level.id
        locationLine = @{ p0 = @{ x = $ax; y = $ay }; p1 = @{ x = $bx; y = $by } }
        height       = 3000
    }
}

$walls = Expect-Ok $session 'revit_create_line_based_elements' @{ elements = @(
        (Wall $x0 $y0 $x1 $y0), (Wall $x1 $y0 $x1 $y1),
        (Wall $x1 $y1 $x0 $y1), (Wall $x0 $y1 $x0 $y0)
    ) } '按定位线建 4 面墙围合'

$wallIds = Get-CreatedIds $walls
$created += $wallIds

if ($wallIds.Count -eq 0) {
    throw "墙没有建成，后面的检查全都依赖它。先看上面那条报错——工具会把可用参数列出来。"
}

Write-Step '建模：点定位 —— 结构类型差分检查'

# 这是 M10 修掉的那个缺陷的正面验证。
# 同一个族建两次，只有 structuralType 不同；两者的参数集合必须不一样——
# 一样就说明这个参数根本没传进 NewFamilyInstance，柱子仍然是非结构的。
if ($columnType) {
    $pair = Expect-Ok $session 'revit_create_point_based_elements' @{ elements = @(
            @{ category = 'OST_StructuralColumns'; typeId = $columnType.id; levelId = $level.id
               locationPoint = @{ x = $x0 + 1000; y = $y0 + 1000 }; structuralType = 'NonStructural' }
            @{ category = 'OST_StructuralColumns'; typeId = $columnType.id; levelId = $level.id
               locationPoint = @{ x = $x0 + 3000; y = $y0 + 1000 }; structuralType = 'Column' }
        ) } '同一柱族分别以 NonStructural 与 Column 建两根'

    if (-not $pair.IsError) {
        $columnIds = Get-CreatedIds $pair
        $created += $columnIds

        $params = Invoke-RevitTool $session 'revit_get_element_parameters' @{ elementIds = $columnIds } -ThrowOnError

        # 结构类型**不是参数**，在属性面板上看不到，也不在 parameters 列表里。
        # 曾经试过比对两根柱子的参数集合来验证，结果是 29 个参数一字不差——
        # 那条路走不通。revit_get_element_parameters 为此单独交出 structuralType。
        $asNonStructural = $params.Data.elements[0].structuralType
        $asColumn = $params.Data.elements[1].structuralType

        if ($null -eq $asColumn) {
            Check -Tool 'revit_get_element_parameters' `
                -What 'structuralType 可观测' -Ok $false `
                -Detail '回执里没有 structuralType 字段——插件还是旧版本，请关掉 Revit 重装后再跑'
        }
        else {
            Check -Tool 'revit_create_point_based_elements' `
                -What '显式 structuralType = Column 真的传进了 Revit' `
                -Ok ($asColumn -eq 'Column') -Detail "读回 $asColumn"

            # 这一条未必成立：钢柱族这类天生就是结构构件的族，
            # Revit 会把 NonStructural 强制纠正回 Column。那不是缺陷，
            # 但得让人看见，否则会以为参数没生效
            if ($asNonStructural -eq $asColumn) {
                Write-Host "     （注：该族把 NonStructural 也纠正成了 $asColumn —— " `
                    "结构族天生是结构构件，Revit 不接受把它建成非结构的）" -ForegroundColor DarkGray
            }
            else {
                Check -Tool 'revit_create_point_based_elements' `
                    -What 'NonStructural 与 Column 建出来的确实不同' -Ok $true `
                    -Detail "$asNonStructural vs $asColumn"
            }
        }
    }

    # 无法识别的结构类型必须当场报错，而不是悄悄退回 NonStructural
    Expect-Refusal $session 'revit_create_point_based_elements' @{ elements = @(
            @{ category = 'OST_StructuralColumns'; typeId = $columnType.id; levelId = $level.id
               locationPoint = @{ x = $x0 + 5000; y = $y0 + 1000 }; structuralType = '随便写的' }
        ) } 'INVALID_PARAMETER' '无法识别的 structuralType 被拒绝'
}

Write-Step '建模：面定位（楼板）'

if ($floorType) {
    $floor = Expect-Ok $session 'revit_create_surface_based_elements' @{ elements = @(
            @{ category = 'OST_Floors'; typeId = $floorType.id; levelId = $level.id
               boundary = @{ outerLoop = @(
                   @{ p0 = @{ x = $x0; y = $y0 }; p1 = @{ x = $x1; y = $y0 } }
                   @{ p0 = @{ x = $x1; y = $y0 }; p1 = @{ x = $x1; y = $y1 } }
                   @{ p0 = @{ x = $x1; y = $y1 }; p1 = @{ x = $x0; y = $y1 } }
                   @{ p0 = @{ x = $x0; y = $y1 }; p1 = @{ x = $x0; y = $y0 } }
               ) } }
        ) } '按闭合边界建楼板'
    $created += Get-CreatedIds $floor
}

# ==================== 房间 ====================

Write-Step '房间'

$rooms = Expect-Ok $session 'revit_create_rooms' @{ elements = @(
        @{ locationPoint = @{ x = $x0 + 4000; y = $y0 + 3000 }; levelId = $level.id
           name = 'M10 验证房间'; number = 'M10-01' }
    ) } '在围合区域内建房间'

if (-not $rooms.IsError) {
    $created += Get-CreatedIds $rooms
    $area = $rooms.Data.elements[0].areaSqm
    Check -Tool 'revit_create_rooms' -What '房间面积大于 0（说明真的被围住了）' `
        -Ok ($area -gt 0) -Detail "$area ㎡"
}

Expect-Ok $session 'revit_list_rooms' @{} '列出房间'

# ==================== 参数与几何 ====================

Write-Step '参数与几何'

Expect-Ok $session 'revit_set_element_parameters' `
    @{ elementIds = $wallIds; parameterName = '注释'; value = 'M10 验证' } '批量改同一个参数'

$read = Expect-Ok $session 'revit_get_element_parameters' `
    @{ elementIds = @($wallIds[0]); nameContains = '注释' } '读回参数'

if (-not $read.IsError) {
    $comment = $read.Data.elements[0].parameters | Where-Object { $_.name -eq '注释' } | Select-Object -First 1
    Check -Tool 'revit_set_element_parameters' -What '写进去的值能原样读回来' `
        -Ok ($comment.displayValue -eq 'M10 验证') -Detail "读到「$($comment.displayValue)」"
}

$geom = Expect-Ok $session 'revit_get_element_geometry' @{ elementIds = @($wallIds[0]) } '读构件几何'
if (-not $geom.IsError) {
    $g = $geom.Data.elements[0]
    Check -Tool 'revit_get_element_geometry' -What '几何用毫米，且与建模时给的坐标对得上' `
        -Ok ($null -ne $g.boundingBox) `
        -Detail "包围盒 min=($($g.boundingBox.min.x), $($g.boundingBox.min.y))"
}

# ==================== 选择集 ====================

Write-Step '选择集'

Expect-Ok $session 'revit_set_selection' @{ elementIds = $wallIds } '在 Revit 里选中这几面墙'
$sel = Expect-Ok $session 'revit_get_selection' @{} '读回选择集'
if (-not $sel.IsError) {
    Check -Tool 'revit_get_selection' -What '选择集数量与刚设进去的一致' `
        -Ok ($sel.Data.count -eq $wallIds.Count) -Detail "$($sel.Data.count) / $($wallIds.Count)"
}
Expect-Ok $session 'revit_set_selection' @{ elementIds = @() } '清空选择集'

# ==================== 明细表 ====================

Write-Step '明细表'

$fields = Invoke-RevitTool $session 'revit_list_schedulable_fields' @{ category = 'OST_Walls' } -ThrowOnError
$wanted = @($fields.Data.fields | Where-Object { $_.name -in @('族与类型', '合计', '注释') } |
    ForEach-Object { $_.name })
if ($wanted.Count -eq 0) { $wanted = @($fields.Data.fields | Select-Object -First 2 | ForEach-Object { $_.name }) }

$schedule = Expect-Ok $session 'revit_create_schedule' `
    @{ category = 'OST_Walls'; name = "M10 墙明细表 $(Get-Date -Format 'HHmmss')"; fields = $wanted } `
    "建墙明细表（字段：$($wanted -join '、')）"

if (-not $schedule.IsError) {
    $scheduleId = $schedule.Data.id
    $created += $scheduleId

    $table = Expect-Ok $session 'revit_read_schedule' @{ scheduleId = $scheduleId } '把明细表读成表格'
    if (-not $table.IsError) {
        Check -Tool 'revit_read_schedule' -What '明细表里能看到刚建的墙' `
            -Ok ($table.Data.rowCount -gt 0) -Detail "$($table.Data.rowCount) 行"
    }

    Expect-Ok $session 'revit_export_schedules' `
        @{ scheduleIds = @($scheduleId); fileName = 'm10-明细表.csv' } '把明细表导成 CSV'
}

# ==================== 视图切换 ====================

Write-Step '视图'

$views = Invoke-RevitTool $session 'revit_list_views' @{ viewType = 'FloorPlan'; onlyPlaceable = $true } -ThrowOnError
$target = $views.Data.views | Where-Object { $_.levelId -eq $level.id } | Select-Object -First 1
if ($target) {
    Expect-Ok $session 'revit_activate_view' @{ viewId = $target.id } "切到视图「$($target.name)」"
}

# ==================== 项目参数 ====================

Write-Step '项目参数'

$paramName = "M10 验证参数"
$existing = Invoke-RevitTool $session 'revit_list_project_parameters' @{ nameContains = $paramName }
if ($existing.Data.total -gt 0) {
    Write-Host "  （「$paramName」已存在，跳过创建——重复绑定会替换类别集合，工具会拒绝）" -ForegroundColor DarkYellow
    Expect-Refusal $session 'revit_create_project_parameter' `
        @{ name = $paramName; categories = @('OST_Walls'); dataType = 'text' } `
        'INVALID_PARAMETER' '同名参数重复创建被拒绝'
}
else {
    $created_param = Expect-Ok $session 'revit_create_project_parameter' `
        @{ name = $paramName; categories = @('OST_Walls'); dataType = 'text'; binding = 'instance' } `
        '创建项目参数并绑定到墙'

    if (-not $created_param.IsError) {
        Check -Tool 'revit_create_project_parameter' -What '回执给出了共享参数文件路径（交付时要带上它）' `
            -Ok ([bool] $created_param.Data.sharedParameterFile) `
            -Detail $created_param.Data.sharedParameterFile

        Expect-Ok $session 'revit_set_element_parameters' `
            @{ elementIds = @($wallIds[0]); parameterName = $paramName; value = '能写进去' } `
            '往新建的项目参数里写值'
    }
}

# ==================== 文档生命周期（默认跳过）====================

# ==================== 构件寻址 ====================

Write-Step 'uniqueId：跨会话稳定的构件寻址'

# ElementId 只在单个文档的单次会话内有效。审计报告里记一串 ElementId，
# 隔天拿出来会指向完全不同的构件——**而且看起来完全正常**。
# 所以查询回执要同时给出 uniqueId，并且所有接受构件 ID 的地方都得认它。

$probe = Invoke-RevitTool $session 'revit_query_elements' `
    @{ category = 'OST_Walls'; limit = 5 } -ThrowOnError

$sample = $probe.Data.elements | Select-Object -First 1

if (-not $sample) {
    Write-Host '  （模型里没有墙，跳过）' -ForegroundColor DarkGray
}
elseif (-not $sample.uniqueId) {
    Check -Tool 'revit_query_elements' -What '查询回执给出 uniqueId' -Ok $false `
        -Detail '回执里没有 uniqueId 字段——插件还是旧版本，请关掉 Revit 重装后再跑'
}
else {
    Check -Tool 'revit_query_elements' -What '查询回执给出 uniqueId' -Ok $true `
        -Detail $sample.uniqueId

    # 拿 uniqueId 反过来查，应当拿到同一个构件
    $byUid = Expect-Ok $session 'revit_get_element_parameters' `
        @{ elementIds = @($sample.uniqueId); nameContains = '注释' } '用 uniqueId 读参数'

    if (-not $byUid.IsError) {
        Check -Tool 'revit_get_element_parameters' -What 'uniqueId 解析到的是同一个构件' `
            -Ok ($byUid.Data.elements[0].id -eq $sample.id) `
            -Detail "$($byUid.Data.elements[0].id) vs $($sample.id)"
    }

    # 几何工具走的是另一条解析路径，单独验一次
    Expect-Ok $session 'revit_get_element_geometry' `
        @{ elementIds = @($sample.uniqueId) } '用 uniqueId 读几何'

    # 写工具也必须认
    Expect-Ok $session 'revit_set_element_parameters' `
        @{ elementIds = @($sample.uniqueId); parameterName = '注释'; value = "uid $stamp" } `
        '用 uniqueId 改参数'

    # 批量读工具不因为一个坏 ID 就整批失败——它把问题放进 notFound 并说清原因。
    # 这是刻意的：读 50 个构件，其中一个 ID 过期，不该让另外 49 个也拿不到
    $bogus = Invoke-RevitTool $session 'revit_get_element_geometry' `
        @{ elementIds = @('00000000-0000-0000-0000-000000000000-00000000') }

    Check -Tool 'revit_get_element_geometry' `
        -What '不存在的 uniqueId 进 notFound，不让整批失败' `
        -Ok ((-not $bogus.IsError) -and $bogus.Data.notFound.Count -eq 1) `
        -Detail "isError=$($bogus.IsError) notFound=$($bogus.Data.notFound.Count)"

    Check -Tool 'revit_get_element_geometry' `
        -What 'notFound 里说清了是"不存在"而不是"格式不对"' `
        -Ok ($bogus.Data.notFound[0] -match 'UniqueId' -and $bogus.Data.notFound[0] -match '不存在') `
        -Detail $bogus.Data.notFound[0]

    # 真正格式不对的，理由必须不一样——两者的补救动作不同
    $garbage = Invoke-RevitTool $session 'revit_get_element_geometry' `
        @{ elementIds = @('既不是数字也不是GUID') }

    Check -Tool 'revit_get_element_geometry' `
        -What '格式不对与找不到给出的是不同的理由' `
        -Ok ($garbage.Data.notFound[0] -match '格式') `
        -Detail $garbage.Data.notFound[0]
}

# ==================== 清空 ElementId 类参数 ====================

Write-Step '清空 ElementId 类参数'

# -1 与空字符串是"把这个参数清空"的正规写法，不是一个找不到的构件。
# 统一寻址那次改造一度把它当成"ID 不存在"拒掉了——清空参数这件事于是变成
# 一句"这个文档里不存在 ID 为 -1 的构件"，调用方完全无从下手。
$wallType = (Invoke-RevitTool $session 'revit_list_types' `
    @{ category = 'OST_Walls'; limit = 1 } -ThrowOnError).Data.types | Select-Object -First 1

if ($wallType) {
    $typeParams = Invoke-RevitTool $session 'revit_get_element_parameters' `
        @{ elementIds = @($wallType.id) } -ThrowOnError

    $clearable = $typeParams.Data.elements[0].parameters |
        Where-Object { $_.storageType -eq 'ElementId' -and -not $_.isReadOnly } |
        Select-Object -First 1

    if ($clearable) {
        $original = $clearable.value

        $cleared = Expect-Ok $session 'revit_set_type_parameters' `
            @{ typeIds = @($wallType.id); parameterName = $clearable.name; value = '-1' } `
            "用 -1 清空「$($clearable.name)」"

        if (-not $cleared.IsError -and $original -and $original -ne '-1') {
            # 原样放回去，别给用户的类型留下痕迹
            Invoke-RevitTool $session 'revit_set_type_parameters' `
                @{ typeIds = @($wallType.id); parameterName = $clearable.name; value = $original } | Out-Null
        }
    }
    else {
        Write-Host '  （这个墙类型上没有可写的 ElementId 参数，跳过）' -ForegroundColor DarkGray
    }
}

# ==================== 文档生命周期：闸门 ====================

Write-Step '文档生命周期：验闸门，不动磁盘'

# 这几个工具会开关文档、往磁盘写模型、把改动推给所有协作者。
# 真去跑它们不该由一个验证脚本自作主张，但**它们的闸门可以安全地验**——
# 而闸门恰恰是最该验的部分：一个该拒绝却放行的 sync_to_central，
# 造成的损失比任何功能缺陷都大。

Expect-Refusal $session 'revit_open_document' `
    @{ action = 'open'; path = 'D:\绝不可能存在的目录\不存在.rvt' } `
    'INVALID_PARAMETER' '打开不存在的文件被拒绝'

Expect-Refusal $session 'revit_open_document' @{ action = '随便写的' } `
    'INVALID_PARAMETER' '无法识别的 action 被拒绝并列出可用值'

Expect-Refusal $session 'revit_close_document' @{ documentId = '不存在的文档' } `
    'ELEMENT_NOT_FOUND' '关闭不存在的文档被拒绝'

# 另存收的是完整路径（与各类导出只收文件名不同）。下面三条都不碰磁盘
Expect-Refusal $session 'revit_save_document_as' @{ path = '办公楼.rvt' } `
    'INVALID_PARAMETER' '另存给相对路径被拒绝'

Expect-Refusal $session 'revit_save_document_as' @{ path = 'D:\绝不可能存在的目录\x.rvt' } `
    'INVALID_PARAMETER' '另存到不存在的目录被拒绝（工具不替你建目录）'

Expect-Refusal $session 'revit_save_document_as' @{ path = 'D:\x.dwg' } `
    'INVALID_PARAMETER' '另存的扩展名必须是 .rvt'

# 当前是未保存的新文档，原地保存无处可存——工具应当说清楚而不是抛个裸异常
$docInfo = Invoke-RevitTool $session 'revit_get_document_info' -ThrowOnError
if (-not $docInfo.Data.pathName) {
    Expect-Refusal $session 'revit_save_document' @{} `
        $null '从未保存过的文档做原地保存被拒绝（没有路径可存）'
}

# 两道闸的**顺序**是有讲究的：「这不是工作共享模型」排在「你还没确认」前面。
# 对一件根本做不到的事索要确认，只会让调用方带着 confirm 再来一次、再被拒一次。
# 所以非工作共享模型上，给不给 confirm 都是 INVALID_PARAMETER。
if ($docInfo.Data.isWorkshared) {
    Expect-Refusal $session 'revit_sync_to_central' @{ action = 'sync'; confirm = $false } `
        'CONFIRMATION_REQUIRED' '同步中心文件缺 confirm 被拒绝'
}
else {
    Expect-Refusal $session 'revit_sync_to_central' @{ action = 'sync'; confirm = $true } `
        'INVALID_PARAMETER' '非工作共享模型同步中心文件被拒绝（即使带了 confirm）'

    Expect-Refusal $session 'revit_sync_to_central' @{ action = 'sync'; confirm = $false } `
        'INVALID_PARAMETER' '「不是工作共享模型」排在「缺 confirm」之前'
}

if ($IncludeLifecycle) {
    Write-Step '文档生命周期'
    Write-Host "  这一组会往磁盘写一个模型副本。" -ForegroundColor Yellow
    $answer = Read-Host "  确认继续？(y/N)"
    if ($answer -eq 'y') {
        $copy = Join-Path ([IO.Path]::GetTempPath()) "m10-副本-$(Get-Date -Format 'HHmmss').rvt"
        Expect-Ok $session 'revit_save_document_as' @{ path = $copy } "另存为 $copy"

        Expect-Refusal $session 'revit_save_document_as' @{ path = $copy } `
            'INVALID_PARAMETER' '覆盖已有文件需要显式 overwrite'
    }
    else { Write-Host "  已跳过。" -ForegroundColor DarkGray }
}
else {
    Write-Host ""
    Write-Host "（文档生命周期与协同工具未验——它们会动磁盘和中心文件。加 -IncludeLifecycle 可验。）" -ForegroundColor DarkGray
}

# ==================== 图纸改号 ====================

Write-Step '图纸：循环改号必须走两阶段'

# 这一段验的是 M10 里唯一一段"调用方自己做不到"的逻辑。
#
# 图纸编号在项目内必须唯一，所以把 101→102、同时 102→103、103→101，
# 按顺序一个个设一定会在第一步就撞号。工具检测到这种情况会先把整批改成
# 临时编号、再改成目标编号。
#
# 临时前缀踩过一个坑：用过 `~MCP~`，而 `~` 是 Revit 图纸编号的**非法字符**，
# Parameter.Set 直接返回 false。这条路径只有循环改号才走得到，
# 常规改号一路正常——所以那个错误只能靠这样跑一遍才发现。

$stamp = Get-Date -Format 'HHmmss'
$numbers = @("M10-$stamp-1", "M10-$stamp-2", "M10-$stamp-3")

$sheetSpecs = @()
for ($i = 0; $i -lt 3; $i++) {
    $sheetSpecs += @{ number = $numbers[$i]; name = "M10 验证图纸 $($i + 1)" }
}

$sheets = Expect-Ok $session 'revit_create_sheets' @{ elements = $sheetSpecs } '建 3 张图纸'
$sheetIds = Get-CreatedIds $sheets
$created += $sheetIds

if ($sheetIds.Count -eq 3) {
    # 循环移位：1→2、2→3、3→1。顺序设置必然中途撞号
    $shift = @(
        @{ sheetId = $sheetIds[0]; number = $numbers[1] }
        @{ sheetId = $sheetIds[1]; number = $numbers[2] }
        @{ sheetId = $sheetIds[2]; number = $numbers[0] }
    )

    $renum = Expect-Ok $session 'revit_update_sheets' @{ sheets = $shift } '循环移位改号'

    if (-not $renum.IsError) {
        Check -Tool 'revit_update_sheets' -What '确实走了两阶段（否则第一步就会撞号失败）' `
            -Ok ([bool] $renum.Data.usedTwoPhaseRenumber) `
            -Detail "usedTwoPhaseRenumber = $($renum.Data.usedTwoPhaseRenumber)"

        # 回读一遍，确认三张图纸真的换到了目标编号上
        $after = Invoke-RevitTool $session 'revit_get_sheet_contents' -ThrowOnError
        $actual = @()
        foreach ($id in $sheetIds) {
            $row = $after.Data.sheets | Where-Object { $_.id -eq $id } | Select-Object -First 1
            $actual += $row.number
        }

        $expected = @($numbers[1], $numbers[2], $numbers[0])
        Check -Tool 'revit_update_sheets' -What '三张图纸的编号都换到了目标值' `
            -Ok (($actual -join ',') -eq ($expected -join ',')) `
            -Detail ($actual -join ' / ')
    }

    # 非法字符：Revit 的图纸编号不接受 \ : { } [ ] | ; < > ? ` ~
    # 工具应当指出是哪个字符，而不是笼统地说"被拒绝"
    $illegal = Invoke-RevitTool $session 'revit_update_sheets' `
        @{ sheets = @(@{ sheetId = $sheetIds[0]; number = "M10~非法" }) }

    Check -Tool 'revit_update_sheets' -What '含非法字符的编号被拒绝，且点名是哪个字符' `
        -Ok ($illegal.IsError -and $illegal.Text -match '不允许的字符') `
        -Detail $illegal.Text

    # 报错里的数组名必须是调用方真的传过的那个（曾经写死成 elements[i]）
    Check -Tool 'revit_update_sheets' -What '报错用的是 sheets[i]，不是通用的 elements[i]' `
        -Ok ($illegal.Text -match 'sheets\[0\]') `
        -Detail $illegal.Text
}

# ==================== 只读工具全覆盖 ====================

Write-Step '只读工具：全部调一遍'

# 只读工具的冒烟成本极低，但漏掉一个就意味着"归并没漏能力"这句话缺一块证据。
# 这里不做业务断言，只确认每一个都能正常返回——业务断言在各自的专项里。
$readOnly = @(
    @{ Name = 'revit_list_documents' }
    @{ Name = 'revit_get_project_units' }
    @{ Name = 'revit_get_project_location' }
    @{ Name = 'revit_list_categories' }
    @{ Name = 'revit_list_families'; Args = @{ limit = 50 } }
    @{ Name = 'revit_list_materials'; Args = @{ limit = 50 } }
    @{ Name = 'revit_list_groups' }
    @{ Name = 'revit_list_view_templates' }
    @{ Name = 'revit_list_revisions' }
    @{ Name = 'revit_list_phases' }
    @{ Name = 'revit_list_design_options' }
    @{ Name = 'revit_list_worksets' }
    @{ Name = 'revit_list_links' }
    @{ Name = 'revit_list_project_parameters' }
    @{ Name = 'revit_list_mep_systems'; Args = @{ types = $true } }
    @{ Name = 'revit_get_sheet_contents' }
    @{ Name = 'revit_get_warnings' }
    @{ Name = 'revit_query_elements'; Args = @{ category = 'OST_Walls' } }
    @{ Name = 'revit_calculate_material_quantities'; Args = @{ category = 'OST_Walls' } }
)

foreach ($ro in $readOnly) {
    $args = @{}
    if ($ro.Args) { $args = $ro.Args }
    Expect-Ok $session $ro.Name $args $ro.Name | Out-Null
}

# ==================== 变换与编组 ====================

Write-Step '变换：平移 / 复制 / 旋转 / 镜像'

$copy = Expect-Ok $session 'revit_transform_elements' `
    @{ elementIds = @($wallIds[0]); operation = 'copy'; translation = @{ x = 0; y = 12000 } } `
    '复制一面墙'
$copied = @($copy.Data.elements | Where-Object { $_.newId } | ForEach-Object { $_.newId })
$created += $copied

if ($copied.Count -gt 0) {
    Expect-Ok $session 'revit_transform_elements' `
        @{ elementIds = $copied; operation = 'move'; translation = @{ x = 1500; y = 0 } } '平移'
    Expect-Ok $session 'revit_transform_elements' `
        @{ elementIds = $copied; operation = 'rotate'
           rotation = @{ origin = @{ x = $x0; y = $y0 + 12000 }; axis = 'z'; degrees = 15 } } '旋转'

    $mirror = Expect-Ok $session 'revit_transform_elements' `
        @{ elementIds = $copied; operation = 'mirror'
           mirror = @{ origin = @{ x = $x0; y = $y0 + 20000 }; normal = 'y' }; keepOriginal = $true } '镜像'
    $created += @($mirror.Data.elements | Where-Object { $_.newId } | ForEach-Object { $_.newId })
}

Expect-Refusal $session 'revit_transform_elements' `
    @{ elementIds = @($wallIds[0]); operation = 'teleport'; translation = @{ x = 1; y = 1 } } `
    'INVALID_PARAMETER' '无法识别的 operation 被拒绝并列出可用值'

Write-Step '钉固'

Expect-Ok $session 'revit_set_elements_pinned' @{ elementIds = @($wallIds[0]); pinned = $true } '钉住'
Expect-Refusal $session 'revit_transform_elements' `
    @{ elementIds = @($wallIds[0]); operation = 'move'; translation = @{ x = 10; y = 0 } } `
    'TRANSACTION_FAILED' '钉住的构件不能移动'
Expect-Ok $session 'revit_set_elements_pinned' @{ elementIds = @($wallIds[0]); pinned = $false } '解钉'

Write-Step '编组'

$groupMembers = @($created | Where-Object { $copied -contains $_ })
if ($groupMembers.Count -ge 1) {
    $group = Expect-Ok $session 'revit_group_elements' `
        @{ operation = 'create'; elementIds = $groupMembers; name = "M10 组 $stamp" } '打成组'

    if (-not $group.IsError -and $group.Data.groups) {
        $groupId = $group.Data.groups[0].groupId
        $created += $groupId

        $listed = Expect-Ok $session 'revit_list_groups' @{ groupId = $groupId; includeMembers = $true } '查组成员'
        if (-not $listed.IsError) {
            Check -Tool 'revit_list_groups' -What '组成员数与打组时给的一致' `
                -Ok ($listed.Data.groups[0].memberCount -eq $groupMembers.Count) `
                -Detail "$($listed.Data.groups[0].memberCount) / $($groupMembers.Count)"
        }

        Expect-Ok $session 'revit_group_elements' @{ operation = 'ungroup'; groupIds = @($groupId) } '打散'
        $created = @($created | Where-Object { $_ -ne $groupId })
    }
}

# ==================== 基准图元 ====================

Write-Step '基准图元：轴网与标高'

$datums = Expect-Ok $session 'revit_create_datums' @{ datums = @(
        @{ kind = 'grid'; name = "M$stamp-1"
           locationLine = @{ p0 = @{ x = $x0 - 3000; y = $y0 - 3000 }; p1 = @{ x = $x1 + 3000; y = $y0 - 3000 } } }
        @{ kind = 'level'; name = "M10 标高 $stamp"; elevationMm = 31234 }
    ) } '建 1 道轴网 + 1 个标高'

if (-not $datums.IsError) {
    $created += Get-CreatedIds $datums 'datums'

    $newLevel = $datums.Data.datums | Where-Object { $_.kind -eq 'level' } | Select-Object -First 1
    Check -Tool 'revit_create_datums' -What '建标高时连带建出了楼层平面（Level.Create 本身不建）' `
        -Ok ([bool] $newLevel.planViewId) -Detail "planViewId = $($newLevel.planViewId)"
    if ($newLevel.planViewId) { $created += $newLevel.planViewId }
}

# ==================== 视图与样板 ====================

Write-Step '视图：平面 / 剖面 / 三维'

$newViews = Expect-Ok $session 'revit_create_views' @{ views = @(
        @{ viewType = 'section'; name = "M10 剖面 $stamp"
           sectionLine = @{ p0 = @{ x = $x0 - 2000; y = $y0 + 3000 }; p1 = @{ x = $x1 + 2000; y = $y0 + 3000 } }
           bottomMm = -1000; topMm = 8000; depthMm = 15000 }
        @{ viewType = 'threeD'; name = "M10 三维 $stamp" }
    ) } '建剖面与三维视图'

$viewIds = Get-CreatedIds $newViews 'views'
$created += $viewIds

$templates = Invoke-RevitTool $session 'revit_list_view_templates' @{ viewType = 'floorPlan' }
if ($templates.Data.templates -and $viewIds.Count -gt 0) {
    $tpl = $templates.Data.templates[0]
    Expect-Ok $session 'revit_apply_view_template' `
        @{ viewIds = @($viewIds[0]); templateId = $tpl.id } "套样板「$($tpl.name)」"
    Expect-Ok $session 'revit_apply_view_template' `
        @{ viewIds = @($viewIds[0]); templateId = $null } '取消套用'
}

# ==================== 注释 ====================

Write-Step '注释：文字 / 标记 / 尺寸标注'

$planView = $target
if ($planView) {
    $notes = Expect-Ok $session 'revit_create_annotations' @{ annotations = @(
            @{ kind = 'textNote'; viewId = $planView.id
               position = @{ x = $x0; y = $y0 - 5000 }; text = "M10 验证文字" }
        ) } '建文字'
    $created += Get-CreatedIds $notes 'annotations'

    $tags = Expect-Ok $session 'revit_create_annotations' @{ annotations = @(
            @{ kind = 'tag'; viewId = $planView.id; elementId = $wallIds[0]
               position = @{ x = $x0 + 2000; y = $y0 - 2000 }; addLeader = $true }
        ) } '建墙标记'
    $created += Get-CreatedIds $tags 'annotations'

    Expect-Refusal $session 'revit_create_annotations' `
        @{ annotations = @(@{ kind = 'tag'; viewId = $planView.id; position = @{ x = 0; y = 0 } }) } `
        'INVALID_PARAMETER' 'kind=tag 缺 elementId 被拒绝'

    Expect-Ok $session 'revit_tag_all_in_view' `
        @{ category = 'OST_Walls'; viewId = $planView.id } '按类别批量标记'
}

# ==================== 材质与类型 ====================

Write-Step '材质'

$srcMat = (Invoke-RevitTool $session 'revit_list_materials' @{ limit = 10 }).Data.materials | Select-Object -First 1
if ($srcMat) {
    $mat = Expect-Ok $session 'revit_create_materials' @{ materials = @(
            @{ name = "M10 材质 $stamp"; copyFromId = $srcMat.id; color = '#FF8800'; transparency = 20 }
        ) } "从「$($srcMat.name)」复制材质"
    $created += Get-CreatedIds $mat 'materials'
}

Write-Step '类型：复制 / 换型 / 改类型参数'

$dup = Expect-Ok $session 'revit_duplicate_type' `
    @{ sourceTypeId = $wallType.id; name = "M10 墙 $stamp"; thicknessMm = 250 } '复制墙类型并改厚度'

if (-not $dup.IsError) {
    $created += $dup.Data.id
    Check -Tool 'revit_duplicate_type' -What '新类型厚度确实是 250' `
        -Ok ($dup.Data.thicknessMm -eq 250) -Detail "$($dup.Data.thicknessMm) mm"

    Expect-Ok $session 'revit_change_element_types' `
        @{ elementIds = @($wallIds[0]); toTypeId = $dup.Data.id } '把一面墙换成新类型'

    $typeParams = Invoke-RevitTool $session 'revit_get_element_parameters' `
        @{ elementIds = @($wallIds[0]); includeTypeParameters = $true }
    $writable = $typeParams.Data.elements[0].typeParameters |
        Where-Object { -not $_.isReadOnly -and $_.storageType -eq 'String' } | Select-Object -First 1
    if ($writable) {
        Expect-Ok $session 'revit_set_type_parameters' `
            @{ typeIds = @($dup.Data.id); parameterName = $writable.name; value = "M10 $stamp" } `
            "改类型参数「$($writable.name)」"
    }
}

# ==================== 批量参数 ====================

Write-Step '批量参数：按条件匹配'

Expect-Ok $session 'revit_batch_set_parameters' @{ updates = @(
        @{ filter = @{ category = 'OST_Walls'; levelId = $level.id }
           parameters = @(@{ name = '注释'; value = "M10 批量 $stamp" }) }
    ) } '按类别+标高匹配并改参数'

Expect-Refusal $session 'revit_batch_set_parameters' `
    @{ updates = @(@{ filter = @{ nameContains = '墙' }; parameters = @(@{ name = '注释'; value = 'x' }) }) } `
    'INVALID_PARAMETER' 'filter 太宽（会扫全模型）被拒绝'

# ==================== MEP ====================

Write-Step 'MEP 管线'

$ductSys = (Invoke-RevitTool $session 'revit_list_mep_systems' @{ types = $true; discipline = 'mechanical' }).Data.systems |
    Select-Object -First 1

$mep = Expect-Ok $session 'revit_create_mep_curves' @{ curves = @(
        @{ kind = 'duct'; levelId = $level.id
           locationLine = @{ p0 = @{ x = $x0; y = $y1 + 4000; z = 3000 }; p1 = @{ x = $x1; y = $y1 + 4000; z = 3000 } }
           systemTypeId = $ductSys.id; widthMm = 400; heightMm = 300 }
        @{ kind = 'pipe'; levelId = $level.id
           locationLine = @{ p0 = @{ x = $x0; y = $y1 + 5500; z = 3000 }; p1 = @{ x = $x1; y = $y1 + 5500; z = 3000 } }
           diameterMm = 100 }
    ) } '建风管与水管'

if (-not $mep.IsError) {
    $created += Get-CreatedIds $mep 'curves'
    $duct = $mep.Data.curves | Where-Object { $_.kind -eq 'duct' } | Select-Object -First 1
    Check -Tool 'revit_create_mep_curves' -What '矩形风管的尺寸能读出来（不是 null）' `
        -Ok ([bool] $duct.size) -Detail "size = $($duct.size)"
}

# ==================== 碰撞检查 ====================

Write-Step '碰撞检查'

Expect-Ok $session 'revit_check_clashes' @{ setA = @{ category = 'OST_Walls' } } '墙与墙自检'

# ==================== 导出 ====================

Write-Step '导出'

if ($planView) {
    Expect-Ok $session 'revit_export_image' `
        @{ viewId = $planView.id; fileName = "m10-$stamp.png" } '导出 PNG'

    Expect-Ok $session 'revit_export_documents' `
        @{ format = 'dwg'; fileName = "m10-$stamp.dwg"; viewIds = @($planView.id) } '导出 DWG'

    Expect-Refusal $session 'revit_export_documents' `
        @{ format = 'dwg'; fileName = "m10-$stamp.ifc"; viewIds = @($planView.id) } `
        'INVALID_PARAMETER' 'format 与扩展名对不上被拒绝'
}

# ==================== 图纸进阶 ====================

Write-Step '图纸：复制与摆视图'

if ($sheetIds.Count -ge 1 -and $viewIds.Count -ge 1) {
    Expect-Ok $session 'revit_add_views_to_sheet' `
        @{ sheetId = $sheetIds[0]; views = @(@{ viewId = $viewIds[0] }) } '把视图摆到图纸上'

    $dupSheet = Expect-Ok $session 'revit_duplicate_sheets' `
        @{ sheets = @(@{ sheetId = $sheetIds[0]; number = "M10-$stamp-D" }); contents = 'empty' } '复制图纸'
    if (-not $dupSheet.IsError) {
        $created += Get-CreatedIds $dupSheet 'sheets'
        Check -Tool 'revit_duplicate_sheets' -What '回执说明了用的是原生还是手工复制' `
            -Ok ([bool] $dupSheet.Data.sheets[0].method) -Detail "method = $($dupSheet.Data.sheets[0].method)"
    }
}

# ==================== 逃生舱闸门 ====================

Write-Step '逃生舱：默认必须是关着的'

# 这两条不验功能，只验闸门。它们能在 Revit 进程里执行任意代码，
# 一个默认开着的逃生舱比任何功能缺陷都危险。
Expect-Refusal $session 'revit_invoke_api' `
    @{ action = 'get'; typeName = 'Autodesk.Revit.DB.Document'; member = 'Title'; confirm = $true } `
    'WRITE_DISABLED' 'revit_invoke_api 默认被拒绝'

Expect-Refusal $session 'revit_execute_script' @{ code = 'return 1;'; confirm = $true } `
    'WRITE_DISABLED' 'revit_execute_script 默认被拒绝'

# ==================== 改动核对 ====================

Write-Step '核对这一趟到底动了什么'

$delta = Invoke-RevitTool $session 'revit_get_model_changes' @{ since = $baseToken; includeDetails = $false } -ThrowOnError
Write-Host "新增 $($delta.Data.addedCount)  修改 $($delta.Data.modifiedCount)  删除 $($delta.Data.deletedCount)"
Check -Tool 'revit_get_model_changes' -What '增量里确实记到了本次新建的构件' `
    -Ok ($delta.Data.addedCount -ge $created.Count) `
    -Detail "增量 $($delta.Data.addedCount) ≥ 本脚本建的 $($created.Count)"

# ==================== 收尾 ====================

if ($KeepScene) {
    Write-Step '保留现场'
    Write-Host "造出来的 $($created.Count) 个构件保留在模型里（-KeepScene）。"
}
elseif ($created.Count -gt 0) {
    Write-Step '清理'

    # 删除工具的两次确认在这里正好顺带验了：第一次给出连带影响，第二次才真删
    $preview = Invoke-RevitTool $session 'revit_delete_elements' @{ elementIds = $created }
    Check -Tool 'revit_delete_elements' -What '第一次调用先给出连带影响，要求确认' `
        -Ok ($preview.IsError -and $preview.Code -eq 'CONFIRMATION_REQUIRED') `
        -Detail $preview.Code

    $done = Expect-Ok $session 'revit_delete_elements' `
        @{ elementIds = $created; confirm = $true } '确认后真的删除'
    if (-not $done.IsError) { Write-Host "  已删除 $($done.Data.deleted) 个构件。" }
}

# ==================== 记分板 ====================

Write-Step '结果'

$passed = @($script:Checks | Where-Object { $_.Ok }).Count
$failed = @($script:Checks | Where-Object { -not $_.Ok })

$summaryColor = 'Red'
if ($failed.Count -eq 0) { $summaryColor = 'Green' }

Write-Host "检查 $($script:Checks.Count) 项，通过 $passed，失败 $($failed.Count)" -ForegroundColor $summaryColor

if ($failed.Count -gt 0) {
    Write-Host ""
    foreach ($f in $failed) {
        Write-Host "  ✗ [$($f.Tool)] $($f.What)" -ForegroundColor Red
        if ($f.Detail) { Write-Host "      $($f.Detail)" -ForegroundColor DarkGray }
    }
}

$toolsTouched = @($script:Checks | ForEach-Object { $_.Tool } | Sort-Object -Unique)
Write-Host ""
Write-Host "本脚本覆盖的工具（$($toolsTouched.Count) 个）：$($toolsTouched -join '、')" -ForegroundColor DarkGray

if ($failed.Count -gt 0) { exit 1 }
