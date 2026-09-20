<#
.SYNOPSIS
    M10-D 正确性验收：把《源码评估与改进报告》里那几条"调用成功、模型却不对"的问题逐条量回来。

.DESCRIPTION
    这些问题的共同点是**调用不报错**：回执漂亮、警告为 0，错的只有几何和约束。
    所以这里一条都不看 isError，全部靠量：

      F01 楼板竖向定位  —— 省略 baseOffset 的板必须落在它的标高上，不是项目零高程
                            （旧实现：二层板的「自标高的高度偏移」被写成 −层高，板堆在 z=0）
      F01b 偏移语义      —— baseOffset 给正、给负、给 0，顶面高程都要对得上
      F06 柱底部约束    —— 标高 + baseOffset 的柱，底面必须在"标高 + 偏移"上
                            （旧实现：150 毫米的底偏移被丢掉，柱底停在标高平面）
      F03 立面          —— viewType: elevation 要真的建出 Elevation 视图，且朝向是给的那个方向
                            （旧实现：拿 Elevation 的视图族类型去调 CreateSection，必然失败）
      F04 房间标记      —— 房间标记要建得出来，且第二次带 skipTagged 不再重复加
                            （旧实现：一律走 IndependentTag，房间无论有没有载入标记族都失败）
      F02 目标文档      —— expectedDocumentId 指着别的文档时必须拒绝，且模型一个字节都不许动
      F11 图纸排版      —— 视口整个外框越界要报出来，不能只看中心点

    默认在 (80000, 80000) 附近作业、结束时清理干净，可以反复运行。
    需要写入模式打开。**不会保存**——本脚本只验行为，不动用户的文件。

.PARAMETER Keep
    保留本次建的东西，不在结束时清理。想在 Revit 里亲眼看看结果时用。

.PARAMETER ProcessId
    同时开着多个 Revit 时，指定连哪一个。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m10d-correctness.ps1
#>
[CmdletBinding()]
param(
    [double] $OriginX = 80000,
    [double] $OriginY = 80000,
    [switch] $Keep,
    [int] $ProcessId,
    [string] $Endpoint
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\McpClient.ps1"

$script:Failures = @()
$script:Created = @()
$script:CreatedViews = @()

function Assert-Close {
    param(
        [string] $What,
        [double] $Actual,
        [double] $Expected,
        [double] $ToleranceMm = 1.0,
        [string] $IfWrong
    )

    $delta = [Math]::Abs($Actual - $Expected)

    if ($delta -le $ToleranceMm) {
        Write-Host ("  ✓ {0}：{1:N1}（期望 {2:N1}）" -f $What, $Actual, $Expected) -ForegroundColor Green
        return
    }

    Write-Host ("  ✗ {0}：{1:N1}，期望 {2:N1}，差 {3:N1}" -f $What, $Actual, $Expected, $delta) -ForegroundColor Red
    if ($IfWrong) { Write-Host "    $IfWrong" -ForegroundColor Red }
    $script:Failures += $What
}

function Assert-True {
    param([string] $What, [bool] $Condition, [string] $IfWrong)

    if ($Condition) {
        Write-Host ("  ✓ {0}" -f $What) -ForegroundColor Green
        return
    }

    Write-Host ("  ✗ {0}" -f $What) -ForegroundColor Red
    if ($IfWrong) { Write-Host "    $IfWrong" -ForegroundColor Red }
    $script:Failures += $What
}

function New-Segment {
    param([double] $X0, [double] $Y0, [double] $X1, [double] $Y1)
    @{ p0 = @{ x = $X0; y = $Y0 }; p1 = @{ x = $X1; y = $Y1 } }
}

function New-Rectangle {
    param([double] $X0, [double] $Y0, [double] $X1, [double] $Y1)
    @(
        (New-Segment $X0 $Y0 $X0 $Y1)
        (New-Segment $X0 $Y1 $X1 $Y1)
        (New-Segment $X1 $Y1 $X1 $Y0)
        (New-Segment $X1 $Y0 $X0 $Y0)
    )
}

function Get-InkRatio {
    <#
    .SYNOPSIS
        一张图里有多少不是白的。这就是"有效内容占比"。

    .DESCRIPTION
        为了快，按 4 像素步长采样，够用来比较两张同一视图导出的图。
        取不到就返回 0——量不了不该让整条验收崩掉。
    #>
    param([string] $Path)

    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction Stop
        $bitmap = [System.Drawing.Bitmap]::FromFile($Path)
        try {
            $ink = 0
            $total = 0
            for ($y = 0; $y -lt $bitmap.Height; $y += 4) {
                for ($x = 0; $x -lt $bitmap.Width; $x += 4) {
                    $total++
                    $pixel = $bitmap.GetPixel($x, $y)
                    if ($pixel.R -lt 245 -or $pixel.G -lt 245 -or $pixel.B -lt 245) { $ink++ }
                }
            }
            if ($total -eq 0) { return 0 }
            return $ink / $total
        }
        finally { $bitmap.Dispose() }
    }
    catch {
        Write-Host "    （读不出图片，跳过占比统计：$($_.Exception.Message)）" -ForegroundColor DarkYellow
        return 0
    }
}

