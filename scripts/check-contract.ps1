# 契约 v1.1 签名核验：把附录 A 的声明与 src/Compositor.Core 的声明逐条比对
#
# 存在理由：项目负责人明确要求「核验签名是否与 v1.1 逐字一致，不看自述」。
# 本脚本给出可复跑的机械结论，取代任何人的口头保证。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\check-contract.ps1
param(
    [string]$Contract = 'C:\Users\12510\Desktop\逆向\ps\文档\总\01-核心引擎-AI1.md',
    [string]$Src      = 'C:\Users\12510\Desktop\逆向\ps\CompositorWindows\src\Compositor.Core'
)

$ErrorActionPreference = 'Stop'

# ── 1. 抽出附录 A 的 csharp 代码块（附录 A 之后出现的那个）──
$md = [System.IO.File]::ReadAllText($Contract)
$startMark = 'namespace Compositor.Core;'
$si = $md.IndexOf($startMark)
if ($si -lt 0) { Write-Host "找不到 'namespace Compositor.Core;' —— 契约文档结构变了" -ForegroundColor Red; exit 2 }
$ci = $md.LastIndexOf('```csharp', $si)
if ($ci -lt 0) { Write-Host '找不到附录 A 的 csharp 代码块' -ForegroundColor Red; exit 2 }
$bodyStart = $md.IndexOf("`n", $ci) + 1
$ce = $md.IndexOf('```', $bodyStart)
if ($ce -lt 0) { Write-Host '代码块未闭合' -ForegroundColor Red; exit 2 }
$contractCode = $md.Substring($bodyStart, $ce - $bodyStart)

