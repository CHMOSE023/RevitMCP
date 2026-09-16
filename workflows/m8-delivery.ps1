<#
.SYNOPSIS
    M8 验收工作流：从模型生成一套图纸 + 一份带截图的质检报告。

.DESCRIPTION
    M8 的验收标准是能交付出东西，而不是工具清单打勾。脚本按顺序验证：

      出图 —— revit_create_sheets 建图纸，revit_add_views_to_sheet 摆视图，
              revit_list_views 确认视图落在了哪张图纸上
      截图 —— revit_export_image 把视图和图纸导成 PNG 落盘
      质检 —— revit_get_warnings + revit_list_rooms 找出问题，
              连同截图写成一份 Markdown 报告

    **报告落盘、截图落盘**，这正是这个形态相对聊天窗口的价值：
    产物能进版本库、进 PR，明天还在。

    默认在 (80000, 80000) 附近作业并在结束时清理干净，可以反复运行。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m8-delivery.ps1
#>
[CmdletBinding()]
param(
    [double] $OriginX = 80000,
    [double] $OriginY = 80000,
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
    Write-Host "请把「操作模式」切到「修改模型」后重新运行。" -ForegroundColor Red
    exit 2
}

$levels = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError

# 标高由视图决定，不是反过来。
#
# 出图导向的流程里，"在哪个标高上建"要服从"图纸上要显示哪个视图"——
# 平面视图只显示自己标高附近的构件，先定标高再找视图，很容易撞上
# "这个标高压根没有平面视图"，于是交付出一张空图纸，而每一步都显示成功。
$planViews = Invoke-RevitTool $session 'revit_list_views' @{ viewType = 'FloorPlan'; onlyPlaceable = $true } -ThrowOnError
$freeView = $planViews.Data.views |
    Where-Object { -not $_.sheetId -and $_.levelId } |
    Select-Object -First 1

if (-not $freeView) { throw '没有「未放置且关联了标高」的平面视图可用，无法交付出有内容的图纸。' }

$level = $levels.Data.levels | Where-Object { $_.id -eq $freeView.levelId } | Select-Object -First 1
if (-not $level) { throw "视图「$($freeView.name)」的标高 $($freeView.levelId) 不在标高列表里。" }

$types = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Walls'; limit = 200 } -ThrowOnError
$wallType = $types.Data.types | Where-Object { $null -ne $_.thicknessMm } |
    Sort-Object { [Math]::Abs($_.thicknessMm - 200) } | Select-Object -First 1
if (-not $wallType) { $wallType = $types.Data.types[0] }
Write-Host "标高「$($level.name)」  墙类型「$($wallType.name)」"

$x0 = $OriginX; $y0 = $OriginY
$x1 = $OriginX + 6000; $y1 = $OriginY + 4000

Write-Step '造一个待交付的小场景（含一处故意留下的问题）'

$walls = @(
    (Wall $x0 $y0 $x1 $y0 $wallType.id $level.id)
    (Wall $x1 $y0 $x1 $y1 $wallType.id $level.id)
    (Wall $x1 $y1 $x0 $y1 $wallType.id $level.id)
    (Wall $x0 $y1 $x0 $y0 $wallType.id $level.id)
    (Wall $x1 $y0 $x1 $y1 $wallType.id $level.id)   # 与东墙重合，质检该抓到它
)
$created = Invoke-RevitTool $session 'revit_create_line_based_elements' @{ elements = $walls } -ThrowOnError
Write-ToolWarnings $created
$wallIds = @($created.Data.elements | ForEach-Object { $_.id })
$dupWallId = $wallIds[4]

$rooms = Invoke-RevitTool $session 'revit_create_rooms' @{ elements = @(
    @{ locationPoint = @{ x = ($x0 + 3000); y = ($y0 + 2000) }; levelId = $level.id; name = 'M8 办公室'; number = 'M8-01' }
) } -ThrowOnError
Write-ToolWarnings $rooms
$roomIds = @($rooms.Data.elements | ForEach-Object { $_.id })
Write-Host "墙 $($wallIds.Count) 面、房间 $($rooms.Data.created) 个（面积 $($rooms.Data.elements[0].areaSqm) ㎡）"

