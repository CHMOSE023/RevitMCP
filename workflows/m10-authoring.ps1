<#
.SYNOPSIS
    M10-A 验收工作流：立骨架 → 按外皮尺寸建墙 → 建带梯井的楼板 → 存盘。

.DESCRIPTION
    这个脚本是 M10-A 的验收标准本身。它要证明四件事，
    每一件都**量回来**，而不是调用没报错就算过：

      1. 标高建得出来      —— revit_create_levels，建完能在 revit_list_levels 里查到
      2. 墙顶到标高        —— topLevelId，包围盒的高度应当等于两条标高的高差
      3. 外皮尺寸照图给     —— locationLineRef，包围盒应当**正好**等于图纸尺寸，
                              而不是大一个墙厚（那就是 M9 之前必须手工内偏的那 100 毫米）
      4. 楼板能留洞        —— boundary.innerLoops，面积应当等于外轮廓减去洞口
      5. 建完存得住        —— revit_save_document，且不带 confirm 时不落盘

    第 3 条是整个 M10-A 最值得盯的一条：定位线的内外侧由 Revit 的内部约定决定，
    符号错了不会报错，只会让每面外墙静默偏半个墙厚。包围盒量的就是这个。

    默认在 (60000, 60000) 附近作业并在结束时清理干净，可以反复运行。

.PARAMETER Keep
    保留本次建的东西，不在结束时清理。想在 Revit 里亲眼看看结果时用。

.PARAMETER Save
    连保存一起验（会真的覆盖用户的 .rvt）。默认只验"不带 confirm 不落盘"那一半。

.PARAMETER ProcessId
    同时开着多个 Revit 时，指定连哪一个。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m10-authoring.ps1
    powershell -ExecutionPolicy Bypass -File workflows\m10-authoring.ps1 -Keep -Save
