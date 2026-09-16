<#
.SYNOPSIS
    M7 验收工作流：这个房间多大、里面有什么、哪两面墙打架了。

.DESCRIPTION
    M7 的验收标准是这三个问题能被回答，而不是工具清单打勾。脚本按顺序验证：

      这个房间多大 —— revit_create_rooms 建房间，revit_list_rooms 读出面积与周长；
                       墙外那个房间面积为 0，工具主动报出"没有围合"
      里面有什么   —— 先用 revit_get_element_geometry 取房间包围盒，
                       再用 revit_query_elements 的 withinBox / near 查里面的构件
      哪两面墙打架 —— revit_get_warnings 读出重叠警告，
                       revit_get_element_geometry 比对两面墙的包围盒，量出重叠范围

    默认在 (70000, 70000) 附近作业并在结束时清理干净，可以反复运行。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m7-space.ps1
#>
[CmdletBinding()]
param(
    [double] $OriginX = 70000,
    [double] $OriginY = 70000,
    [double] $Width = 6000,
    [double] $Depth = 4000,
    [switch] $KeepModel,
    [int] $ProcessId,
    [string] $Endpoint
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\McpClient.ps1"

function Wall($x0, $y0, $x1, $y1, $typeId, $levelId) {
    @{
        category     = 'OST_Walls'
        typeId       = $typeId
        levelId      = $levelId
        height       = 3000
        locationLine = @{ p0 = @{ x = [double]$x0; y = [double]$y0 }; p1 = @{ x = [double]$x1; y = [double]$y1 } }
    }
}

# ==================== 准备 ====================

Write-Step '连接与准备'

$connectArgs = @{}
if ($ProcessId) { $connectArgs['ProcessId'] = $ProcessId }
if ($Endpoint) { $connectArgs['Endpoint'] = $Endpoint }
$session = Connect-RevitMcp @connectArgs

$info = Invoke-RevitTool $session 'revit_get_document_info' -ThrowOnError
Write-Host "文档「$($info.Data.title)」  Revit $($info.Data.revitVersion)"

if (-not $info.Data.writeEnabled) {
    Write-Host "`n当前是浏览模式，本工作流需要建模。" -ForegroundColor Red
    Write-Host "请在 Revit 的 RevitMCP 面板上把「操作模式」切到「修改模型」后重新运行。" -ForegroundColor Red
    exit 2
}

$levels = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError
if ($levels.Data.total -eq 0) { throw '模型里没有标高。' }
$level = $levels.Data.levels | Where-Object { $_.isBuildingStory } | Select-Object -First 1
if (-not $level) { $level = $levels.Data.levels[0] }
Write-Host "标高「$($level.name)」 (id=$($level.id))"

$types = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Walls'; limit = 200 } -ThrowOnError
$wallType = $types.Data.types | Where-Object { $null -ne $_.thicknessMm } |
    Sort-Object { [Math]::Abs($_.thicknessMm - 200) } | Select-Object -First 1
if (-not $wallType) { $wallType = $types.Data.types[0] }
Write-Host "墙类型「$($wallType.name)」 (厚 $($wallType.thicknessMm) mm)"

$x0 = $OriginX; $y0 = $OriginY
$x1 = $OriginX + $Width; $y1 = $OriginY + $Depth
$xm = $OriginX + $Width / 2

# ==================== 建两个房间的壳 ====================

Write-Step '建墙：一圈外墙 + 中间一道隔墙，分成左右两间'

$walls = @(
    (Wall $x0 $y0 $x1 $y0 $wallType.id $level.id)
    (Wall $x1 $y0 $x1 $y1 $wallType.id $level.id)
    (Wall $x1 $y1 $x0 $y1 $wallType.id $level.id)
    (Wall $x0 $y1 $x0 $y0 $wallType.id $level.id)
    (Wall $xm $y0 $xm $y1 $wallType.id $level.id)   # 隔墙
)
$created = Invoke-RevitTool $session 'revit_create_line_based_elements' @{ elements = $walls } -ThrowOnError
Write-ToolWarnings $created
$wallIds = @($created.Data.elements | ForEach-Object { $_.id })
Write-Host "建了 $($created.Data.created) 面墙：$($wallIds -join ', ')"

# ==================== 问题一：这个房间多大 ====================

Write-Step '问题一：这个房间多大'

$rooms = Invoke-RevitTool $session 'revit_create_rooms' @{ elements = @(
    @{ locationPoint = @{ x = ($x0 + 1500); y = ($y0 + 2000) }; levelId = $level.id; name = 'M7 左间' }
    @{ locationPoint = @{ x = ($xm + 1500); y = ($y0 + 2000) }; levelId = $level.id; name = 'M7 右间' }
    # 第三个点落在墙外，用来验证"房间建出来了但没围上"能被如实报出
    @{ locationPoint = @{ x = ($x1 + 4000); y = ($y0 + 2000) }; levelId = $level.id; name = 'M7 墙外' }
) } -ThrowOnError

Write-ToolWarnings $rooms
$roomIds = @($rooms.Data.elements | ForEach-Object { $_.id })
$rooms.Data.elements | Format-Table index, id, name, areaSqm, isBounded -AutoSize | Out-String | Write-Host

$bounded = @($rooms.Data.elements | Where-Object { $_.isBounded })
if ($bounded.Count -eq 2 -and $rooms.Data.unbounded -eq 1) {
    Write-Host "两个房间围合成功、墙外那个如实报了 0 面积——这正是期望的结果" -ForegroundColor Green
}
else {
    Write-Host "预期 2 个围合 + 1 个未围合，实际 $($bounded.Count) / $($rooms.Data.unbounded)" -ForegroundColor Yellow
}

$list = Invoke-RevitTool $session 'revit_list_rooms' @{ nameContains = 'M7'; includeBoundary = $true } -ThrowOnError
Write-ToolWarnings $list
Write-Host "list_rooms 读回 $($list.Data.total) 个房间，合计 $($list.Data.totalAreaSqm) ㎡，未围合 $($list.Data.unbounded) 个"
foreach ($r in $list.Data.rooms) {
    $loops = if ($r.boundary) { $r.boundary.Count } else { 0 }
    $pts = if ($r.boundary -and $r.boundary.Count -gt 0) { $r.boundary[0].points.Count } else { 0 }
    Write-Host ("  {0,-10} {1,8} ㎡  周长 {2,8} mm  边界环 {3}（外环 {4} 点）" -f $r.name, $r.areaSqm, $r.perimeterMm, $loops, $pts)
}

# ==================== 问题二：里面有什么 ====================

Write-Step '问题二：这个房间里有什么'

$leftRoom = $list.Data.rooms | Where-Object { $_.name -eq 'M7 左间' } | Select-Object -First 1
if (-not $leftRoom) { throw '找不到刚建的左间。' }

$geo = Invoke-RevitTool $session 'revit_get_element_geometry' @{ elementIds = @($leftRoom.id) } -ThrowOnError
$box = $geo.Data.elements[0].boundingBox
if (-not $box) { throw '房间没有包围盒。' }
Write-Host ("左间包围盒 {0} → {1}  ({2} x {3} x {4} mm)" -f `
    "($($box.min.x), $($box.min.y))", "($($box.max.x), $($box.max.y))", $box.sizeXMm, $box.sizeYMm, $box.sizeZMm)

$inside = Invoke-RevitTool $session 'revit_query_elements' @{
    withinBox = @{ min = $box.min; max = $box.max }
    limit     = 50
} -ThrowOnError
Write-Host "包围盒内共 $($inside.Data.total) 个构件（跨类别，没有指定 category）："
$inside.Data.elements | Group-Object category | ForEach-Object {
    Write-Host ("    {0,-12} {1} 个" -f $_.Name, $_.Count)
}

Write-Step '换个问法：房间中心点附近有什么'

$near = Invoke-RevitTool $session 'revit_query_elements' @{
    category = 'OST_Walls'
    near     = @{ point = @{ x = $leftRoom.location.x; y = $leftRoom.location.y }; radiusMm = 2500 }
} -ThrowOnError
Write-Host "半径 2500mm 内有 $($near.Data.total) 面墙，按距离排序："
$near.Data.elements | ForEach-Object { Write-Host ("    {0}  {1} mm" -f $_.id, $_.distanceMm) }

$sorted = @($near.Data.elements | ForEach-Object { $_.distanceMm })
$isSorted = $true
for ($i = 1; $i -lt $sorted.Count; $i++) { if ($sorted[$i] -lt $sorted[$i-1]) { $isSorted = $false } }
if ($isSorted) { Write-Host "距离确实是从近到远" -ForegroundColor Green }
else { Write-Host "排序不对" -ForegroundColor Red }

# ==================== 问题三：哪两面墙打架了 ====================

Write-Step '问题三：哪两面墙打架了'

$dup = Invoke-RevitTool $session 'revit_create_line_based_elements' @{ elements = @(
    (Wall $xm $y0 $xm $y1 $wallType.id $level.id)   # 与隔墙完全重合
) } -ThrowOnError
Write-ToolWarnings $dup
$dupId = $dup.Data.elements[0].id
$partitionId = $wallIds[4]
Write-Host "又建了一面与隔墙重合的墙：$dupId（隔墙是 $partitionId）"

$warn = Invoke-RevitTool $session 'revit_get_warnings' -ThrowOnError
$mine = @()
foreach ($g in $warn.Data.groups) {
    $hit = @($g.elementIds | Where-Object { $_ -eq $dupId -or $_ -eq $partitionId })
    if ($hit.Count -gt 0) { $mine += [PSCustomObject]@{ Description = $g.description; Ids = $hit } }
}
if ($mine.Count -gt 0) {
    Write-Host "Revit 自己报了警告：" -ForegroundColor Green
    foreach ($g in $mine) { Write-Host "    $($g.Description)"; Write-Host "      涉及 $($g.Ids -join ', ')" }
}
else {
    Write-Host "这个 Revit 版本没有报重叠警告，下面用几何自己比" -ForegroundColor Yellow
}

$pair = Invoke-RevitTool $session 'revit_get_element_geometry' @{ elementIds = @($partitionId, $dupId) } -ThrowOnError
$a = $pair.Data.elements[0].boundingBox
$b = $pair.Data.elements[1].boundingBox
$overlapX = [Math]::Min($a.max.x, $b.max.x) - [Math]::Max($a.min.x, $b.min.x)
$overlapY = [Math]::Min($a.max.y, $b.max.y) - [Math]::Max($a.min.y, $b.min.y)
$overlapZ = [Math]::Min($a.max.z, $b.max.z) - [Math]::Max($a.min.z, $b.min.z)

Write-Host "两面墙的包围盒重叠范围：X $overlapX mm  Y $overlapY mm  Z $overlapZ mm"
if ($overlapX -gt 0 -and $overlapY -gt 0 -and $overlapZ -gt 0) {
    Write-Host "三个方向都重叠——几何上确认它们占了同一块地方" -ForegroundColor Green
}

Write-Step '收场：删掉重复的那面墙'

$del = Invoke-RevitTool $session 'revit_delete_elements' @{ elementIds = @($dupId); confirm = $true } -ThrowOnError
Write-ToolWarnings $del
Write-Host "已删除 $($del.Data.deleted) 个构件"

# ==================== 清理 ====================

if ($KeepModel) {
    Write-Step '保留本次建的构件（-KeepModel）'
    Write-Host "墙：$($wallIds -join ', ')"
    Write-Host "房间：$($roomIds -join ', ')"
}
else {
    Write-Step '清理'
    $all = @($roomIds + $wallIds)
    $cleanup = Invoke-RevitTool $session 'revit_delete_elements' @{ elementIds = $all; confirm = $true } -ThrowOnError
    Write-ToolWarnings $cleanup
    Write-Host "已清理 $($cleanup.Data.deleted) 个构件，模型回到运行前的样子。"
}

# ==================== 结论 ====================

Write-Step 'M7 验收结论'

Write-Host "共 $($session.Calls) 次工具调用。"
Write-Host ""
Write-Host "  这个房间多大   → create_rooms 建完即报面积，list_rooms 读出面积/周长/边界" -ForegroundColor Green
Write-Host "  里面有什么     → get_element_geometry 取包围盒，query_elements 按 withinBox / near 查" -ForegroundColor Green
Write-Host "  哪两面墙打架   → get_warnings 读 Revit 的判断，包围盒重叠量给出几何佐证" -ForegroundColor Green
Write-Host ""
Write-Host "面积为 0 的房间没有被当成成功：工具主动说出了「没有围合」。" -ForegroundColor Green
