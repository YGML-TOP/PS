# XML 文档注释标签配平检查器
#
# 存在理由：`TreatWarningsAsErrors=true` 下 CS1570（XML 格式错误）直接构建失败。
# 一次 1-B 交付里，4 处标签写错（<b> 未闭合、</c> 误写成 </b>、多一个 </b>）
# 造成 12 个错误、其中 9 个是级联误报。逐个肉眼看不可靠，
# 本脚本把全仓每个 /// 注释块的标签栈跑一遍，直接指出文件、行号、错配的两端。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\check-xmldoc.ps1 -Root src
param(
    [string]$Root = 'src'
)

$ErrorActionPreference = 'Stop'

# 这些标签是自闭合或不需要配平
$void = @('br', 'hr', 'see', 'seealso', 'paramref', 'typeparamref', 'inheritdoc', 'include', 'exclude')

$files = Get-ChildItem -Path $Root -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

$problems = 0
$blocks = 0

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $stack = New-Object System.Collections.Generic.List[object]
    $inComment = $false
    $startLine = 0

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $lineNo = $i + 1

        # 只看 /// 文档注释行
        $m = [regex]::Match($line, '^\s*///\s?(?<body>.*)$')
        if (-not $m.Success) {
            # 非注释行 = 一个文档注释块结束
            if ($inComment -and $stack.Count -gt 0) {
                $problems++
                $rel = $file.FullName
                Write-Host ("[{0}({1})] 未闭合 <{2}>（开于第 {3} 行）" -f `
                    (Split-Path $rel -Leaf), $startLine, $stack[$stack.Count - 1].Name, $stack[$stack.Count - 1].Line) -ForegroundColor Red
                $stack.Clear()
            }

            continue
        }

        if (-not $inComment) { $inComment = $true; $startLine = $lineNo; $stack.Clear() }

        $body = $m.Groups['body'].Value

        # <code> 与 <c> 内部按纯文本处理（可能有裸 & 和 < ）
        # 这里不做特殊处理，因为本仓库的 <code> 块内已全部转义；真出问题会被报出来。
        foreach ($t in [regex]::Matches($body, '<(?<slash>/?)(?<name>[A-Za-z][A-Za-z0-9]*)\b[^>]*?>')) {
            $name = $t.Groups['name'].Value
            $isClose = $t.Groups['slash'].Value -eq '/'

            if ($name -in $void) { continue }

            if ($isClose) {
                if ($stack.Count -eq 0) {
                    $problems++
                    Write-Host ("[{0}({1})] 多余的 </{2}>（栈为空）" -f (Split-Path $file.FullName -Leaf), $lineNo, $name) -ForegroundColor Red
                }
                elseif ($stack[$stack.Count - 1].Name -ne $name) {
                    $problems++
                    $top = $stack[$stack.Count - 1]
                    Write-Host ("[{0}({1})] </{2}> 与未闭合的 <{3}>（开于第 {4} 行）错配" -f `
                        (Split-Path $file.FullName -Leaf), $lineNo, $name, $top.Name, $top.Line) -ForegroundColor Red
                    # 弹出到匹配项，容忍嵌套错误
                    $idx = -1
                    for ($k = $stack.Count - 1; $k -ge 0; $k--) {
                        if ($stack[$k].Name -eq $name) { $idx = $k; break }
                    }

                    if ($idx -ge 0) { $stack.RemoveRange($idx, $stack.Count - $idx) } else { $stack.Clear() }
                }
                else {
                    $stack.RemoveAt($stack.Count - 1)
                }
            }
            else {
                # 自闭合写法 <tag ... /> 不入栈
                if ($t.Value.TrimEnd().EndsWith('/>')) { continue }
                $stack.Add([pscustomobject]@{ Name = $name; Line = $lineNo }) | Out-Null
            }
        }

        if ($inComment -and $stack.Count -eq 0) { $blocks++ }
    }

    if ($inComment -and $stack.Count -gt 0) {
        $problems++
        Write-Host ("[{0}({1})] 文件结束仍未闭合 <{2}>（开于第 {3} 行）" -f `
            (Split-Path $file.FullName -Leaf), $startLine, $stack[$stack.Count - 1].Name, $stack[$stack.Count - 1].Line) -ForegroundColor Red
    }
}

Write-Host ''
if ($problems -eq 0) {
    Write-Host ("XML 注释标签全部配平（{0} 个文件，{1} 个文档注释块）。" -f $files.Count, $blocks) -ForegroundColor Green
    exit 0
}

Write-Host ("{0} 处标签配平问题。" -f $problems) -ForegroundColor Red
exit 1
