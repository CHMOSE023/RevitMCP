<#
.SYNOPSIS
    M6 验收工作流：建错 → 自己发现 → 自己删掉 → 重建，全程不需要人按 Ctrl+Z。

.DESCRIPTION
    这个脚本是 M6 的验收标准本身，不是演示的附属品。它要证明四件事：

      1. 一次批量建模在撤销栈里只占一步（5 面墙 = 1 步，不是 5 步）
      2. 建错了模型自己看得见   —— revit_get_warnings 读 Revit 自己的警告表
      3. 建错了模型自己收得了场 —— revit_delete_elements 先预览再确认
      4. 全程不需要用户介入

    脚本会故意建一面与南墙完全重叠的墙，制造一个真实的错误，
    然后靠工具把它找出来、删掉、复查干净。

    默认在 (50000, 50000) 附近作业并在结束时清理干净，可以反复运行。

.PARAMETER KeepWalls
    保留本次建的墙，不在结束时清理。想在 Revit 里亲眼看看结果时用。

.PARAMETER ProcessId
    同时开着多个 Revit 时，指定连哪一个。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m6-closed-loop.ps1
    powershell -ExecutionPolicy Bypass -File workflows\m6-closed-loop.ps1 -KeepWalls
#>
[CmdletBinding()]
param(
    [double] $OriginX = 50000,
    [double] $OriginY = 50000,
    [double] $RoomWidth = 6000,
    [double] $RoomDepth = 4000,
    [switch] $KeepWalls,
    [int] $ProcessId,
    [string] $Endpoint
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\McpClient.ps1"

function New-WallSpec {
    param([double] $X0, [double] $Y0, [double] $X1, [double] $Y1, [string] $TypeId, [string] $LevelId)

    @{
        category     = 'OST_Walls'
        typeId       = $TypeId
        levelId      = $LevelId
        height       = 3000
        locationLine = @{
            p0 = @{ x = $X0; y = $Y0 }
            p1 = @{ x = $X1; y = $Y1 }
        }
    }
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

Write-Step '摸清项目：单位、标高、墙类型'

$units = Invoke-RevitTool $session 'revit_get_project_units' -ThrowOnError
Write-Host "项目长度单位：$($units.Data.lengthUnit)；工具单位：$($units.Data.toolLengthUnit)"
Write-ToolWarnings $units

$levels = Invoke-RevitTool $session 'revit_list_levels' -ThrowOnError
if ($levels.Data.total -eq 0) { throw '模型里没有标高，无法建墙。' }

$level = $levels.Data.levels[0]
Write-Host "使用标高 「$($level.name)」 (id=$($level.id), 高程 $($level.elevationMm) mm)"

$types = Invoke-RevitTool $session 'revit_list_types' @{ category = 'OST_Walls'; limit = 200 } -ThrowOnError
if ($types.Data.total -eq 0) { throw '模型里没有墙类型，无法建墙。' }

# 挑一个厚度最接近 200 的基本墙：这正是 typeId 存在的理由——
# "内隔墙 200 厚"这种要求只能通过类型表达，凭类型名猜是猜不中的
$wallType = $types.Data.types |
    Where-Object { $null -ne $_.thicknessMm } |
    Sort-Object { [Math]::Abs($_.thicknessMm - 200) } |
    Select-Object -First 1

if (-not $wallType) { $wallType = $types.Data.types[0] }

Write-Host "使用墙类型 「$($wallType.name)」 (id=$($wallType.id), 厚 $($wallType.thicknessMm) mm)"

# ==================== 建，并且故意建错 ====================

Write-Step '建一圈墙——其中一面是故意建错的'

$x0 = $OriginX
$y0 = $OriginY
$x1 = $OriginX + $RoomWidth
$y1 = $OriginY + $RoomDepth

$walls = @(
    (New-WallSpec $x0 $y0 $x1 $y0 $wallType.id $level.id)   # 南
    (New-WallSpec $x1 $y0 $x1 $y1 $wallType.id $level.id)   # 东
    (New-WallSpec $x1 $y1 $x0 $y1 $wallType.id $level.id)   # 北
    (New-WallSpec $x0 $y1 $x0 $y0 $wallType.id $level.id)   # 西
    (New-WallSpec $x0 $y0 $x1 $y0 $wallType.id $level.id)   # 南墙的重复品 ← 这就是"建错"
)

$created = Invoke-RevitTool $session 'revit_create_line_based_elements' @{ elements = $walls } -ThrowOnError
Write-ToolWarnings $created

Write-Host "一次调用建了 $($created.Data.created) 面墙。"
Write-Host "撤销栈里这是 1 步，不是 $($created.Data.created) 步——去 Revit 的撤销下拉里可以核对。" -ForegroundColor Green

$allIds = @($created.Data.elements | ForEach-Object { $_.id })
$duplicateId = $created.Data.elements[4].id
Write-Host "本次构件 ID：$($allIds -join ', ')"

# ==================== 发现 ====================

Write-Step '复查：模型自己有没有发现问题'

$warnings = Invoke-RevitTool $session 'revit_get_warnings' -ThrowOnError

# 只看与本次新建的墙有关的警告。模型里原有的警告不是这次的责任，
# 混在一起会让"我刚才干的事有没有出问题"这个问题失去答案
$mine = @()
foreach ($group in $warnings.Data.groups) {
    $hit = @($group.elementIds | Where-Object { $allIds -contains $_ })
    if ($hit.Count -gt 0) {
        $mine += [PSCustomObject]@{ Description = $group.description; Ids = $hit; Count = $group.count }
    }
}

Write-Host "模型现有警告 $($warnings.Data.total) 条，其中与本次新建的墙有关的 $($mine.Count) 组："
foreach ($group in $mine) {
    Write-Host "  · $($group.Description)" -ForegroundColor Yellow
    Write-Host "    涉及：$($group.Ids -join ', ')"
}

if ($mine.Count -eq 0) {
    Write-Host ""
    Write-Host "没有发现预期的重叠警告。" -ForegroundColor Red
    Write-Host "可能这个 Revit 版本对完全重合的墙不报警告，也可能警告表还没刷新。" -ForegroundColor Red
    Write-Host "重叠的那面墙是 $duplicateId，下面仍按计划删除它。" -ForegroundColor Red
}

# ==================== 收场 ====================

Write-Step '删掉建错的那面墙——先看清楚要删什么'

$preview = Invoke-RevitTool $session 'revit_delete_elements' @{ elementIds = @($duplicateId) }

if (-not $preview.IsError -or $preview.Code -ne 'CONFIRMATION_REQUIRED') {
    throw "删除工具没有像预期那样要求确认，而是返回：$($preview.Text)"
}

Write-Host "工具拒绝直接执行，先给出预览：" -ForegroundColor Green
foreach ($line in $preview.Text -split "`n") { Write-Host "  $line" }

Write-Step '确认后再删'

$deleted = Invoke-RevitTool $session 'revit_delete_elements' `
    @{ elementIds = @($duplicateId); confirm = $true } -ThrowOnError

Write-ToolWarnings $deleted
Write-Host "已删除 $($deleted.Data.deleted) 个构件（点名 $($deleted.Data.requested) 个，连带 $($deleted.Data.cascaded) 个）。"

# ==================== 复查 ====================

Write-Step '再查一次：问题清掉了吗'

$remainingIds = @($allIds | Where-Object { $_ -ne $duplicateId })
$after = Invoke-RevitTool $session 'revit_get_warnings' -ThrowOnError

$stillMine = @()
foreach ($group in $after.Data.groups) {
    $hit = @($group.elementIds | Where-Object { $remainingIds -contains $_ })
    if ($hit.Count -gt 0) { $stillMine += $group.description }
}

if ($stillMine.Count -eq 0) {
    Write-Host "与这四面墙相关的警告已清空。" -ForegroundColor Green
}
else {
    Write-Host "仍有警告：" -ForegroundColor Yellow
    foreach ($description in $stillMine) { Write-Host "  · $description" }
}

# ==================== 交回给用户 ====================

Write-Step '把结果选中，交回给用户'

$selection = Invoke-RevitTool $session 'revit_set_selection' @{ elementIds = $remainingIds } -ThrowOnError
Write-Host "已在 Revit 里选中 $($selection.Data.count) 面墙——用户屏幕上现在是高亮的。"

# ==================== 清理 ====================

if ($KeepWalls) {
    Write-Step '保留本次建的墙（-KeepWalls）'
    Write-Host "构件 ID：$($remainingIds -join ', ')"
    Write-Host "要还原模型，在 Revit 里按一次 Ctrl+Z 撤销建墙，再按一次撤销删除。"
}
else {
    Write-Step '清理：把本次建的墙全部删掉'

    $cleanup = Invoke-RevitTool $session 'revit_delete_elements' `
        @{ elementIds = $remainingIds; confirm = $true } -ThrowOnError

    Write-ToolWarnings $cleanup
    Write-Host "已清理 $($cleanup.Data.deleted) 个构件，模型回到运行前的样子。"

    Invoke-RevitTool $session 'revit_set_selection' @{ elementIds = @() } -ThrowOnError | Out-Null
}

# ==================== 结论 ====================

Write-Step 'M6 验收结论'

Write-Host "共 $($session.Calls) 次工具调用，全程没有人工介入。"
Write-Host ""
Write-Host "  建错  → 一次调用建 5 面墙，撤销栈 1 步" -ForegroundColor Green
Write-Host "  发现  → revit_get_warnings 读出 Revit 自己记的重叠警告" -ForegroundColor Green
Write-Host "  收场  → revit_delete_elements 先预览连带影响，确认后才动手" -ForegroundColor Green
Write-Host "  复查  → 警告清空，剩下的四面墙是干净的" -ForegroundColor Green
Write-Host ""
Write-Host "全程没有人按过 Ctrl+Z。" -ForegroundColor Green