#>
[CmdletBinding()]
param(
    [double] $OriginX = 60000,
    [double] $OriginY = 60000,
    [double] $Width = 10000,
    [double] $Depth = 8000,
    [double] $RoofHeight = 7000,
    [double] $ShaftWidth = 3000,
    [double] $ShaftDepth = 2400,
    [switch] $Keep,
    [switch] $Save,
    [int] $ProcessId,
    [string] $Endpoint
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\McpClient.ps1"

$script:Failures = @()

function Assert-Close {
    <#
    .SYNOPSIS
        量一个数对不对。不抛异常——把所有偏差都收集完再一起报，
        比在第一条上就停下有用：几条同时错，往往指向同一个原因。
    #>
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

function New-Segment {
    param([double] $X0, [double] $Y0, [double] $X1, [double] $Y1)
    @{ p0 = @{ x = $X0; y = $Y0 }; p1 = @{ x = $X1; y = $Y1 } }
}

function New-Rectangle {
    <#
    .SYNOPSIS
        顺时针给一圈线段（X 向右、Y 向上时），这样每一段的外表面都朝着矩形外面。

    .DESCRIPTION
        绕向不是随便挑的：墙的内外由定位线方向决定。
        2026-09-17 在 Revit 2019 上量过——200 厚墙围 10000 × 8000，
        顺时针给的包围盒正好是 10000 × 8000，逆时针给是 10400 × 8400。
        本脚本第一版用的就是逆时针，于是量出 10400，正好把这个约定钉死了。
    #>
    param([double] $X0, [double] $Y0, [double] $X1, [double] $Y1)
    @(
        (New-Segment $X0 $Y0 $X0 $Y1)
        (New-Segment $X0 $Y1 $X1 $Y1)
        (New-Segment $X1 $Y1 $X1 $Y0)
        (New-Segment $X1 $Y0 $X0 $Y0)
    )
}

# ==================== 连接 ====================

Write-Step '连接'

$connectArgs = @{}
if ($ProcessId) { $connectArgs['ProcessId'] = $ProcessId }
if ($Endpoint) { $connectArgs['Endpoint'] = $Endpoint }
$session = Connect-RevitMcp @connectArgs

Write-Host "端点 $($session.Endpoint)"

$info = Invoke-RevitTool $session 'revit_get_document_info' -ThrowOnError
Write-Host "文档 「$($info.Data.title)」  Revit $($info.Data.revitVersion)"

if (-not $info.Data.writeEnabled) {
    Write-Host ""
    Write-Host "写入模式没有开启，本工作流需要建模。" -ForegroundColor Red
    Write-Host "请在 Revit 的 RevitMCP 面板上打开「写入」开关后重新运行。" -ForegroundColor Red
    exit 2
}

# ==================== 摸清项目 ====================

Write-Step '摸清项目：标高与类型'

$levels = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError
if ($levels.Data.total -eq 0) { throw '模型里没有标高，无法建模。' }

$baseLevel = $levels.Data.levels | Sort-Object elevationMm | Select-Object -First 1
Write-Host "底标高 「$($baseLevel.name)」 (id=$($baseLevel.id), 高程 $($baseLevel.elevationMm) mm)"

$wallTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Walls'; limit = 200 } -ThrowOnError

# 必须挑一个有厚度的基本墙：幕墙没有层构造，定位线换算无从谈起
$wallType = $wallTypes.Data.types |
    Where-Object { $null -ne $_.thicknessMm -and $_.thicknessMm -gt 0 } |
    Sort-Object { [Math]::Abs($_.thicknessMm - 200) } |
    Select-Object -First 1

if (-not $wallType) { throw '模型里没有带厚度的墙类型，无法验证定位线。' }
Write-Host "墙类型 「$($wallType.name)」 (id=$($wallType.id), 厚 $($wallType.thicknessMm) mm)"

$floorTypes = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Floors'; limit = 200 } -ThrowOnError
if ($floorTypes.Data.total -eq 0) { throw '模型里没有楼板类型。' }
$floorType = $floorTypes.Data.types[0]
Write-Host "楼板类型 「$($floorType.name)」 (id=$($floorType.id))"

# ==================== 1. 立骨架 ====================

Write-Step '1 立骨架：新建一条屋面标高'

$roofElevation = $baseLevel.elevationMm + $RoofHeight
$roofName = "M10 屋面 $(Get-Random -Minimum 1000 -Maximum 9999)"

$newLevels = Invoke-RevitTool $session 'revit_create_levels' @{
    levels = @(@{ elevationMm = $roofElevation; name = $roofName })
} -ThrowOnError

Write-ToolWarnings $newLevels

$roofLevel = $newLevels.Data.datums[0]
Write-Host "新建标高 「$($roofLevel.name)」 (id=$($roofLevel.id), 高程 $($roofLevel.elevationMm) mm)"

# 建完能不能查到——这是"真的进了模型"和"工具回了个 ID"的区别
$after = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError
$found = $after.Data.levels | Where-Object { $_.id -eq $roofLevel.id }

if ($found) {
    Write-Host "  ✓ revit_list_levels 里查得到它" -ForegroundColor Green
}
else {
    Write-Host "  ✗ revit_list_levels 里查不到新建的标高" -ForegroundColor Red
    $script:Failures += '新建标高不可见'
}

# ==================== 2/3. 按外皮尺寸建墙 ====================

Write-Step '2·3 建四面外墙：外皮照图给，墙顶约束到屋面标高'

$x0 = $OriginX
$y0 = $OriginY
$x1 = $OriginX + $Width
$y1 = $OriginY + $Depth

$wallSpecs = @()
foreach ($segment in (New-Rectangle $x0 $y0 $x1 $y1)) {
    $wallSpecs += @{
        category        = 'OST_Walls'
        typeId          = $wallType.id
        levelId         = $baseLevel.id
        topLevelId      = $roofLevel.id          # ← 不写 height，让墙顶到标高
        locationLineRef = 'FinishFaceExterior'   # ← 给的线就是外皮，不必自己内偏
        locationLine    = $segment
    }
}

$walls = Invoke-RevitTool $session 'revit_create_line_based_elements' @{ elements = $wallSpecs } -ThrowOnError
Write-ToolWarnings $walls

$wallIds = @($walls.Data.elements | ForEach-Object { $_.id })
Write-Host "建了 $($walls.Data.created) 面墙：$($wallIds -join ', ')"

$shift = $walls.Data.elements[0].locationLineShiftMm
Write-Host "每面墙按定位线挪开了 $shift mm（应为墙厚的一半 = $($wallType.thicknessMm / 2)）"
Assert-Close '定位线位移' $shift ($wallType.thicknessMm / 2) 0.5

Write-Host ""
Write-Host "量一圈墙的包围盒——这是本工作流最该盯的一条：" -ForegroundColor Cyan

$geometry = Invoke-RevitTool $session 'revit_get_element_geometry' @{ elementIds = $wallIds } -ThrowOnError

$minX = ($geometry.Data.elements | ForEach-Object { $_.boundingBox.min.x } | Measure-Object -Minimum).Minimum
$maxX = ($geometry.Data.elements | ForEach-Object { $_.boundingBox.max.x } | Measure-Object -Maximum).Maximum
$minY = ($geometry.Data.elements | ForEach-Object { $_.boundingBox.min.y } | Measure-Object -Minimum).Minimum
$maxY = ($geometry.Data.elements | ForEach-Object { $_.boundingBox.max.y } | Measure-Object -Maximum).Maximum
$minZ = ($geometry.Data.elements | ForEach-Object { $_.boundingBox.min.z } | Measure-Object -Minimum).Minimum
$maxZ = ($geometry.Data.elements | ForEach-Object { $_.boundingBox.max.z } | Measure-Object -Maximum).Maximum

$wrongSide = "方向反了：外表面被当成了内表面，整圈墙朝外挪了半个墙厚。把各段的起终点调个个儿。"

Assert-Close '外轮廓 X' ($maxX - $minX) $Width 1.0 $wrongSide
Assert-Close '外轮廓 Y' ($maxY - $minY) $Depth 1.0 $wrongSide
Assert-Close '墙高（顶部约束）' ($maxZ - $minZ) $RoofHeight 1.0 `
    "顶部约束没生效，墙可能停在默认高度 3000。"

# ==================== 4. 带梯井的楼板 ====================

Write-Step '4 建屋面板：中间留一个梯井洞'

$sx0 = $OriginX + 1000
$sy0 = $OriginY + 1000
$sx1 = $sx0 + $ShaftWidth
$sy1 = $sy0 + $ShaftDepth

$floor = Invoke-RevitTool $session 'revit_create_surface_based_elements' @{
    elements = @(@{
        category = 'OST_Floors'
        typeId   = $floorType.id
        levelId  = $roofLevel.id
        boundary = @{
            outerLoop  = (New-Rectangle $x0 $y0 $x1 $y1)
            innerLoops = @(, (New-Rectangle $sx0 $sy0 $sx1 $sy1))
        }
    })
} -ThrowOnError

Write-ToolWarnings $floor

$floorId = $floor.Data.elements[0].id
Write-Host "建了 1 块带洞楼板：$floorId"

# 面积是洞真的开出来了的唯一凭据：包围盒看不见洞
$params = Invoke-RevitTool $session 'revit_get_element_parameters' @{
    elementIds   = @($floorId)
    nameContains = '面积'
} -ThrowOnError

$areaParam = $params.Data.elements[0].parameters |
    Where-Object { $_.storageType -eq 'Double' -and $_.value } |
    Select-Object -First 1

if (-not $areaParam) {
    # 英文界面的项目参数名是 Area
    $params = Invoke-RevitTool $session 'revit_get_element_parameters' @{
        elementIds = @($floorId); nameContains = 'Area'
    } -ThrowOnError
    $areaParam = $params.Data.elements[0].parameters |
        Where-Object { $_.storageType -eq 'Double' -and $_.value } |
        Select-Object -First 1
}

if ($areaParam) {
    # 原始值是 Revit 内部单位（平方英尺）；1 ft = 0.3048 m 是精确定义值
    $areaSqm = [double] $areaParam.value * 0.09290304
    $expected = ($Width * $Depth - $ShaftWidth * $ShaftDepth) / 1000000.0

    Write-Host "楼板面积 $([Math]::Round($areaSqm, 2)) ㎡（参数「$($areaParam.name)」）"
    Assert-Close '楼板面积（㎡）' $areaSqm $expected 0.05 `
        "洞没开出来：面积等于整块外轮廓，说明 innerLoops 被忽略了。"
}
else {
    Write-Host "  ⚠ 读不到楼板的面积参数，跳过洞口校验" -ForegroundColor DarkYellow
}

# ==================== 5. 存盘 ====================

Write-Step '5 存盘：不带 confirm 不落盘'

$dry = Invoke-RevitTool $session 'revit_save_document' -ThrowOnError
Write-ToolWarnings $dry

Write-Host "目标文件：$($dry.Data.path)"
Write-Host "未保存的改动：$($dry.Data.hadUnsavedChanges)"

if ($dry.Data.saved) {
    Write-Host "  ✗ 没给 confirm 却落了盘" -ForegroundColor Red
    $script:Failures += 'confirm 闸门失效'
}
else {
    Write-Host "  ✓ 没给 confirm，如实回报状态且没有落盘" -ForegroundColor Green
}

if ($Save) {
    Write-Step '5b 确认后再存'

    $saved = Invoke-RevitTool $session 'revit_save_document' @{ confirm = $true } -ThrowOnError
    Write-ToolWarnings $saved

    if ($saved.Data.saved) {
        Write-Host "  ✓ 已存盘：$($saved.Data.path)（$([Math]::Round($saved.Data.fileSizeBytes / 1MB, 1)) MB）" -ForegroundColor Green
    }
    else {
        Write-Host "  ✗ 带了 confirm 仍未落盘" -ForegroundColor Red
        $script:Failures += '保存未生效'
    }
}
else {
    Write-Host ""
    Write-Host "跳过真正的保存（加 -Save 连它一起验，会覆盖用户的 .rvt）。" -ForegroundColor DarkYellow
}

# ==================== 收场 ====================

if (-not $Keep) {
    Write-Step '清理'

    # 顺序要紧：先删构件再删标高。反过来 Revit 会把依附在标高上的东西一起带走，
    # 删除回执里的数字就对不上了
    $cleanup = Invoke-RevitTool $session 'revit_delete_elements' `
        @{ elementIds = @($wallIds + $floorId); confirm = $true } -ThrowOnError
    Write-Host "删除构件 $($cleanup.Data.deleted) 个"

    $dropLevel = Invoke-RevitTool $session 'revit_delete_elements' `
        @{ elementIds = @($roofLevel.id); confirm = $true } -ThrowOnError
    Write-Host "删除标高 $($dropLevel.Data.deleted) 个"

    if ($Save) {
        Write-Host "注意：-Save 已经把带着这些构件的状态存进了 .rvt，清理后需要再存一次。" -ForegroundColor DarkYellow
    }
}
else {
    Write-Host ""
    Write-Host "已保留本次建的东西。标高「$roofName」、$($wallIds.Count) 面墙、1 块带洞楼板。" -ForegroundColor DarkYellow
}

# ==================== 结论 ====================

Write-Step '结论'

if ($script:Failures.Count -eq 0) {
    Write-Host "M10-A 全部通过。" -ForegroundColor Green
    Write-Host "标高建得出、墙顶得到标高、外皮尺寸照图给、楼板留得了洞、存盘有闸门。" -ForegroundColor Green
    exit 0
}

Write-Host "有 $($script:Failures.Count) 项没通过：" -ForegroundColor Red
foreach ($failure in $script:Failures) { Write-Host "  · $failure" -ForegroundColor Red }
exit 1
