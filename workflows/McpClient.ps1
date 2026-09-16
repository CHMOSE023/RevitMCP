<#
.SYNOPSIS
    调用本机 Revit 里的 RevitMCP 服务。供 workflows 目录下的工作流脚本 dot-source。

.DESCRIPTION
    走 modern era（2026-07-28）：无状态，不需要 initialize 握手，
    版本随每个请求的 _meta 传递。工作流脚本因此可以随时开始、随时中断。

.EXAMPLE
    . "$PSScriptRoot\McpClient.ps1"
    $session = Connect-RevitMcp
    $info = Invoke-RevitTool $session 'revit_get_document_info' @{}
#>

$script:ProtocolVersion = '2026-07-28'
$script:MetaVersionKey = 'io.modelcontextprotocol/protocolVersion'
$script:MetaClientKey = 'io.modelcontextprotocol/clientInfo'

function Connect-RevitMcp {
    <#
    .SYNOPSIS
        找到本机正在跑的 RevitMCP 实例并读出令牌。
    .PARAMETER Endpoint
        手动指定端点，形如 http://127.0.0.1:7801/mcp。省略时从实例发现文件自动查找。
    .PARAMETER Pid
        同时开着多个 Revit 时，用进程 ID 指定连哪一个。
    #>
    [CmdletBinding()]
    param(
        [string] $Endpoint,
        [int] $ProcessId,
        [string] $Token
    )

    if (-not $Endpoint) {
        $instanceDir = Join-Path $env:LOCALAPPDATA 'RevitMCP\instances'
        if (-not (Test-Path $instanceDir)) {
            throw "找不到实例目录 $instanceDir。Revit 里的 RevitMCP 服务没在运行。"
        }

        $files = Get-ChildItem -Path $instanceDir -Filter 'revit-*.json' -ErrorAction SilentlyContinue
        if (-not $files) {
            throw "没有正在运行的 RevitMCP 实例。请在 Revit 的 RevitMCP 面板上启动服务。"
        }

        $instances = @()
        foreach ($file in $files) {
            try { $instances += (Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json) }
            catch { Write-Warning "实例文件 $($file.Name) 读取失败，跳过。" }
        }

        if ($ProcessId) {
            $instances = @($instances | Where-Object { $_.pid -eq $ProcessId })
            if (-not $instances) { throw "没有 pid 为 $ProcessId 的 RevitMCP 实例。" }
        }

        if ($instances.Count -gt 1) {
            Write-Host "发现 $($instances.Count) 个 Revit 实例：" -ForegroundColor Yellow
            foreach ($i in $instances) {
                Write-Host "  pid=$($i.pid) 端口=$($i.port) 文档=$($i.activeDocument) 写入=$($i.writeEnabled)"
            }
            Write-Host "默认连第一个。要指定请加 -ProcessId。" -ForegroundColor Yellow
        }

        $instance = $instances[0]
        $Endpoint = $instance.endpoint
    }

    if (-not $Token) {
        $configPath = Join-Path $env:APPDATA 'RevitMCP\config.json'
        if (-not (Test-Path $configPath)) {
            throw "找不到配置文件 $configPath，无法取得访问令牌。"
        }
        $config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $Token = $config.token
    }

    if (-not $Token) { throw "配置文件里没有 token。" }

    [PSCustomObject]@{
        Endpoint = $Endpoint
        Token    = $Token
        Calls    = 0
    }
}

function Get-RevitInstances {
    <#
    .SYNOPSIS
        列出本机所有正在跑的 RevitMCP 实例。
    .DESCRIPTION
        实例发现文件里已经有端口、活动文档、写入开关，批处理据此逐个连过去——
        不需要用户手工抄端口号，也不需要服务之间互相知道对方存在。
    .OUTPUTS
        每个实例一个对象：Pid / Port / Endpoint / RevitVersion / ActiveDocument / WriteEnabled
    #>
    [CmdletBinding()]
    param()

    $dir = Join-Path $env:LOCALAPPDATA 'RevitMCP\instances'
    if (-not (Test-Path $dir)) { return @() }

    $instances = @()
    foreach ($file in (Get-ChildItem -Path $dir -Filter 'revit-*.json' -ErrorAction SilentlyContinue)) {
        try {
            $raw = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $instances += [PSCustomObject]@{
                Pid            = $raw.pid
                Port           = $raw.port
                Endpoint       = $raw.endpoint
                RevitVersion   = $raw.revitVersion
                ActiveDocument = $raw.activeDocument
                WriteEnabled   = $raw.writeEnabled
            }
        }
        catch {
            Write-Warning "实例文件 $($file.Name) 读取失败，跳过。"
        }
    }

    return @($instances | Sort-Object Pid)
}