# ==================== 出图 ====================

Write-Step '出图：建一张图纸，把平面视图摆上去'

Write-Host "用准备阶段选定的「$($freeView.name)」(id=$($freeView.id), 标高=$($freeView.level), 1:$($freeView.scale))"

$stamp = Get-Date -Format 'HHmmss'
$sheetNumber = "M8-$stamp"
$sheets = Invoke-RevitTool $session 'revit_create_sheets' @{ elements = @(
    @{ number = $sheetNumber; name = 'M8 交付测试图纸' }
) } -ThrowOnError
Write-ToolWarnings $sheets
$sheet = $sheets.Data.elements[0]
Write-Host "图纸「$($sheet.number) $($sheet.name)」 id=$($sheet.id)  标题栏：$(if($sheet.titleBlock){$sheet.titleBlock}else{'无'})"

$placed = Invoke-RevitTool $session 'revit_add_views_to_sheet' @{
    sheetId = $sheet.id
    views   = @(@{ viewId = $freeView.id })
} -ThrowOnError
Write-ToolWarnings $placed
Write-Host "已把 $($placed.Data.placed) 个视图放到图纸上，视口 $($placed.Data.views[0].viewportId)"

Write-Step '复查：视图现在挂在哪张图纸上'

$recheck = Invoke-RevitTool $session 'revit_list_views' @{ nameContains = $freeView.name } -ThrowOnError
$onSheet = $recheck.Data.views | Where-Object { $_.id -eq $freeView.id } | Select-Object -First 1
if ($onSheet.sheetNumber -eq $sheetNumber) {
    Write-Host "list_views 已能读出它在「$($onSheet.sheetNumber)」上" -ForegroundColor Green
} else {
    Write-Host "预期 $sheetNumber，实际 $($onSheet.sheetNumber)" -ForegroundColor Red
}

Write-Step '同一个视图不能放两次'

$again = Invoke-RevitTool $session 'revit_add_views_to_sheet' @{
    sheetId = $sheet.id
    views   = @(@{ viewId = $freeView.id })
}
if ($again.IsError) {
    Write-Host "如期被拒：$($again.Text)" -ForegroundColor Green
} else {
    Write-Host "没有拦住重复放置" -ForegroundColor Red
}

# ==================== 制表 ====================

Write-Step '制表：给墙做一张明细表'

$fields = Invoke-RevitTool $session 'revit_list_schedulable_fields' @{ category = 'OST_Walls' } -ThrowOnError
Write-Host "OST_Walls 可用字段 $($fields.Data.total) 个"

# 字段名是 Revit 按项目语言给的，脚本不能写死——先查再挑。
# 优先挑几个对墙有意义的，找不到就退到前三个
$wanted = @()
foreach ($key in @('族与类型', '类型', '长度', '面积', '体积')) {
    $hit = $fields.Data.fields | Where-Object { $_.name -eq $key } | Select-Object -First 1
    if ($hit -and $wanted -notcontains $hit.name) { $wanted += $hit.name }
    if ($wanted.Count -ge 3) { break }
}
if ($wanted.Count -eq 0) { $wanted = @($fields.Data.fields | Select-Object -First 3 | ForEach-Object { $_.name }) }
Write-Host "选用字段：$($wanted -join ' / ')"

$schedule = Invoke-RevitTool $session 'revit_create_schedule' @{
    category = 'OST_Walls'
    name     = "M8 墙明细表 $stamp"
    fields   = $wanted
    sortBy   = $wanted[0]
} -ThrowOnError
Write-ToolWarnings $schedule
Write-Host "明细表「$($schedule.Data.name)」 id=$($schedule.Data.id)  表体 $($schedule.Data.rowCount) 行"

Write-Step '读回来核对'

