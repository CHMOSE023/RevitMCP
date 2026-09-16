<#
.SYNOPSIS
    M9：把同一份标准跑在本机所有打开的模型上，产出一份汇总。

.DESCRIPTION
    一个 Revit 实例可以同时开着十几个项目。批处理把它们逐个查一遍，
    靠的是各只读工具的 documentId 参数——**不切换活动文档**，
    那会打断用户正在看的东西，而查询本不该有这种副作用。

    本机开着多个 Revit 时，每个实例里的文档也都会被覆盖到。

.PARAMETER Standard
    规则文件路径，默认 standards/example-standard.json

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File workflows\m9-batch.ps1
#>
[CmdletBinding()]
param(
    [string] $Standard,
    [string] $Report
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\McpClient.ps1"
. "$PSScriptRoot\standards\Audit.ps1"

if (-not $Standard) { $Standard = Join-Path $PSScriptRoot 'standards\example-standard.json' }
if (-not (Test-Path $Standard)) { throw "找不到标准文件：$Standard" }

$standardDoc = Get-Content $Standard -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Step '发现实例'

# 强制成数组：PowerShell 会把"只有一个元素"的返回值解包成单个对象，
# 那时 .Count 是空的，"发现 个实例"这种话就是这么来的
$instances = @(Get-RevitInstances)
if ($instances.Count -eq 0) {
    Write-Host "本机没有正在运行的 RevitMCP 实例。" -ForegroundColor Red
    Write-Host "请至少启动一个 Revit 并打开模型。"
    exit 2
}

Write-Host "发现 $($instances.Count) 个实例："
foreach ($i in $instances) {
    Write-Host ("  pid={0,-6} 端口={1}  Revit {2}  文档「{3}」  写入={4}" -f `
        $i.Pid, $i.Port, $i.RevitVersion, $i.ActiveDocument, $i.WriteEnabled)
}

Write-Host ""
Write-Host "标准「$($standardDoc.name)」 v$($standardDoc.version)  $($standardDoc.rules.Count) 条规则"

# ==================== 逐个审计 ====================

$audits = @()

foreach ($instance in $instances) {
    # 每个实例单独连：一个连不上不该把整轮批处理带走，
    # 那正是批处理最该扛住的一类故障
    try {
        $session = Connect-RevitMcp -Endpoint $instance.Endpoint
        $docs = Invoke-RevitTool $session 'revit_list_documents' -ThrowOnError
    }
    catch {
        Write-Step "pid=$($instance.Pid)"
        Write-Host "  连接失败：$($_.Exception.Message)" -ForegroundColor Red
        $audits += [PSCustomObject]@{
            Pid = $instance.Pid; Document = "(pid $($instance.Pid))"; Revit = $instance.RevitVersion
            Reached = $false; Skipped = $false; Summary = $null; Results = @(); Error = $_.Exception.Message
        }
        continue
    }

    Write-Host ""
    Write-Host "pid=$($instance.Pid) 打开了 $($docs.Data.total) 个文档" -ForegroundColor Cyan

    foreach ($doc in $docs.Data.documents) {
        Write-Step "审计「$($doc.title)」$(if($doc.isActive){' [活动]'}else{''})"

        $record = [PSCustomObject]@{
            Pid      = $instance.Pid
            Document = $doc.title
            Revit    = $instance.RevitVersion
            Reached  = $false
            Skipped  = $false
            Summary  = $null
            Results  = @()
            Error    = $null
        }

        # 族文档和项目文档是两套 API 语义，规则大多不适用。
        # 这是**主动跳过**，不是失败——不该让整批审计判定为不合格
        if ($doc.isFamilyDocument) {
            $record.Skipped = $true
            $record.Error = '族文档，已跳过'
            Write-Host "  族文档，跳过" -ForegroundColor DarkGray
            $audits += $record
            continue
        }

        try {
            $record.Reached = $true
            $record.Results = Invoke-StandardCheck -Session $session -Rules $standardDoc.rules -DocumentId $doc.id
            $record.Summary = Get-AuditSummary -Results $record.Results

            foreach ($r in $record.Results) {
                $color = switch ($r.Status) {
                    'pass'  { 'DarkGray' }
                    'fail'  { if ($r.Severity -eq 'error') { 'Red' } else { 'Yellow' } }
                    'error' { 'Magenta' }
                    default { 'DarkGray' }
                }
                Write-Host (Format-AuditLine $r) -ForegroundColor $color
            }

            $verdict = if ($record.Summary.Compliant) { '合格' } else { '不合格' }
            $verdictColor = if ($record.Summary.Compliant) { 'Green' } else { 'Red' }
            Write-Host "  → $verdict（通过 $($record.Summary.Passed)，不合格 $($record.Summary.Failed)，待改进 $($record.Summary.Warned)）" -ForegroundColor $verdictColor
        }
        catch {
            $record.Reached = $false
            $record.Error = $_.Exception.Message
            Write-Host "  审计失败：$($record.Error)" -ForegroundColor Red
        }

        $audits += $record
    }
}

# ==================== 汇总 ====================

Write-Step '汇总'

$reached = @($audits | Where-Object { $_.Reached })
$compliant = @($reached | Where-Object { $_.Summary.Compliant })
$skipped = @($audits | Where-Object { $_.Skipped })
# 真正的失败：既没查成、也不是主动跳过的
$broken = @($audits | Where-Object { -not $_.Reached -and -not $_.Skipped })

Write-Host ("文档 {0} 个：查了 {1}，合格 {2}，不合格 {3}，跳过 {4}，查不成 {5}" -f `
    $audits.Count, $reached.Count, $compliant.Count, ($reached.Count - $compliant.Count), $skipped.Count, $broken.Count)

# 哪条规则最常被违反——这是批处理相对单次审计真正多出来的信息
$ruleStats = @{}
foreach ($a in $reached) {
    foreach ($r in $a.Results) {
        if (-not $ruleStats.ContainsKey($r.Title)) {
            $ruleStats[$r.Title] = [PSCustomObject]@{ Title = $r.Title; Failed = 0; Total = 0 }
        }
        $ruleStats[$r.Title].Total++
        if ($r.Status -eq 'fail') { $ruleStats[$r.Title].Failed++ }
    }
}

$hotspots = @($ruleStats.Values | Where-Object { $_.Failed -gt 0 } | Sort-Object -Property Failed -Descending)
if ($hotspots.Count -gt 0) {
    Write-Host ""
    Write-Host "最常被违反的规则："
    foreach ($h in ($hotspots | Select-Object -First 5)) {
        Write-Host ("  {0,-28} {1}/{2} 个模型未通过" -f $h.Title, $h.Failed, $h.Total)
    }
}

# ==================== 报告 ====================

if (-not $Report) {
    $dir = Join-Path $env:LOCALAPPDATA 'RevitMCP\exports'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $Report = Join-Path $dir "批量审计-$(Get-Date -Format 'yyyyMMdd-HHmmss').md"
}

$lines = @()
$lines += "# 批量审计汇总"
$lines += ""
$lines += "标准：$($standardDoc.name) v$($standardDoc.version)　｜　时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += ""
$lines += "文档 $($audits.Count) 个：合格 $($compliant.Count)，不合格 $($reached.Count - $compliant.Count)，跳过 $($skipped.Count)，查不成 $($broken.Count)"
$lines += ""
$lines += "## 各文档结果"
$lines += ""
$lines += "| 文档 | Revit | 结论 | 通过 | 不合格 | 待改进 |"
$lines += "|---|---|---|---|---|---|"
foreach ($a in $audits) {
    if (-not $a.Reached) {
        $why = if ($a.Skipped) { $a.Error } elseif ($a.Error) { "查不成：$($a.Error)" } else { '没连上' }
        $lines += "| $($a.Document) | $($a.Revit) | $why | — | — | — |"
        continue
    }
    $verdict = if ($a.Summary.Compliant) { '合格' } else { '**不合格**' }
    $lines += "| $($a.Document) | $($a.Revit) | $verdict | $($a.Summary.Passed) | $($a.Summary.Failed) | $($a.Summary.Warned) |"
}

if ($hotspots.Count -gt 0) {
    $lines += ""
    $lines += "## 最常被违反的规则"
    $lines += ""
    $lines += "| 规则 | 未通过 / 总数 |"
    $lines += "|---|---|"
    foreach ($h in $hotspots) { $lines += "| $($h.Title) | $($h.Failed) / $($h.Total) |" }
}

$lines += ""
$lines += "## 逐文档明细"
foreach ($a in $audits) {
    $lines += ""
    $lines += "### $($a.Document)"
    $lines += ""
    if (-not $a.Reached) {
        # 主动跳过和真的查不成是两回事，报告里不能混为一谈
        $lines += $(if ($a.Skipped) { $a.Error } else { "未能连接或审计：$($a.Error)" })
        continue
    }
    $lines += "| 规则 | 级别 | 结果 | 说明 |"
    $lines += "|---|---|---|---|"
    foreach ($r in $a.Results) {
        $status = switch ($r.Status) {
            'pass'  { '通过' }
            'fail'  { if ($r.Severity -eq 'error') { '**不合格**' } else { '待改进' } }
            'skip'  { '跳过' }
            'error' { '查不成' }
        }
        $lines += "| $($r.Title) | $($r.Severity) | $status | $($r.Detail) |"
    }
}

$lines += ""
$lines += "---"
$lines += ""
$lines += "规则定义：``$Standard``"
$lines += ""
$lines += "> 本次覆盖的是**本机所有 Revit 实例里打开的全部项目文档**。"
$lines += "> 查询不切换活动文档，用户正在看的东西不受打扰。"

$lines -join "`r`n" | Out-File -FilePath $Report -Encoding utf8

Write-Host ""
Write-Host "汇总报告：$Report"

# 主动跳过的文档不算失败——开着一个族文档不该让整批审计判定为不合格
if ($compliant.Count -lt $reached.Count -or $broken.Count -gt 0) { exit 1 }