# ── 2. 收集 src 全部源码文本（按类型归属建索引）──
$srcFiles = Get-ChildItem -Path $Src -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$allSrc = ($srcFiles | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n"

# ── 3. 解析契约：类型名 + 其成员标识符（按出现顺序扫描，记录「当前类型」）──
$typePattern = '^\s*public\s+(?:(?:sealed|abstract|static|readonly|partial|file)\s+)*(?:record\s+struct|record|class|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)'
$lines = $contractCode -split "`r?`n"
$types = New-Object System.Collections.Generic.List[string]
$members = @{}
$current = $null

foreach ($line in $lines) {
    $i++
    if ($line -match $typePattern) {
        $current = $Matches[1]
        $types.Add($current)
        if (-not $members.ContainsKey($current)) {
            $members[$current] = New-Object System.Collections.Generic.List[string]
        }

        continue
    }

    if ($null -eq $current) { continue }
    if ($line -notmatch '^\s{4,}(?:public|internal)\b') { continue }
    if ($line -match '^\s*///') { continue }

    # 多行方法签名：一直读到括号配平且行尾是 ; 或 { 为止，合成一条「逻辑声明」
    $buf = $line
    while (($buf -notmatch '[;{]\s*$' -or (($buf.ToCharArray() | Where-Object { $_ -eq '(' }).Count -ne ($buf.ToCharArray() | Where-Object { $_ -eq ')' }).Count)) -and $i -lt $lines.Length - 1) {
        $i++
        $buf += ' ' + $lines[$i]
    }

    # 提取形如 `Identifier(` / `Identifier {` 的成员名
    foreach ($m in [regex]::Matches($buf, '([A-Za-z_][A-Za-z0-9_]*)\s*(?:\(|\{)')) {
        $name = $m.Groups[1].Value
        if ($name -in @('public', 'internal', 'get', 'set', 'init', 'static', 'readonly', 'sealed',
                        'abstract', 'override', 'virtual', 'event', 'class', 'record', 'struct',
                        'enum', 'interface', 'partial', 'private', 'protected', 'new', 'params')) { continue }
        $members[$current].Add($name) | Out-Null
    }
}

# 枚举成员：位于 enum { } 内部的裸标识符行（如 `Normal,` / `ColorBurn,`）
$enumName = $null
$enumDepth = 0
for ($i = 0; $i -lt $lines.Length; $i++) {
    $line = $lines[$i]
    if ($line -match '^\s*public\s+enum\s+([A-Za-z_][A-Za-z0-9_]*)') { $enumName = $Matches[1]; $enumDepth = 0; continue }
    if ($null -eq $enumName) { continue }
    $opens = ([regex]::Matches($line, '\{')).Count
    $closes = ([regex]::Matches($line, '\}')).Count
    $enumDepth += $opens - $closes
    if ($enumDepth -gt 0) {
        foreach ($m in [regex]::Matches($line, '([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|,|$)')) {
            $name = $m.Groups[1].Value
            if ($name -match '^(case|default|return|var|public|internal|//)') { continue }
            if (-not $members.ContainsKey($enumName)) {
                $members[$enumName] = New-Object System.Collections.Generic.List[string]
            }

            $members[$enumName].Add($name) | Out-Null
        }
    }

    if ($enumDepth -le 0) { $enumName = $null }
}

# ── 4. 比对 ──
$missingType = @()
$missingMember = @()
$okType = 0
$okMember = 0

foreach ($t in $types) {
    if ($allSrc -match "\b$([regex]::Escape($t))\b") { $okType++ } else { $missingType += $t }
    foreach ($m in ($members[$t] | Select-Object -Unique)) {
        if ($allSrc -match "\b$([regex]::Escape($m))\b") { $okMember++ } else { $missingMember += "$t.$m" }
    }
}

Write-Host ''
Write-Host '=== 契约 v1.1 签名核验 ===' -ForegroundColor Cyan
Write-Host ("契约类型: {0}    命中: {1}    缺失: {2}" -f $types.Count, $okType, $missingType.Count) -ForegroundColor White
Write-Host ("契约成员: {0}    命中: {1}    缺失: {2}" -f (($members.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum), $okMember, $missingMember.Count) -ForegroundColor White

if ($missingType.Count -gt 0) {
    Write-Host ''
    Write-Host '缺失类型:' -ForegroundColor Red
    $missingType | ForEach-Object { Write-Host "  - $_" }
}
if ($missingMember.Count -gt 0) {
    Write-Host ''
    Write-Host '缺失成员:' -ForegroundColor Red
    $missingMember | ForEach-Object { Write-Host "  - $_" }
}

Write-Host ''
if ($missingType.Count -eq 0 -and $missingMember.Count -eq 0) {
    Write-Host '结论: 契约附录 A 列出的类型与成员在 src/ 中全部存在。' -ForegroundColor Green
} else {
    Write-Host '结论: 存在缺失项，见上。' -ForegroundColor Red
}

# ═══════════════════════════════════════════════════════════════════════
# 第二段：把 C# 里的 .comp 字面量与 Swift 源码里的字面量逐条比对
#
# 这是 .comp 互通的唯一真正高危点。v1.0 契约曾把 BlendMode 写成整数枚举，
# 一旦照那个实现，Mac 版读到的图层会全部落到 Normal，而两边都编译通过、测试全绿。
# 「名称存在」查不出这类错，必须比对字面量本身。
# ═══════════════════════════════════════════════════════════════════════
$swiftBlend = 'C:\Users\12510\Desktop\逆向\ps\_eval_Compositor\Compositor-main\Compositor\Document\LayerAppearance.swift'
$swiftSampling = 'C:\Users\12510\Desktop\逆向\ps\_eval_Compositor\Compositor-main\Compositor\Document\LayerTransform.swift'

$fail = 0
Write-Host ''
Write-Host '=== .comp 字面量比对（Swift 原文 vs C# 映射表）===' -ForegroundColor Cyan

# ── 混合模式：抽 Swift `case xxx = "字面量"` ──
$swiftText = [System.IO.File]::ReadAllText($swiftBlend)
$swiftBlendLiterals = [regex]::Matches(
    $swiftText.Substring(0, [Math]::Min(1400, $swiftText.Length)),
    '=\s*"([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique

# ── C# 侧：抽 BlendModeStrings.Literal 数组内容 ──
$csBlend = [System.IO.File]::ReadAllText((Join-Path $Src 'BlendMode.cs'))
$arrMatch = [regex]::Match(
    $csBlend,
    'private static readonly string\[\] Literal\s*=\s*\{(?<body>.*?)\};',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
$csBlendLiterals = [regex]::Matches($arrMatch.Groups['body'].Value, '"([^"]*)"') |
    ForEach-Object { $_.Groups[1].Value }

Write-Host ("混合模式：Swift {0} 个 / C# {1} 个" -f $swiftBlendLiterals.Count, $csBlendLiterals.Count) -ForegroundColor White
for ($i = 0; $i -lt [Math]::Max($swiftBlendLiterals.Count, $csBlendLiterals.Count); $i++) {
    $s = if ($i -lt $swiftBlendLiterals.Count) { $swiftBlendLiterals[$i] } else { '<无>' }
    $c = if ($i -lt $csBlendLiterals.Count) { $csBlendLiterals[$i] } else { '<无>' }
    if ($s -ne $c) {
        $fail++
        Write-Host ("  [{0,2}] Swift='{1}'  C#='{2}'  ← 不一致" -f $i, $s, $c) -ForegroundColor Red
    }
}

# ── 采样档位 ──
$swiftSamplingText = [System.IO.File]::ReadAllText($swiftSampling)
$swiftSamplingLiterals = [regex]::Matches($swiftSamplingText, '=\s*"([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique

$csSampling = [System.IO.File]::ReadAllText((Join-Path $Src 'Transform\LayerTransform.cs'))
$arrMatch2 = [regex]::Match(
    $csSampling,
    'private static readonly string\[\] Literal\s*=\s*\{(?<body>.*?)\};',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
$csSamplingLiterals = [regex]::Matches($arrMatch2.Groups['body'].Value, '"([^"]*)"') |
    ForEach-Object { $_.Groups[1].Value }

Write-Host ("采样档位：Swift {0} 个 / C# {1} 个" -f $swiftSamplingLiterals.Count, $csSamplingLiterals.Count) -ForegroundColor White
for ($i = 0; $i -lt [Math]::Max($swiftSamplingLiterals.Count, $csSamplingLiterals.Count); $i++) {
    $s = if ($i -lt $swiftSamplingLiterals.Count) { $swiftSamplingLiterals[$i] } else { '<无>' }
    $c = if ($i -lt $csSamplingLiterals.Count) { $csSamplingLiterals[$i] } else { '<无>' }
    if ($s -ne $c) {
        $fail++
        Write-Host ("  [{0,2}] Swift='{1}'  C#='{2}'  ← 不一致" -f $i, $s, $c) -ForegroundColor Red
    }
}

Write-Host ''
if ($fail -eq 0) {
    Write-Host '字面量结论: C# 映射表与 Swift 原文逐条一致（含大小写、空格与括号）。' -ForegroundColor Green
} else {
    Write-Host "字面量结论: $fail 处不一致，见上。" -ForegroundColor Red
}

Write-Host ''
Write-Host '⚠ 本脚本的边界（务必读）:' -ForegroundColor Yellow
Write-Host '  1) 第一段是**存在性**检查——证明契约里的类型/成员名在 src/ 中都有。' -ForegroundColor Yellow
Write-Host '     它**不**校验参数类型、参数顺序、返回类型与默认值是否逐字一致。' -ForegroundColor Yellow
Write-Host '  2) 第二段比对的是**字面量字符串**，这是 .comp 互通唯一真正高危的地方。' -ForegroundColor Yellow
Write-Host '  3) 「逐字一致」的终极证明只有一个：装上 SDK 编译 + 下游 5 个 AI 照此写代码。' -ForegroundColor Yellow

if ($missingType.Count -gt 0 -or $missingMember.Count -gt 0 -or $fail -gt 0) { exit 1 }
exit 0