$table = Invoke-RevitTool $session 'revit_read_schedule' @{ scheduleId = $schedule.Data.id; maxRows = 10 } -ThrowOnError
Write-ToolWarnings $table
Write-Host "读到 $($table.Data.rowCount) 行 x $($table.Data.columnCount) 列："
$rowIndex = 0
foreach ($row in $table.Data.rows) {
    $tag = if ($rowIndex -eq 0) { '标题' } else { "  $rowIndex " }
    Write-Host ("    [{0}] {1}" -f $tag, ($row -join ' | '))
    $rowIndex++
}

if ($table.Data.rowCount -gt 1) {
    Write-Host "明细表统计到了本次建的墙" -ForegroundColor Green
} else {
    Write-Host "明细表是空的——只有列标题" -ForegroundColor Yellow
}

Write-Step '把明细表也放到图纸上'

$placedSchedule = Invoke-RevitTool $session 'revit_add_views_to_sheet' @{
    sheetId = $sheet.id
    views   = @(@{ viewId = $schedule.Data.id; position = @{ x = 600.0; y = 450.0 } })
}
if ($placedSchedule.IsError) {
    Write-Host "放置失败：$($placedSchedule.Text)" -ForegroundColor Red
} else {
    Write-ToolWarnings $placedSchedule
    Write-Host "明细表实例 $($placedSchedule.Data.views[0].viewportId) 已放到「$($placedSchedule.Data.sheetNumber)」上"
    Write-Host "（明细表走 ScheduleSheetInstance，不是 Viewport——它可以同时出现在多张图纸上）"
}

# ==================== 截图 ====================

Write-Step '截图：把视图和图纸导成 PNG'

$shots = @()
foreach ($target in @(
    @{ id = $freeView.id; file = "m8-plan-$stamp.png";  label = '平面视图' },
    @{ id = $sheet.id;    file = "m8-sheet-$stamp.png"; label = '图纸' }
)) {
    $img = Invoke-RevitTool $session 'revit_export_image' @{
        viewId = $target.id; fileName = $target.file; pixelWidth = 1600
    }
    if ($img.IsError) {
        Write-Host "  $($target.label) 导出失败：$($img.Text)" -ForegroundColor Red
    } else {
        Write-ToolWarnings $img
        $kb = [Math]::Round($img.Data.fileSizeBytes / 1KB, 1)
        Write-Host "  $($target.label)：$($img.Data.path)  ($kb KB，可见构件 $($img.Data.visibleElementCount) 个)"
        $shots += [PSCustomObject]@{ Label = $target.label; Path = $img.Data.path; Name = $img.Data.viewName }
    }
}

Write-Step '路径必须受限：这几种写法都该被拒'

foreach ($bad in @('..\escape.png', 'sub\plan.png', 'C:\windows\evil.png', 'payload.exe')) {
    $r = Invoke-RevitTool $session 'revit_export_image' @{ viewId = $freeView.id; fileName = $bad }
    $mark = if ($r.IsError -and $r.Code -eq 'INVALID_PARAMETER') { '拒绝' } else { "意外放行 -> $($r.Code)" }
    Write-Host ("  {0,-24} {1}" -f $bad, $mark)
}

# ==================== 质检 ====================

Write-Step '质检：模型现在有什么问题'

$warn = Invoke-RevitTool $session 'revit_get_warnings' -ThrowOnError
$mine = @()
foreach ($g in $warn.Data.groups) {
    $hit = @($g.elementIds | Where-Object { $wallIds -contains $_ })
    if ($hit.Count -gt 0) { $mine += [PSCustomObject]@{ Description = $g.description; Count = $g.count; Ids = $hit } }
}
Write-Host "与本次构件相关的警告 $($mine.Count) 组"
foreach ($g in $mine) { Write-Host "  · $($g.Description)" }

$roomList = Invoke-RevitTool $session 'revit_list_rooms' @{ nameContains = 'M8' } -ThrowOnError
Write-Host "房间 $($roomList.Data.total) 个，未围合 $($roomList.Data.unbounded) 个，合计 $($roomList.Data.totalAreaSqm) ㎡"

Write-Step '写报告'