function Get-Box {
    param([PSCustomObject] $Session, [string] $Id)

    $geometry = Invoke-RevitTool $Session 'revit_get_element_geometry' `
        @{ elementIds = @($Id) } -ThrowOnError

    $element = $geometry.Data.elements | Select-Object -First 1
    if (-not $element) { throw "构件 $Id 查不到几何。" }
    return $element.boundingBox
}

# ==================== 连接 ====================

Write-Step '连接'

$connectArgs = @{}
if ($ProcessId) { $connectArgs['ProcessId'] = $ProcessId }
if ($Endpoint) { $connectArgs['Endpoint'] = $Endpoint }
$session = Connect-RevitMcp @connectArgs

$info = Invoke-RevitTool $session 'revit_get_document_info' -ThrowOnError
Write-Host "文档 「$($info.Data.title)」  Revit $($info.Data.revitVersion)"

if (-not $info.Data.writeEnabled) {
    Write-Host "写入模式没有开启，本工作流需要建模。" -ForegroundColor Red
    exit 2
}

$activeDocumentId = $info.Data.pathName
if (-not $activeDocumentId) { $activeDocumentId = $info.Data.title }

# ==================== 准备：两条标高、一个楼板类型 ====================

Write-Step '准备：标高与类型'

$levels = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError
$baseLevel = $levels.Data.levels | Sort-Object elevationMm | Select-Object -First 1

# 专门为这次验收建一条高标高：必须是非零高程，F01 的 bug 在零标高上看不出来
$testElevation = [double]$baseLevel.elevationMm + 12345

$datums = Invoke-RevitTool $session 'revit_create_datums' @{
    datums = @(@{
        kind           = 'level'
        name           = "M10D 验收 $([DateTime]::Now.ToString('HHmmss'))"
        elevationMm    = $testElevation
        createPlanView = $true
    })
} -ThrowOnError

$upperLevel = $datums.Data.datums | Select-Object -First 1
$script:Created += $upperLevel.id
Write-Host "验收标高 「$($upperLevel.name)」 高程 $($upperLevel.elevationMm) mm"

$floorTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Floors'; limit = 50 } -ThrowOnError
$floorType = $floorTypes.Data.types | Where-Object { $_.thicknessMm -gt 0 } | Select-Object -First 1
if (-not $floorType) { throw '项目里没有带厚度的楼板类型。' }

Write-Host "楼板类型 「$($floorType.name)」 厚 $($floorType.thicknessMm) mm"

# ==================== F01：楼板落在它的标高上 ====================

Write-Step 'F01 楼板竖向定位'

$cases = @(
    @{ Label = '省略 baseOffset'; Offset = $null; Expected = $testElevation }
    @{ Label = 'baseOffset = 0'; Offset = 0; Expected = $testElevation }
    @{ Label = 'baseOffset = 300'; Offset = 300; Expected = $testElevation + 300 }
    @{ Label = 'baseOffset = -450'; Offset = -450; Expected = $testElevation - 450 }
)

$slot = 0
foreach ($case in $cases) {
    $x = $OriginX + $slot * 5000
    $spec = @{
        category = 'OST_Floors'
        typeId   = $floorType.id
        levelId  = $upperLevel.id
        boundary = @{ outerLoop = (New-Rectangle $x $OriginY ($x + 3000) ($OriginY + 3000)) }
    }
    if ($null -ne $case.Offset) { $spec['baseOffset'] = $case.Offset }

    $reply = Invoke-RevitTool $session 'revit_create_surface_based_elements' `
        @{ elements = @($spec) } -ThrowOnError

    $floor = $reply.Data.elements | Select-Object -First 1
    $script:Created += $floor.id

    # 顶面高程：楼板的「自标高的高度偏移」定的是顶面
    $box = Get-Box $session $floor.id
    Assert-Close "$($case.Label) 顶面高程" ([double]$box.max.z) ([double]$case.Expected) 1.0 `
        '楼板忽略了 levelId——边界被压到项目零高程后，Revit 把偏移记成了 −标高高程（F01）。'

    # 回执自己也要说得对，别让调用方再查一次
    Assert-Close "$($case.Label) 回执 elevationMm" ([double]$floor.elevationMm) ([double]$case.Expected) 1.0 `
        '回执里的 elevationMm 与实际几何不符——回执必须来自写完读回来的值。'

    $slot++
}

