<#
.SYNOPSIS
    构建 RevitMCP 的一个或多个 Revit 版本。

.EXAMPLE
    .\build\build-all.ps1                      # 构建全部支持的版本（Release）
    .\build\build-all.ps1 -RevitYears 2024     # 只构建 2024
    .\build\build-all.ps1 -Configuration Debug -RevitYears 2021,2024
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet(2019, 2020, 2021, 2022, 2023, 2024)]
    [int[]] $RevitYears = @(2019, 2020, 2021, 2022, 2023, 2024)
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\RevitMCP.Addin\RevitMCP.Addin.csproj'

$failed = @()

foreach ($year in $RevitYears) {
    $config = "$Configuration R$($year.ToString().Substring(2))"
    Write-Host ""
    Write-Host "=== 构建 Revit $year  ($config) ===" -ForegroundColor Cyan

    dotnet build $project -c $config --nologo
    if ($LASTEXITCODE -ne 0) {
        $failed += $year
        Write-Host "Revit $year 构建失败" -ForegroundColor Red
    }
    else {
        Write-Host "产物：$repoRoot\artifacts\$year\" -ForegroundColor Green
    }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "以下版本构建失败：$($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "全部构建成功。" -ForegroundColor Green