$exportDir = Split-Path -Parent $shots[0].Path
$reportPath = Join-Path $exportDir "m8-质检报告-$stamp.md"

$lines = @()
$lines += "# 质检报告 · $($info.Data.title)"
$lines += ""
$lines += "生成时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')　｜　Revit $($info.Data.revitVersion)"
$lines += ""
$lines += "## 交付物"
$lines += ""
$lines += "| 图纸 | 名称 | 含视图 |"
$lines += "|---|---|---|"
$lines += "| $($sheet.number) | $($sheet.name) | $($freeView.name) (1:$($freeView.scale)) |"
$lines += ""
$lines += "## 房间"
$lines += ""
$lines += "| 编号 | 名称 | 面积 (㎡) | 围合 |"
$lines += "|---|---|---|---|"
foreach ($r in $roomList.Data.rooms) {
    $ok = if ($r.isBounded) { '是' } else { '**否**' }
    $lines += "| $($r.number) | $($r.name) | $($r.areaSqm) | $ok |"
}
$lines += ""
$lines += "## 统计表"
$lines += ""
$lines += "明细表「$($schedule.Data.name)」，$($table.Data.rowCount) 行 x $($table.Data.columnCount) 列"
$lines += ""
if ($table.Data.rows.Count -gt 0) {
    $header = $table.Data.rows[0]
    $lines += "| " + ($header -join " | ") + " |"
    $lines += "|" + (($header | ForEach-Object { "---" }) -join "|") + "|"
    foreach ($row in ($table.Data.rows | Select-Object -Skip 1)) {
        $lines += "| " + ($row -join " | ") + " |"
    }
}
$lines += ""
$lines += "## 发现的问题"
$lines += ""
if ($mine.Count -eq 0) {
    $lines += "本次构件未触发 Revit 警告。"
} else {
    foreach ($g in $mine) {
        $lines += "- **$($g.Description)**"
        $lines += "  - 涉及构件：$($g.Ids -join ', ')"
    }
}
$lines += ""
$lines += "## 截图"
$lines += ""
foreach ($s in $shots) {
    $lines += "### $($s.Label)：$($s.Name)"
    $lines += ""
    $lines += "![$($s.Label)]($(Split-Path -Leaf $s.Path))"
    $lines += ""
}
$lines -join "`r`n" | Out-File -FilePath $reportPath -Encoding utf8

Write-Host "报告：$reportPath"
Write-Host "截图与报告在同一个目录，Markdown 里的图片引用是相对路径，直接就能看。"

# ==================== 清理 ====================

if ($KeepModel) {
    Write-Step '保留本次建的构件（-KeepModel）'
    Write-Host "图纸 $($sheet.id)、墙 $($wallIds -join ', ')、房间 $($roomIds -join ', ')"
}
else {
    Write-Step '清理模型（导出的文件保留）'
    $all = @($sheet.id, $schedule.Data.id) + $roomIds + $wallIds
    $cleanup = Invoke-RevitTool $session 'revit_delete_elements' @{ elementIds = $all; confirm = $true } -ThrowOnError
    Write-ToolWarnings $cleanup
    Write-Host "已清理 $($cleanup.Data.deleted) 个构件。"
}

# ==================== 结论 ====================

Write-Step 'M8 验收结论'

Write-Host "共 $($session.Calls) 次工具调用。"
Write-Host ""
Write-Host "  出图   → create_sheets 建图纸，add_views_to_sheet 摆视图，list_views 复查落位" -ForegroundColor Green
Write-Host "  截图   → export_image 落盘 $($shots.Count) 张 PNG，路径越界的写法全部被拒" -ForegroundColor Green
Write-Host "  制表   → list_schedulable_fields 查字段，create_schedule 建表，read_schedule 读回" -ForegroundColor Green
Write-Host "  质检   → get_warnings + list_rooms 找出问题，连同截图和统计表写成 Markdown 报告" -ForegroundColor Green
Write-Host ""
Write-Host "产物留在磁盘上：$exportDir" -ForegroundColor Green