# ==================== F06：柱的底部约束 ====================

Write-Step 'F06 柱底部约束'

$columnTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Columns'; limit = 20 } -ThrowOnError
$columnType = $columnTypes.Data.types | Select-Object -First 1

if (-not $columnType) {
    Write-Host "  · 项目里没有建筑柱类型，跳过 F06" -ForegroundColor DarkYellow
}
else {
    $column = Invoke-RevitTool $session 'revit_create_point_based_elements' @{
        elements = @(@{
            category      = 'OST_Columns'
            typeId        = $columnType.id
            locationPoint = @{ x = $OriginX; y = $OriginY + 8000 }
            levelId       = $upperLevel.id
            baseOffset    = 150
        })
    } -ThrowOnError

    $newColumn = $column.Data.elements | Select-Object -First 1
    $script:Created += $newColumn.id

    $box = Get-Box $session $newColumn.id
    Assert-Close '柱底面高程（标高 + 150）' ([double]$box.min.z) ($testElevation + 150) 1.0 `
        'baseOffset 被丢掉了：NewFamilyInstance 只拿插入点定位，底部偏移必须显式写（F06）。'

    Assert-Close '柱回执 baseOffsetMm' ([double]$newColumn.baseOffsetMm) 150 0.1 `
        '回执没有报出实际写进去的底部偏移。'
}

# ==================== F03：立面 ====================

Write-Step 'F03 立面'

$families = Invoke-RevitTool $session 'revit_list_view_family_types' @{ viewType = 'elevation' } -ThrowOnError
Assert-True '能列出立面的视图族类型' ($families.Data.total -gt 0) `
    'revit_list_view_family_types 查不到立面类型——没有它，"请显式指定 viewFamilyTypeId" 就是一条死路。'

# 站在建筑南侧朝北看
$elevation = Invoke-RevitTool $session 'revit_create_views' @{
    views = @(@{
        viewType    = 'elevation'
        name        = "M10D 南立面 $([DateTime]::Now.ToString('HHmmss'))"
        sectionLine = @{
            p0 = @{ x = $OriginX; y = $OriginY - 6000 }
            p1 = @{ x = $OriginX; y = $OriginY }
        }
        depthMm     = 20000
        scale       = 100
    })
}

if ($elevation.IsError) {
    Assert-True '立面创建成功' $false "创建失败：$($elevation.Text)"
}
else {
    $view = $elevation.Data.views | Select-Object -First 1
    $script:CreatedViews += $view.id

    Assert-True '立面的 viewType 是 Elevation' ($view.viewType -eq 'Elevation') `
        "实际是 $($view.viewType)——用剖面冒充立面不算通过（F03）。"
}

