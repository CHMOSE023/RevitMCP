<#
.SYNOPSIS
    M9：把一份企业标准跑在当前模型上，产出合规报告。

.DESCRIPTION
    规则是数据（JSON），检查器是代码（standards/Audit.ps1），报告是产物（Markdown）。
    三者分开，规则才能进版本库、按项目分支、改动走 PR。

    **全程只读**——审计不该碰模型。写入模式关着也能跑，这是刻意的：
    质检的人未必有改模型的权限，也不该有。

.PARAMETER Standard
    规则文件路径，默认 standards/example-standard.json

.PARAMETER Report
    报告输出路径，默认写到导出目录旁边

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m9-audit.ps1
    powershell -ExecutionPolicy Bypass -File workflows\m9-audit.ps1 -Standard .\my-standard.json
#>
[CmdletBinding()]
param(
    [string] $Standard,
    [string] $Report,
    [string] $DocumentId,
    [int] $ProcessId,
    [string] $Endpoint
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\McpClient.ps1"
. "$PSScriptRoot\standards\Audit.ps1"

if (-not $Standard) { $Standard = Join-Path $PSScriptRoot 'standards\example-standard.json' }
if (-not (Test-Path $Standard)) { throw "找不到标准文件：$Standard" }

$standardDoc = Get-Content $Standard -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Step '连接'

$connectArgs = @{}
if ($ProcessId) { $connectArgs['ProcessId'] = $ProcessId }
if ($Endpoint) { $connectArgs['Endpoint'] = $Endpoint }
$session = Connect-RevitMcp @connectArgs

$info = Invoke-RevitTool $session 'revit_get_document_info' -ThrowOnError
$docs = Invoke-RevitTool $session 'revit_list_documents' -ThrowOnError

$targetTitle = $info.Data.title
if ($DocumentId) {
    $picked = $docs.Data.documents | Where-Object { $_.id -eq $DocumentId } | Select-Object -First 1
    if (-not $picked) { throw "没有打开 ID 为「$DocumentId」的文档。当前打开：$(($docs.Data.documents | ForEach-Object { $_.id }) -join '、')" }
    $targetTitle = $picked.title
}

Write-Host "文档「$targetTitle」  Revit $($info.Data.revitVersion)"
if ($docs.Data.total -gt 1 -and -not $DocumentId) {
    Write-Host "（这个 Revit 里开着 $($docs.Data.total) 个文档，本次只查活动的那个。要全查用 m9-batch.ps1）" -ForegroundColor DarkYellow
}

# 族文档和项目文档是两套 API 语义，建模标准大多不适用。
# m9-batch 直接跳过它们，这里也得说一声——否则会得出
# "族文档只有一个参照标高，所以标高数不合格"这种没有意义的结论
$targetDoc = if ($DocumentId) { $picked } else { $docs.Data.documents | Where-Object { $_.isActive } | Select-Object -First 1 }
if ($targetDoc -and $targetDoc.isFamilyDocument) {
    Write-Host ""
    Write-Host "「$targetTitle」是族文档，建模标准大多不适用——下面的结论仅供参考。" -ForegroundColor Yellow
    Write-Host "要审计项目文档，用 -DocumentId 指定，或在 Revit 里切到项目再跑。" -ForegroundColor Yellow
    $isFamily = $true
}
Write-Host "标准「$($standardDoc.name)」 v$($standardDoc.version)  共 $($standardDoc.rules.Count) 条规则"

# 审计是只读的，写入开着反而是个值得提的风险
if ($info.Data.writeEnabled) {
    Write-Host "（注意：当前是「修改模型」模式。审计本身只读，但这时候模型可能被别的会话改动）" -ForegroundColor DarkYellow
}

Write-Step '逐条检查'

$results = Invoke-StandardCheck -Session $session -Rules $standardDoc.rules -DocumentId $DocumentId

foreach ($r in $results) {
    $color = switch ($r.Status) {
        'pass'  { 'Green' }
        'fail'  { if ($r.Severity -eq 'error') { 'Red' } else { 'Yellow' } }
        'error' { 'Magenta' }
        default { 'DarkGray' }
    }
    Write-Host (Format-AuditLine $r) -ForegroundColor $color

    # 违规要点名到构件，否则这份报告没法据以动手
    foreach ($o in ($r.Offenders | Select-Object -First 5)) {
        Write-Host "         · $o" -ForegroundColor DarkGray
    }
    if ($r.Offenders.Count -gt 5) {
        Write-Host "         · …… 另有 $($r.Offenders.Count - 5) 项，详见报告" -ForegroundColor DarkGray
    }
}

Write-Step '结论'

$summary = Get-AuditSummary -Results $results

Write-Host "规则 $($summary.Total) 条：通过 $($summary.Passed)，不合格 $($summary.Failed)，待改进 $($summary.Warned)，跳过 $($summary.Skipped)，查不成 $($summary.Broken)"
if ($summary.Compliant) {
    Write-Host "结论：合格" -ForegroundColor Green
} else {
    Write-Host "结论：不合格" -ForegroundColor Red
}

# ==================== 报告 ====================

if (-not $Report) {
    $dir = Join-Path $env:LOCALAPPDATA 'RevitMCP\exports'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $safeTitle = ($targetTitle -replace '[^\w\-]', '_')
    $Report = Join-Path $dir "审计-$safeTitle-$(Get-Date -Format 'yyyyMMdd-HHmmss').md"
}

$lines = @()
$lines += "# 建模标准审计报告"
$lines += ""
$lines += "| | |"
$lines += "|---|---|"
$lines += "| 模型 | $targetTitle$(if($isFamily){'（族文档）'}else{''}) |"
$lines += "| 路径 | $(if($info.Data.pathName){$info.Data.pathName}else{'（未保存）'}) |"
$lines += "| 标准 | $($standardDoc.name) v$($standardDoc.version) |"
$lines += "| 时间 | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') |"
$lines += "| 结论 | $(if($summary.Compliant){'**合格**'}else{'**不合格**'}) |"
$lines += ""
$lines += "通过 $($summary.Passed) ｜ 不合格 $($summary.Failed) ｜ 待改进 $($summary.Warned) ｜ 跳过 $($summary.Skipped) ｜ 查不成 $($summary.Broken)"
if ($isFamily) {
    $lines += ""
    $lines += "> **这是族文档**，建模标准大多针对项目文档，以下结论仅供参考。"
}
$lines += ""
$lines += "## 逐条结果"
$lines += ""
$lines += "| 规则 | 级别 | 结果 | 说明 |"
$lines += "|---|---|---|---|"
foreach ($r in $results) {
    $status = switch ($r.Status) {
        'pass'  { '通过' }
        'fail'  { if ($r.Severity -eq 'error') { '**不合格**' } else { '待改进' } }
        'skip'  { '跳过' }
        'error' { '查不成' }
    }
    $lines += "| $($r.Title) | $($r.Severity) | $status | $($r.Detail) |"
}

$withOffenders = @($results | Where-Object { $_.Offenders.Count -gt 0 })
if ($withOffenders.Count -gt 0) {
    $lines += ""
    $lines += "## 违规明细"
    $lines += ""
    foreach ($r in $withOffenders) {
        $lines += "### $($r.Title)"
        $lines += ""
        $lines += $r.Detail
        $lines += ""
        foreach ($o in $r.Offenders) { $lines += "- ``$o``" }
        $lines += ""
    }
}

$lines += ""
$lines += "---"
$lines += ""
$lines += "规则定义：``$Standard``"

$lines -join "`r`n" | Out-File -FilePath $Report -Encoding utf8
Write-Host ""
Write-Host "报告：$Report"

# 让 CI 和批处理能按退出码判断
if (-not $summary.Compliant) { exit 1 }
