<#
.SYNOPSIS
    把已构建的 RevitMCP 安装到指定 Revit 版本的插件目录。

.DESCRIPTION
    安装到当前用户的 %APPDATA%\Autodesk\Revit\Addins\<年份>\，无需管理员权限。
    会自动 Unblock-File —— 从网络或邮件取得的 DLL 带 Zone.Identifier 标记时，
    Revit 会静默跳过加载，这是"插件装了却不出现"最常见的原因。

.EXAMPLE
    .\build\install.ps1 -RevitYear 2024
    .\build\install.ps1 -RevitYear 2024 -Uninstall
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(2019, 2020, 2021, 2022, 2023, 2024)]
    [int] $RevitYear,

    [ValidateSet('Debug', 'Release')]
    [string] $BuildConfiguration = 'Release',

    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
$targetDir = Join-Path $addinsRoot 'RevitMCP'
$manifest = Join-Path $addinsRoot 'RevitMCP.addin'

if ($Uninstall) {
    if (Test-Path $manifest) { Remove-Item $manifest -Force; Write-Host "已删除 $manifest" }
    if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force; Write-Host "已删除 $targetDir" }
    Write-Host "卸载完成。请重启 Revit $RevitYear。" -ForegroundColor Green
    return
}

$flavor = if ($BuildConfiguration -eq 'Debug') { '-debug' } else { '' }
$sourceDir = Join-Path $repoRoot "artifacts\$RevitYear$flavor"
if (-not (Test-Path $sourceDir)) {
    throw "未找到构建产物：$sourceDir`n请先运行： .\build\build-all.ps1 -Configuration $BuildConfiguration -RevitYears $RevitYear"
}

$mainDll = Join-Path $sourceDir 'RevitMCP.Addin.dll'
if (-not (Test-Path $mainDll)) {
    throw "产物目录中缺少 RevitMCP.Addin.dll：$sourceDir"
}

# Revit 运行时会锁定已加载的 DLL，先确认没有 Revit 在跑
$running = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
if ($running) {
    throw "检测到 Revit 正在运行（PID: $($running.Id -join ', ')）。请先关闭 Revit 再安装。"
}

New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

Copy-Item (Join-Path $sourceDir '*') -Destination $targetDir -Recurse -Force
Get-ChildItem $targetDir -Recurse -File | Unblock-File

$template = Get-Content (Join-Path $PSScriptRoot 'RevitMCP.addin.template') -Raw
Set-Content -Path $manifest -Value $template -Encoding utf8

Write-Host ""
Write-Host "安装完成。" -ForegroundColor Green
Write-Host "  清单: $manifest"
Write-Host "  程序: $targetDir"
Write-Host ""
Write-Host "启动 Revit $RevitYear 后，功能区应出现 RevitMCP 选项卡。"
Write-Host "日志: $env:LOCALAPPDATA\RevitMCP\logs\"