# ==================== F04：房间标记 ====================

Write-Step 'F04 房间标记'

$roomTagTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_RoomTags'; limit = 10 }
$rooms = Invoke-RevitTool $session 'revit_list_rooms' @{ limit = 5 }
$room = $null
if (-not $rooms.IsError) { $room = $rooms.Data.rooms | Where-Object { $_.isBounded } | Select-Object -First 1 }

if (-not $room) {
    Write-Host "  · 模型里没有围合的房间，跳过 F04（这条要在有房间的模型上跑）" -ForegroundColor DarkYellow
}
elseif ($roomTagTypes.IsError -or $roomTagTypes.Data.total -eq 0) {
    Write-Host "  · 项目里没有载入房间标记族，跳过 F04" -ForegroundColor DarkYellow
}
else {
    $plans = Invoke-RevitTool $session 'revit_list_views' @{ limit = 200 } -ThrowOnError
    $plan = $plans.Data.views |
        Where-Object { $_.viewType -eq 'FloorPlan' -and $_.levelId -eq $room.levelId } |
        Select-Object -First 1

    if (-not $plan) {
        Write-Host "  · 房间所在标高没有平面视图，跳过 F04" -ForegroundColor DarkYellow
    }
    else {
        $tag = Invoke-RevitTool $session 'revit_create_annotations' @{
            annotations = @(@{
                kind      = 'tag'
                viewId    = $plan.id
                elementId = $room.id
                position  = @{ x = $room.location.x; y = $room.location.y }
            })
        }

        if ($tag.IsError) {
            Assert-True '房间标记建得出来' $false `
                "创建失败：$($tag.Text)（旧实现在这里必然失败：房间要走 NewRoomTag，不是 IndependentTag）"
        }
        else {
            $createdTag = $tag.Data.annotations | Select-Object -First 1
            $script:Created += $createdTag.id
            Assert-True '房间标记建得出来' $true

            # 去重：第二次带 skipTagged 不该再给同一个房间加一个
            $again = Invoke-RevitTool $session 'revit_tag_all_in_view' `
                @{ category = 'OST_Rooms'; viewId = $plan.id; skipTagged = $true }

            if (-not $again.IsError) {
                Assert-True '已标记的房间不会被重复标记' ($again.Data.alreadyTagged -ge 1) `
                    'AlreadyTaggedIn 只数了 IndependentTag，房间标记没被算进去，于是重复加了一个。'

                foreach ($id in $again.Data.tagIds) { $script:Created += $id }
            }
        }
    }
}

# ==================== F02：目标文档前置条件 ====================

Write-Step 'F02 目标文档前置条件'

$wrong = Invoke-RevitTool $session 'revit_create_datums' @{
    expectedDocumentId = 'C:\不存在的项目\别的文档.rvt'
    datums             = @(@{
        kind        = 'level'
        name        = 'M10D 不该被创建'
        elevationMm = $testElevation + 999
    })
}

Assert-True 'expectedDocumentId 不匹配时被拒绝' `
    ($wrong.IsError -and $wrong.Code -eq 'WRONG_DOCUMENT') `
    "期望 WRONG_DOCUMENT，实际 IsError=$($wrong.IsError) Code=$($wrong.Code)。"

$after = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError
Assert-True '被拒绝的写入没有改动模型' `
    (-not ($after.Data.levels | Where-Object { $_.name -eq 'M10D 不该被创建' })) `
    '那条标高真的被建出来了——前置校验没有拦住写入。'

$right = Invoke-RevitTool $session 'revit_create_datums' @{
    expectedDocumentId = $activeDocumentId
    datums             = @(@{
        kind        = 'level'
        name        = "M10D 匹配 $([DateTime]::Now.ToString('HHmmss'))"
        elevationMm = $testElevation + 1500
    })
}

Assert-True 'expectedDocumentId 匹配时照常执行' (-not $right.IsError) `
    "匹配也被拒了：$($right.Text)"

if (-not $right.IsError) {
    foreach ($datum in $right.Data.datums) { $script:Created += $datum.id }
}

# ==================== F11：图纸排版 ====================

Write-Step 'F11 图纸越界检查'

$sheet = Invoke-RevitTool $session 'revit_create_sheets' @{
    elements = @(@{ number = "M10D-$([DateTime]::Now.ToString('HHmmss'))"; name = 'M10D 排版验收' })
}

if ($sheet.IsError) {
    Write-Host "  · 建不出图纸（$($sheet.Text)），跳过 F11" -ForegroundColor DarkYellow
}
else {
    $sheetId = ($sheet.Data.elements | Select-Object -First 1).id
    $script:Created += $sheetId

    $anyPlan = (Invoke-RevitTool $session 'revit_list_views' @{ limit = 200 } -ThrowOnError).Data.views |
        Where-Object { $_.viewType -eq 'FloorPlan' -and -not $_.sheetId } |
        Select-Object -First 1

    if (-not $anyPlan) {
        Write-Host "  · 没有空闲的平面视图可放，跳过 F11" -ForegroundColor DarkYellow
    }
    else {
        # 故意把视口中心放在纸内、外框探出纸外
        $placed = Invoke-RevitTool $session 'revit_add_views_to_sheet' @{
            sheetId = $sheetId
            views   = @(@{ viewId = $anyPlan.id; position = @{ x = 30; y = 300 } })
        } -ThrowOnError

        $box = ($placed.Data.views | Select-Object -First 1).box
        $outline = $placed.Data.sheetOutline
        $overflows = $box -and $outline -and ($box.min.x -lt $outline.min.x - 1)

        if (-not $overflows) {
            Write-Host "  · 这个视口没有越界（视图比例太小），F11 这次没验到" -ForegroundColor DarkYellow
        }
        else {
            $warned = @($placed.Warnings | Where-Object { $_ -match '超出图纸范围' }).Count -gt 0
            Assert-True '整个外框越界会被报出来' $warned `
                '视口左边已经探出纸外，但没有任何警告——只看中心点的检查漏掉了它（F11）。'
        }
    }
}

# ==================== F14：能力补齐 ====================

Write-Step 'F14a 房间竖向约束'

$roomLevels = (Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError).Data.levels |
    Sort-Object elevationMm
$roomBase = $roomLevels | Select-Object -First 1
$roomTop = $roomLevels | Where-Object { $_.elevationMm -gt $roomBase.elevationMm } | Select-Object -First 1

$existingRoom = $null
$roomList = Invoke-RevitTool $session 'revit_list_rooms' @{ limit = 5 }
if (-not $roomList.IsError) {
    $existingRoom = $roomList.Data.rooms | Where-Object { $_.isBounded -and $_.levelId -eq $roomBase.id } | Select-Object -First 1
}

if (-not $roomTop) {
    Write-Host "  · 只有一条标高，跳过 F14a" -ForegroundColor DarkYellow
}
elseif (-not $existingRoom) {
    Write-Host "  · 底标高上没有围合房间，跳过 F14a（需要一个有房间的模型）" -ForegroundColor DarkYellow
}
else {
    # 在已有房间里再放一个：会得到"多余的房间"警告，但高度参数照样能验，建完就删
    $room = Invoke-RevitTool $session 'revit_create_rooms' @{
        elements = @(@{
            locationPoint     = @{ x = $existingRoom.location.x; y = $existingRoom.location.y }
            levelId           = $roomBase.id
            name              = 'M10E 高度验收'
            upperLimitLevelId = $roomTop.id
            limitOffsetMm     = -200
        })
    } -ThrowOnError

    $newRoom = $room.Data.elements | Select-Object -First 1
    $script:Created += $newRoom.id

    $expectedHeight = [double]$roomTop.elevationMm - [double]$roomBase.elevationMm - 200

    Assert-Close '房间高度 = 上限标高 - 底标高 - 200' `
        ([double]$newRoom.unboundedHeightMm) $expectedHeight 1.0 `
        '房间上限没设上，高度还是 Revit 默认的 2438.4 毫米（F14）。'

    Assert-True '回执报出了上限标高' ($newRoom.upperLimit -eq $roomTop.name) `
        "回执里的 upperLimit 是「$($newRoom.upperLimit)」，期望「$($roomTop.name)」。"
}

Write-Step 'F14b 视图取景'

$view3d = (Invoke-RevitTool $session 'revit_list_views' @{ limit = 200 } -ThrowOnError).Data.views |
    Where-Object { $_.viewType -eq 'ThreeD' -and -not $_.sheetId } |
    Select-Object -First 1

if (-not $view3d) {
    Write-Host "  · 没有空闲的三维视图，跳过 F14b" -ForegroundColor DarkYellow
}
else {
    # 先把这个视图放开，建立一个确定的基线。
    # 不这么做的话，只要之前有人（包括上一次跑本脚本）收紧过它，
    # "收紧前"那张图就已经是收好的，前后占比一样，断言会假失败
    Invoke-RevitTool $session 'revit_set_view_extent' `
        @{ viewId = $view3d.id; cropActive = $false } -ThrowOnError | Out-Null

    $before = Invoke-RevitTool $session 'revit_export_image' `
        @{ viewId = $view3d.id; fileName = 'm10e-before.png'; pixelWidth = 1200 } -ThrowOnError

    # 按建筑的墙取景，而不是"所有可见构件"——本脚本自己在 80 米外建了验收用的板，
    # 那些板也是可见构件，按它们算出来的范围会把建筑挤成一个点。
    # 这同时验到了 elementIds 这条路径
    $walls = (Invoke-RevitTool $session 'revit_query_elements' `
        @{ category = 'OST_Walls'; limit = 100 } -ThrowOnError).Data.elements

    $extentArgs = @{ viewId = $view3d.id; paddingMm = 1000 }
    if ($walls -and $walls.Count -gt 0) { $extentArgs['elementIds'] = @($walls | ForEach-Object { $_.id }) }

    $extent = Invoke-RevitTool $session 'revit_set_view_extent' $extentArgs

    if ($extent.IsError) {
        Assert-True '取景收得动' $false "失败：$($extent.Text)"
    }
    else {
        Assert-True '取景后裁剪是开启的' ([bool]$extent.Data.cropActive) '裁剪没打开，取景不会生效。'
        Assert-True '三维视图连剖切框一起收紧' ([bool]$extent.Data.sectionBoxActive) `
            '三维只裁画面不剖切的话，框外的构件还在画面里。'
        Assert-True '基准图元被隐藏了' ([bool]$extent.Data.datumsHidden) `
            '标高线不受剖切框约束，不关掉它们，图里还是它们占大头。'
        Assert-True '报出了取景范围' ($null -ne $extent.Data.extent) '回执没有给出取景范围。'

        $after = Invoke-RevitTool $session 'revit_export_image' `
            @{ viewId = $view3d.id; fileName = 'm10e-after.png'; pixelWidth = 1200 } -ThrowOnError

        # **量图本身，不量标志位。**第一次跑这条时三个标志位全是对的，
        # 导出的图却更差了——建筑被挤到角落，裁剪区落在一片空地上。
        # 所以这里直接数非白像素占比：它就是报告里说的"图中有效内容占比"
        $inkBefore = Get-InkRatio $before.Data.path
        $inkAfter = Get-InkRatio $after.Data.path

        Write-Host ("  · 有效内容占比：收紧前 {0:P2}，收紧后 {1:P2}" -f $inkBefore, $inkAfter) -ForegroundColor DarkGray
        Write-Host ("    图在 {0}" -f (Split-Path $after.Data.path)) -ForegroundColor DarkGray

        Assert-True '收紧之后图里的有效内容变多了' ($inkAfter -gt $inkBefore * 1.5) `
            ("收紧前 {0:P2}、收紧后 {1:P2}——取景没起作用。" -f $inkBefore, $inkAfter)

        # 还原：这是用户的视图，验收不该把它留在收紧的状态
        $restore = Invoke-RevitTool $session 'revit_set_view_extent' `
            @{ viewId = $view3d.id; cropActive = $false }
        Assert-True '能还原成不裁剪' ((-not $restore.IsError) -and (-not $restore.Data.cropActive)) `
            "还原失败：$($restore.Text)"
    }
}

Write-Step 'F14c 载入族（只验错误路径，不往项目里塞族）'

$missing = Invoke-RevitTool $session 'revit_load_family' `
    @{ path = 'C:\这个文件不存在\某个族.rfa' }

Assert-True '路径不存在时说得清楚' `
    ($missing.IsError -and $missing.Code -eq 'ELEMENT_NOT_FOUND') `
    "期望 ELEMENT_NOT_FOUND，实际 Code=$($missing.Code)：$($missing.Text)"

$wrongExt = Invoke-RevitTool $session 'revit_load_family' `
    @{ path = 'C:\Users\pc-bim\Desktop\RevitTest\MCPModelTest.rvt' }

Assert-True '把 .rvt 当族载入会被拦下' `
    ($wrongExt.IsError -and $wrongExt.Code -eq 'INVALID_PARAMETER') `
    "期望 INVALID_PARAMETER，实际 Code=$($wrongExt.Code)：$($wrongExt.Text)"

# 真的载一个族：从 Revit 自带库里挑一个，载完就删，不留在项目里
$libraryFamily = Get-ChildItem -Path @(
    "C:\ProgramData\Autodesk\RVT 2019\Libraries\China\注释\标记",
    "C:\ProgramData\Autodesk\RVT 2019\Libraries\China\注释"
) -Filter '*.rfa' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $libraryFamily) {
    Write-Host "  · 本机没有 Revit 族库，跳过真实载入" -ForegroundColor DarkYellow
}
else {
    $load = Invoke-RevitTool $session 'revit_load_family' @{ path = $libraryFamily.FullName }

    if ($load.IsError) {
        Assert-True '载入族' $false "失败：$($load.Text)"
    }
    else {
        Write-Host ("  · 载入「{0}」（{1}），loaded={2}" -f $load.Data.name, $load.Data.category, $load.Data.loaded) -ForegroundColor DarkGray
        Assert-True '回执带回了类型 ID' ($load.Data.types.Count -gt 0) `
            '载入成功却没给类型 ID，调用方还得再查一次 revit_list_types。'

        # 只删我们这次载进来的；项目里本来就有的不动
        if ($load.Data.loaded) { $script:Created += $load.Data.id }
    }
}

# ==================== F09：幂等与操作状态 ====================

Write-Step 'F09 幂等键与操作状态'

$stamp = [DateTime]::Now.ToString('HHmmss')
$key = "m10f-$stamp"
$levelName = "M10F 幂等 $stamp"

$makeLevel = @{
    requestKey = $key
    datums     = @(@{ kind = 'level'; name = $levelName; elevationMm = ($testElevation + 3000); createPlanView = $false })
}

$firstCall = Invoke-RevitTool $session 'revit_create_datums' $makeLevel -ThrowOnError
$madeLevel = $firstCall.Data.datums | Select-Object -First 1
$script:Created += $madeLevel.id

Assert-True '写回执带 operationId' (-not [string]::IsNullOrEmpty($firstCall.Data.operationId)) `
    '超时那一刻，回执里的 operationId 是调用方唯一的抓手。'

# 原样重发：这正是超时后会做的事，**不能再建一条标高**
$secondCall = Invoke-RevitTool $session 'revit_create_datums' $makeLevel

Assert-True '同键同参重发不会再执行一次' (-not $secondCall.IsError) `
    "重发被拒了：$($secondCall.Text)"

$levelsNow = (Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError).Data.levels
$sameName = @($levelsNow | Where-Object { $_.name -eq $levelName })

Assert-True '模型里只有一条这个名字的标高' ($sameName.Count -eq 1) `
    "实际有 $($sameName.Count) 条——幂等没生效，重复创建已经发生。"

if (-not $secondCall.IsError) {
    $replayed = @($secondCall.Warnings | Where-Object { $_ -match '这是重放' }).Count -gt 0
    Assert-True '重放这件事被说了出来' $replayed `
        '回执没有说明这是重放，调用方会以为又建了一条。'
}

# 同键异参：必须拒绝，且**什么都不建**
$conflict = Invoke-RevitTool $session 'revit_create_datums' @{
    requestKey = $key
    datums     = @(@{ kind = 'level'; name = "$levelName 不该出现"; elevationMm = ($testElevation + 4000) })
}

Assert-True '同键异参被拒绝' ($conflict.IsError -and $conflict.Code -eq 'IDEMPOTENCY_CONFLICT') `
    "期望 IDEMPOTENCY_CONFLICT，实际 Code=$($conflict.Code)：$($conflict.Text)"

$after = (Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError).Data.levels
Assert-True '被拒绝的那次没有建出任何东西' `
    (-not ($after | Where-Object { $_.name -like '*不该出现*' })) `
    '冲突的那次居然建出了标高。'

# 状态查询
$status = Invoke-RevitTool $session 'revit_get_operation_status' @{ operationId = $firstCall.Data.operationId }

if ($status.IsError) {
    Assert-True '查得到操作状态' $false "查询失败：$($status.Text)"
}
else {
    Assert-True '状态是 committed' ($status.Data.state -eq 'committed') `
        "实际状态：$($status.Data.state)"
    Assert-True 'nextStep 说得出接下来该干什么' (-not [string]::IsNullOrEmpty($status.Data.nextStep))
    Write-Host ("  · nextStep：{0}" -f $status.Data.nextStep) -ForegroundColor DarkGray
}

# 查不到的 ID：必须是 unknown + 查证指引，不能说"没有这个操作"
$missingStatus = Invoke-RevitTool $session 'revit_get_operation_status' @{ operationId = 'op_不存在的ID' }

Assert-True '查不到的操作返回 unknown 而非"不存在"' `
    ((-not $missingStatus.IsError) -and $missingStatus.Data.state -eq 'unknown') `
    "实际：IsError=$($missingStatus.IsError) state=$($missingStatus.Data.state)"

if (-not $missingStatus.IsError) {
    Assert-True '并且告诉你怎么去模型里核实' `
        ($missingStatus.Data.nextStep -match 'revit_get_model_changes|revit_query_elements') `
        '查不到时只说"没有"，调用方会以为它没发生过——而模型里可能躺着那次操作的成果。'
}

# ==================== 清理 ====================

if (-not $Keep) {
    Write-Step '清理'

    $ids = @($script:Created | Where-Object { $_ }) + @($script:CreatedViews | Where-Object { $_ })
    if ($ids.Count -gt 0) {
        $cleanup = Invoke-RevitTool $session 'revit_delete_elements' `
            @{ elementIds = $ids; confirm = $true }

        if ($cleanup.IsError) { Write-Host "清理失败：$($cleanup.Text)" -ForegroundColor Yellow }
        else { Write-Host "已删除 $($cleanup.Data.deleted) 个构件（含连带）。" }
    }
}
else {
    Write-Host ""
    Write-Host "已保留本次创建的构件。" -ForegroundColor DarkYellow
}

# ==================== 结论 ====================

Write-Step '结论'

if ($script:Failures.Count -eq 0) {
    Write-Host "全部通过。" -ForegroundColor Green
    exit 0
}

Write-Host "$($script:Failures.Count) 项未通过：" -ForegroundColor Red
foreach ($failure in $script:Failures) { Write-Host "  · $failure" -ForegroundColor Red }
exit 1
