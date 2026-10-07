<#
.SYNOPSIS
    统计代码行数——用唯一可靠的口径。

.DESCRIPTION
    **不要用 `Get-Content ... | Measure-Object -Line` 或 `(Get-Content ...).Count`。**

    这两种写法在本机都会**漏算空行**，实测同一份文件：
        (Get-Content $f).Count                → 4146 行（错）
        [System.IO.File]::ReadAllLines($f).Count → 4497 行（对）
        差额                                  → 351 行全是空行

    本项目波次 1-A 的交付报告就是用错口径把 4497 行报成了 4146 行，
    导致进度看板数字不可信。任务书早就点名要避免这个坑，此脚本是固化修复。

    注意 `Get-Content` 的行为还随文件末尾空行数变化，不稳定；
    `ReadAllLines()` 是 BCL 语义确定的唯一可信来源。

.PARAMETER Path
    要统计的根目录，默认本仓库的 src\ 与 tests\。

.PARAMETER Extension
    只统计该扩展名的文件。默认 `.cs`。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\count-lines.ps1
    powershell -ExecutionPolicy Bypass -File scripts\count-lines.ps1 -Path C:\some\other\repo
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path (Split-Path -Parent $PSScriptRoot) ''),
    [string[]]$Extension = @('.cs')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$groups = [ordered]@{
    '生产代码 src' = Join-Path $repo 'src'
    '测试代码 tests' = Join-Path $repo 'tests'
}

$grandTotal = 0
$rows = @()

foreach ($entry in $groups.GetEnumerator()) {
    $dir = $entry.Value
    if (-not (Test-Path -LiteralPath $dir)) { continue }

    $total = 0
    $nonBlank = 0
    $files = 0

    foreach ($f in Get-ChildItem -LiteralPath $dir -Recurse -File |
                Where-Object { $Extension -contains $_.Extension } |
                Where-Object { $_.FullName -notmatch '\\(obj|bin|out|TestResults|test-artifacts)\\' }) {
        $lines = [System.IO.File]::ReadAllLines($f.FullName)
        $total += $lines.Count
        $nonBlank += ($lines | Where-Object { $_.Trim() -ne '' }).Count
        $files++
    }

    if ($files -eq 0) { continue }
    $rows += [pscustomobject]@{
        分组 = $entry.Key
        文件数 = $files
        总行数 = $total
        非空行 = $nonBlank
        空行 = $total - $nonBlank
    }
    $grandTotal += $total
}

$rows | Format-Table -AutoSize
Write-Host ""
Write-Host ("仓库总行数（{0}，ReadAllLines 口径）: {1}" -f ($Extension -join '/'), $grandTotal) -ForegroundColor Cyan

if ($Path -and (Test-Path -LiteralPath $Path) -and $Path -ne $repo) {
    Write-Host ""
    Write-Host ("--- 指定路径: {0} ---" -f $Path) -ForegroundColor Yellow
    $n = 0
    foreach ($f in Get-ChildItem -LiteralPath $Path -Recurse -File |
                Where-Object { $Extension -contains $_.Extension } |
                Where-Object { $_.FullName -notmatch '\\(obj|bin|out|TestResults|test-artifacts)\\' }) {
        $c = [System.IO.File]::ReadAllLines($f.FullName).Count
        $n += $c
        Write-Host ("{0,6}  {1}" -f $c, $f.FullName.Replace($repo + '\', ''))
    }
    Write-Host ("指定路径合计: {0}" -f $n) -ForegroundColor Yellow
}