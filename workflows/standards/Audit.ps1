<#
.SYNOPSIS
    企业标准检查器：把一份 JSON 规则跑在一个 Revit 模型上，产出结构化结果。

.DESCRIPTION
    **规则是数据，检查器是代码。**

    规则写成 JSON 放进版本库——能 diff、能 review、能进 PR、能按项目分支。
    这和本项目拒绝内建数据库是同一条理由：与其维护一个会过期的黑盒，
    不如让规则以纯文本的形式活在它该在的地方。

    每条规则产出同一种形状的结果：通过与否、一句人话、以及具体是哪些构件违规。
    **违规必须点名到构件 ID**——一句"命名不规范"没人能据此动手。

    支持的规则类型见 example-standard.json。
#>

function Invoke-StandardCheck {
    <#
    .SYNOPSIS
        对一个模型跑一整套规则。
    .PARAMETER DocumentId
        要检查哪个文档（来自 revit_list_documents）。省略则查当前活动文档。
        一个 Revit 能同时开十几个项目，批量审计靠它逐个指定——
        **不需要切换活动文档**，那会打断用户正在看的东西。
    .OUTPUTS
        每条规则一个结果对象：Id / Title / Severity / Status / Detail / Offenders
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [PSCustomObject] $Session,
        [Parameter(Mandatory = $true)] $Rules,
        [string] $DocumentId
    )

    $results = @()

    foreach ($rule in $Rules) {
        $severity = if ($rule.severity) { $rule.severity } else { 'error' }

        try {
            $outcome = Invoke-SingleRule -Session $Session -Rule $rule -DocumentId $DocumentId
        }
        catch {
            # 一条规则崩了不能把整轮审计带走——其余规则的结论仍然有价值。
            # 但它必须显示成"没查成"，而不是悄悄算作通过
            $outcome = @{
                Status    = 'error'
                Detail    = "规则执行失败：$($_.Exception.Message)"
                Offenders = @()
            }
        }

        $results += [PSCustomObject]@{
            Id        = $rule.id
            Title     = if ($rule.title) { $rule.title } else { $rule.id }
            Severity  = $severity
            Status    = $outcome.Status
            Detail    = $outcome.Detail
            Offenders = @($outcome.Offenders)
        }
    }

    return $results
}