function Invoke-RevitTool {
    <#
    .SYNOPSIS
        调用一个工具，返回统一形状的结果。
    .OUTPUTS
        IsError  工具是否报错（含 CONFIRMATION_REQUIRED 这类可预期的拒绝）
        Code     领域错误码，形如 WRITE_DISABLED；成功时为 $null
        Data     structuredContent，成功时的结构化输出
        Text     文本内容，失败时是给模型看的那段说明
        Warnings 工具产生的警告
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [PSCustomObject] $Session,
        [Parameter(Mandatory = $true)] [string] $Name,
        [hashtable] $Arguments = @{},
        [switch] $ThrowOnError
    )

    $meta = @{}
    $meta[$script:MetaVersionKey] = $script:ProtocolVersion
    $meta[$script:MetaClientKey] = @{ name = 'RevitMCP.workflows'; version = '1.0.0' }

    $body = @{
        jsonrpc = '2.0'
        id      = ++$Session.Calls
        method  = 'tools/call'
        params  = @{
            name      = $Name
            arguments = $Arguments
            _meta     = $meta
        }
    }

    # -Depth 10：PowerShell 的 ConvertTo-Json 默认只展开 2 层，
    # 而建模工具的入参是 elements[].locationLine.p0.x —— 用默认值会把
    # 嵌套对象序列化成 "System.Collections.Hashtable" 这种字符串，且不报错
    $json = $body | ConvertTo-Json -Depth 10 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)

    # modern era 强制要求这三个头与消息体一致（规范的反走私要求：
    # 中间层不解析 JSON 也能看出这是对哪个工具的调用）。少一个就是 400，
    # 校验在协议层之前，服务端日志里不会留下痕迹
    $headers = @{
        'Authorization'         = "Bearer $($Session.Token)"
        'Accept'                = 'application/json, text/event-stream'
        'MCP-Protocol-Version'  = $script:ProtocolVersion
        'Mcp-Method'            = 'tools/call'
        'Mcp-Name'              = $Name
    }

    try {
        $response = Invoke-WebRequest -Uri $Session.Endpoint -Method Post -Body $bytes `
            -ContentType 'application/json; charset=utf-8' -Headers $headers -UseBasicParsing
    }
    catch {
        # 服务端把拒绝的理由放在响应体里，PS 5.1 把它收进 ErrorDetails。
        # 只报 "(400) Bad Request" 等于把唯一有用的信息丢掉
        $detail = $_.ErrorDetails.Message
        if ($detail) { throw "调用 $Name 失败（$($_.Exception.Message)）：$detail" }
        throw "调用 $Name 失败：$($_.Exception.Message)"
    }

    # PS 5.1 会按响应头里的字符集猜编码，中文常常猜错。直接按 UTF-8 解字节最稳
    $text = [System.Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
    $message = $text | ConvertFrom-Json

    if ($message.PSObject.Properties.Name -contains 'error') {
        throw "调用 $Name 返回 JSON-RPC 错误 $($message.error.code)：$($message.error.message)"
    }

    $result = $message.result
    $contentText = $null
    if ($result.content -and $result.content.Count -gt 0) { $contentText = $result.content[0].text }

    $isError = [bool] $result.isError
    $code = $null
    if ($isError -and $contentText -match '^([A-Z_]+):') { $code = $Matches[1] }

    $warnings = @()
    if ($result.structuredContent -and
        ($result.structuredContent.PSObject.Properties.Name -contains 'warnings')) {
        $warnings = @($result.structuredContent.warnings)
    }

    if ($isError -and $ThrowOnError) {
        throw "$Name 失败：$contentText"
    }

    [PSCustomObject]@{
        IsError  = $isError
        Code     = $code
        Data     = $result.structuredContent
        Text     = $contentText
        Warnings = $warnings
    }
}

function Write-ToolWarnings {
    <#
    .SYNOPSIS
        把工具返回的警告打出来。工具刻意说出口的话，不该被脚本吞掉。
    #>
    param([Parameter(Mandatory = $true)] $Result, [string] $Indent = '  ')

    foreach ($warning in $Result.Warnings) {
        Write-Host "$Indent⚠ $warning" -ForegroundColor DarkYellow
    }
}

function Write-Step {
    param([string] $Text)
    Write-Host ""
    Write-Host "── $Text " -ForegroundColor Cyan -NoNewline
    Write-Host ("─" * [Math]::Max(0, 60 - $Text.Length)) -ForegroundColor DarkCyan
}
