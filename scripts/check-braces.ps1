# 括号/引号配平检查器（ReadAllLines 口径，含 BOM 以便 PS 5.1 正确读取中文）
#
# 为什么要自己写：
#   1. Get-Content | Measure-Object -Line 会漏算空行，口径与进度看板不一致。
#   2. 简单的正则数 { } 会把 @"..." 逐字字符串里的花括号算进去，导致误报。
#      波次 1-A 期间我用正则版误报过 2 个文件"不配平"。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts/check-braces.ps1 -Root src
param(
    [string]$Root = "src"
)

$ErrorActionPreference = 'Stop'
$rootPath = Resolve-Path $Root

$files = Get-ChildItem -Path $rootPath -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$failures = 0

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)

    $brace = 0; $paren = 0; $bracket = 0
    $inBlockComment = $false
    $inVerbatim = $false
    $inChar = $false
    $inLineComment = $false
    # 逐字字符串插值里的 "" 表示转义引号，用一个翻转标记处理
    $inVerbatimQuote = $false

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        for ($j = 0; $j -lt $line.Length; $j++) {
            $c = $line[$j]
            $next = if ($j + 1 -lt $line.Length) { $line[$j + 1] } else { [char]0 }

            if ($inLineComment) { continue }
            if ($inBlockComment) {
                if ($c -eq '*' -and $next -eq '/') { $inBlockComment = $false; $j++ }
                continue
            }
            if ($inVerbatim) {
                if ($c -eq '"') {
                    if ($inVerbatimQuote) { $inVerbatimQuote = $false }
                    elseif ($next -eq '"') { $inVerbatimQuote = $true; $j++ }
                    else { $inVerbatim = $false }
                }
                continue
            }
            if ($inChar) {
                if ($c -eq '\') { $j++; continue }
                if ($c -eq "'") { $inChar = $false }
                continue
            }

            # 注释与字符串的起始
            if ($c -eq '/' -and $next -eq '/') { $inLineComment = $true; continue }
            if ($c -eq '/' -and $next -eq '*') { $inBlockComment = $true; $j++; continue }
            if ($c -eq '@' -and $next -eq '"') { $inVerbatim = $true; $j++; continue }
            if ($c -eq '"') { $inVerbatim = $true; continue }   # 普通字符串也走同一套转义处理
            if ($c -eq "'") { $inChar = $true; continue }

            switch ($c) {
                '{' { $brace++ }
                '}' { $brace-- }
                '(' { $paren++ }
                ')' { $paren-- }
                '[' { $bracket++ }
                ']' { $bracket-- }
            }
        }
        # 行注释每行结束要复位（它不跨行）
        $inLineComment = $false
    }

    $ok = ($brace -eq 0 -and $paren -eq 0 -and $bracket -eq 0)
    if (-not $ok) {
        $failures++
        $rel = $file.FullName.Replace((Get-Location).Path + '\', '')
        Write-Host ("FAIL {0}  {{={1}  (={2}  [={3}" -f $rel, $brace, $paren, $bracket) -ForegroundColor Red
    }
}

Write-Host ""
if ($failures -eq 0) {
    Write-Host ("OK  {0} 个 .cs 文件全部配平" -f $files.Count) -ForegroundColor Green
    exit 0
}
Write-Host ("{0} 个文件不配平" -f $failures) -ForegroundColor Red
exit 1