function Invoke-SingleRule {
    param($Session, $Rule, [string] $DocumentId)

    switch ($Rule.type) {
        'maxWarnings'       { return Test-MaxWarnings       -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'roomsBounded'      { return Test-RoomsBounded      -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'namePattern'       { return Test-NamePattern       -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'requiredParameter' { return Test-RequiredParameter -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'projectUnits'      { return Test-ProjectUnits      -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'viewsOnSheets'     { return Test-ViewsOnSheets     -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'categoryPresent'   { return Test-CategoryPresent   -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'levelsHaveViews'   { return Test-LevelsHaveViews   -Session $Session -Rule $Rule -DocumentId $DocumentId }
        'sheetNumberFormat' { return Test-SheetNumberFormat -Session $Session -Rule $Rule -DocumentId $DocumentId }
        default {
            # 不认识的规则类型要报错，不能当没看见——
            # 静默跳过会让一条写错 type 的规则永远显示"通过"
            throw "未知的规则类型「$($Rule.type)」。可用：maxWarnings、roomsBounded、namePattern、requiredParameter、projectUnits、viewsOnSheets、categoryPresent、levelsHaveViews、sheetNumberFormat"
        }
    }
}

function Add-DocumentId {
    <#
    .SYNOPSIS
        给工具入参挂上 documentId（为空就不挂，让工具用活动文档）。
    .NOTES
        参数**不能**叫 $Args——那是 PowerShell 的自动变量（一个数组）。
        用它当参数名，传进来的 hashtable 会被当成 Object[]，
        报一句 "Cannot convert System.Object[] to System.Collections.Hashtable"，
        而且是在每一次调用上都报，看起来像所有规则同时坏掉。
        这个坑在本文件里踩过两次：一次是局部变量，一次是参数名。
    #>
    param([hashtable] $Arguments, [string] $DocumentId)

    if (-not [string]::IsNullOrWhiteSpace($DocumentId)) { $Arguments['documentId'] = $DocumentId }
    return $Arguments
}

# ==================== 各类规则 ====================

function Test-MaxWarnings {
    param($Session, $Rule, [string] $DocumentId)

    $max = if ($null -ne $Rule.max) { [int]$Rule.max } else { 0 }
    # 不要用 $args 当局部变量名——它是 PowerShell 的自动变量
    $query = Add-DocumentId @{} $DocumentId
    if ($Rule.descriptionContains) { $query['descriptionContains'] = $Rule.descriptionContains }

    $w = Invoke-RevitTool $Session 'revit_get_warnings' $query -ThrowOnError
    $total = $w.Data.total

    $offenders = @()
    foreach ($g in $w.Data.groups) {
        foreach ($id in $g.elementIds) { $offenders += $id }
    }

    if ($total -le $max) {
        return @{ Status = 'pass'; Detail = "模型警告 $total 条，未超过上限 $max"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "模型警告 $total 条，超过上限 $max。最多的一类：$(if($w.Data.groups.Count -gt 0){$w.Data.groups[0].description}else{'—'})"
        Offenders = $offenders | Select-Object -First 50
    }
}

function Test-RoomsBounded {
    param($Session, $Rule, [string] $DocumentId)

    $r = Invoke-RevitTool $Session 'revit_list_rooms' (Add-DocumentId @{ limit = 500 } $DocumentId) -ThrowOnError

    if ($r.Data.total -eq 0) {
        return @{ Status = 'skip'; Detail = '模型里没有房间'; Offenders = @() }
    }

    $bad = @($r.Data.rooms | Where-Object { -not $_.isBounded })

    if ($bad.Count -eq 0) {
        return @{ Status = 'pass'; Detail = "$($r.Data.total) 个房间全部围合，合计 $($r.Data.totalAreaSqm) ㎡"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "$($bad.Count)/$($r.Data.total) 个房间没有围合（面积为 0）"
        Offenders = @($bad | ForEach-Object { "$($_.id) $($_.number) $($_.name)" })
    }
}

function Test-NamePattern {
    param($Session, $Rule, [string] $DocumentId)

    if (-not $Rule.category) { throw 'namePattern 规则缺少 category' }
    if (-not $Rule.pattern) { throw 'namePattern 规则缺少 pattern' }

    # 检查类型名还是实例名：类型命名规范更常见，所以默认查类型
    $scope = if ($Rule.scope) { $Rule.scope } else { 'type' }

    if ($scope -eq 'type') {
        $t = Invoke-RevitTool $Session 'revit_list_types' (Add-DocumentId @{ category = $Rule.category; limit = 500 } $DocumentId) -ThrowOnError
        $items = @($t.Data.types | ForEach-Object { @{ Id = $_.id; Name = $_.name; InUse = $_.instanceCount -gt 0 } })
        # 没用上的类型不算违规——项目样板里带进来一堆没人用的类型是常态
        if ($Rule.onlyInUse -ne $false) { $items = @($items | Where-Object { $_.InUse }) }
    }
    else {
        $q = Invoke-RevitTool $Session 'revit_query_elements' (Add-DocumentId @{ category = $Rule.category; limit = 1000 } $DocumentId) -ThrowOnError
        $items = @($q.Data.elements | ForEach-Object { @{ Id = $_.id; Name = $_.name } })
    }

    if ($items.Count -eq 0) {
        return @{ Status = 'skip'; Detail = "$($Rule.category) 下没有可检查的对象"; Offenders = @() }
    }

    # negate：**禁止**匹配某个模式。
    # "命名必须含厚度"和"不得使用常规类型"是两类条款，后者只能用排除表达
    $negate = ($Rule.negate -eq $true)
    $bad = if ($negate) {
        @($items | Where-Object { $_.Name -match $Rule.pattern })
    } else {
        @($items | Where-Object { $_.Name -notmatch $Rule.pattern })
    }

    $verb = if ($negate) { '不应匹配' } else { '应匹配' }

    if ($bad.Count -eq 0) {
        return @{ Status = 'pass'; Detail = "$($items.Count) 个全部合规（$verb $($Rule.pattern)）"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "$($bad.Count)/$($items.Count) 个违规（$verb $($Rule.pattern)）"
        Offenders = @($bad | Select-Object -First 50 | ForEach-Object { "$($_.Id) 「$($_.Name)」" })
    }
}

function Test-RequiredParameter {
    param($Session, $Rule, [string] $DocumentId)

    if (-not $Rule.category) { throw 'requiredParameter 规则缺少 category' }
    if (-not $Rule.parameter) { throw 'requiredParameter 规则缺少 parameter' }

    $q = Invoke-RevitTool $Session 'revit_query_elements' (Add-DocumentId @{ category = $Rule.category; limit = 200 } $DocumentId) -ThrowOnError
    if ($q.Data.total -eq 0) {
        return @{ Status = 'skip'; Detail = "$($Rule.category) 下没有构件"; Offenders = @() }
    }

    $ids = @($q.Data.elements | ForEach-Object { $_.id })
    $p = Invoke-RevitTool $Session 'revit_get_element_parameters' (Add-DocumentId @{
        elementIds = $ids; nameContains = $Rule.parameter
    } $DocumentId) -ThrowOnError

    $bad = @()
    foreach ($el in $p.Data.elements) {
        $hit = $el.parameters | Where-Object { $_.name -eq $Rule.parameter } | Select-Object -First 1
        if (-not $hit -or [string]::IsNullOrWhiteSpace($hit.value)) {
            $bad += "$($el.id) 「$($el.name)」"
        }
    }

    # 查询本身可能被 limit 截断，结论要如实带上这个限定
    $scope = if ($q.Data.truncated) { "抽查了 $($q.Data.returned)/$($q.Data.total) 个" } else { "$($q.Data.total) 个" }

    if ($bad.Count -eq 0) {
        return @{ Status = 'pass'; Detail = "$scope 构件的「$($Rule.parameter)」都已填写"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "$scope 构件里有 $($bad.Count) 个没填「$($Rule.parameter)」"
        Offenders = $bad | Select-Object -First 50
    }
}

function Test-ProjectUnits {
    param($Session, $Rule, [string] $DocumentId)

    $u = Invoke-RevitTool $Session 'revit_get_project_units' (Add-DocumentId @{} $DocumentId) -ThrowOnError
    $expected = if ($Rule.lengthUnit) { $Rule.lengthUnit } else { 'millimeters' }

    if ($u.Data.lengthUnit -eq $expected) {
        return @{ Status = 'pass'; Detail = "长度单位为 $expected"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "长度单位是 $($u.Data.lengthUnit)，标准要求 $expected"
        Offenders = @()
    }
}

function Test-ViewsOnSheets {
    param($Session, $Rule, [string] $DocumentId)

    $v = Invoke-RevitTool $Session 'revit_list_views' (Add-DocumentId @{ onlyPlaceable = $true; limit = 1000 } $DocumentId) -ThrowOnError

    # 默认只管这几类"该出图"的视图；明细表、图例之类不在其列
    $types = if ($Rule.viewTypes) { @($Rule.viewTypes) } else { @('FloorPlan', 'Section', 'Elevation') }
    $candidates = @($v.Data.views | Where-Object { $types -contains $_.viewType })

    if ($candidates.Count -eq 0) {
        return @{ Status = 'skip'; Detail = '没有该类视图'; Offenders = @() }
    }

    $orphans = @($candidates | Where-Object { -not $_.sheetId })

    if ($orphans.Count -eq 0) {
        return @{ Status = 'pass'; Detail = "$($candidates.Count) 个视图都已放到图纸上"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "$($orphans.Count)/$($candidates.Count) 个视图没有放到任何图纸上"
        Offenders = @($orphans | Select-Object -First 50 | ForEach-Object { "$($_.id) 「$($_.name)」($($_.viewType))" })
    }
}

function Test-CategoryPresent {
    param($Session, $Rule, [string] $DocumentId)

    if (-not $Rule.category) { throw 'categoryPresent 规则缺少 category' }
    $min = if ($null -ne $Rule.min) { [int]$Rule.min } else { 1 }

    $q = Invoke-RevitTool $Session 'revit_query_elements' (Add-DocumentId @{ category = $Rule.category; limit = 1 } $DocumentId) -ThrowOnError

    if ($q.Data.total -ge $min) {
        return @{ Status = 'pass'; Detail = "$($Rule.category) 有 $($q.Data.total) 个，不少于 $min"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "$($Rule.category) 只有 $($q.Data.total) 个，标准要求至少 $min"
        Offenders = @()
    }
}

function Test-LevelsHaveViews {
    <#
    .SYNOPSIS
        每个建筑标高都要有对应的楼层平面视图。
    .NOTES
        这是第一条**需要交叉两类对象**的规则：标高一份、视图一份，靠 levelId 对上。
        单看任何一边都答不了"哪个标高漏了平面图"。
    #>
    param($Session, $Rule, [string] $DocumentId)

    $levels = Invoke-RevitTool $Session 'revit_list_levels' (Add-DocumentId @{} $DocumentId) -ThrowOnError
    $views = Invoke-RevitTool $Session 'revit_list_views' (Add-DocumentId @{ viewType = 'FloorPlan'; limit = 1000 } $DocumentId) -ThrowOnError

    # 默认只管建筑楼层：结构标高、参照标高本来就不一定出平面图。
    # 但"建筑楼层"这个标记也不够准——实测官方样例里
    # Foundation / Ceiling / Roof Line 都是 building story，却都不该出楼层平面。
    # 所以再给一个按名字排除的口子，让标准能落到具体项目上
    $onlyStories = ($Rule.onlyBuildingStories -ne $false)
    $targets = @($levels.Data.levels | Where-Object {
        (-not $onlyStories -or $_.isBuildingStory) -and
        ([string]::IsNullOrWhiteSpace($Rule.excludePattern) -or $_.name -notmatch $Rule.excludePattern)
    })

    if ($targets.Count -eq 0) {
        return @{ Status = 'skip'; Detail = '没有建筑标高'; Offenders = @() }
    }

    $covered = @{}
    foreach ($v in $views.Data.views) {
        if ($v.levelId) { $covered[$v.levelId] = $true }
    }

    $bad = @($targets | Where-Object { -not $covered.ContainsKey($_.id) })

    if ($bad.Count -eq 0) {
        return @{ Status = 'pass'; Detail = "$($targets.Count) 个标高都有楼层平面"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "$($bad.Count)/$($targets.Count) 个标高没有楼层平面视图"
        Offenders = @($bad | ForEach-Object { "$($_.id) 「$($_.name)」标高 $($_.elevationMm)mm" })
    }
}

function Test-SheetNumberFormat {
    <#
    .SYNOPSIS
        图纸编号必须符合指定格式。
    .NOTES
        直接读 list_views 里图纸自身的 sheetNumber。
        早先那一版是绕道查图纸构件再读「图纸编号」参数——能work，
        但依赖中文参数名，换个语言的 Revit 就崩。工具层补上这个字段之后就不必了。
    #>
    param($Session, $Rule, [string] $DocumentId)

    if (-not $Rule.pattern) { throw 'sheetNumberFormat 规则缺少 pattern' }

    $v = Invoke-RevitTool $Session 'revit_list_views' (Add-DocumentId @{ viewType = 'DrawingSheet'; limit = 500 } $DocumentId) -ThrowOnError
    $sheets = @($v.Data.views | Where-Object { -not $_.isTemplate })

    if ($sheets.Count -eq 0) {
        return @{ Status = 'skip'; Detail = '模型里没有图纸'; Offenders = @() }
    }

    $missing = @($sheets | Where-Object { [string]::IsNullOrWhiteSpace($_.sheetNumber) })
    if ($missing.Count -eq $sheets.Count) {
        return @{ Status = 'error'; Detail = '读不到任何图纸的编号，无法检查'; Offenders = @() }
    }

    $bad = @($sheets | Where-Object { $_.sheetNumber -notmatch $Rule.pattern })

    if ($bad.Count -eq 0) {
        return @{ Status = 'pass'; Detail = "$($sheets.Count) 张图纸编号都符合 $($Rule.pattern)"; Offenders = @() }
    }

    return @{
        Status    = 'fail'
        Detail    = "$($bad.Count)/$($sheets.Count) 张图纸编号不符合 $($Rule.pattern)"
        Offenders = @($bad | Select-Object -First 50 | ForEach-Object { "$($_.id) 编号「$($_.sheetNumber)」名称「$($_.name)」" })
    }
}

# ==================== 汇总 ====================

function Get-AuditSummary {
    <#
    .SYNOPSIS
        把一组规则结果压成一行结论。
        error 级的失败才算"不合格"；warning 级失败只计数，不翻盘。
    #>
    param([Parameter(Mandatory = $true)] $Results)

    $errors = @($Results | Where-Object { $_.Status -eq 'fail' -and $_.Severity -eq 'error' })
    $warns = @($Results | Where-Object { $_.Status -eq 'fail' -and $_.Severity -ne 'error' })
    $broken = @($Results | Where-Object { $_.Status -eq 'error' })
    $passed = @($Results | Where-Object { $_.Status -eq 'pass' })
    $skipped = @($Results | Where-Object { $_.Status -eq 'skip' })

    [PSCustomObject]@{
        Total    = @($Results).Count
        Passed   = $passed.Count
        Failed   = $errors.Count
        Warned   = $warns.Count
        Broken   = $broken.Count
        Skipped  = $skipped.Count

        # 规则自己崩了也算不合格：它没能证明模型是好的，
        # 而"没查出问题"和"没查"是两回事
        Compliant = ($errors.Count -eq 0 -and $broken.Count -eq 0)
    }
}

function Format-AuditLine {
    param($Result)

    $mark = switch ($Result.Status) {
        'pass'  { '通过' }
        'fail'  { if ($Result.Severity -eq 'error') { '不合格' } else { '待改进' } }
        'skip'  { '跳过' }
        'error' { '查不成' }
        default { $Result.Status }
    }

    return "  [$mark] $($Result.Title)：$($Result.Detail)"
}
