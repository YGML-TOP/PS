$ErrorActionPreference = 'Stop'

# 逐条核对本文档引用的 macOS 源码行号是否真的对得上。
# 用法：改完 docs/*.md 后跑一次，不要靠眼睛。
#
# 为什么必须机械校验：本目录下几份文档的价值几乎全在那些行号上。
# 行号漂了但文字还读得通，读者会照着错的行号去改代码——
# 这种「看起来对」的文档比没有文档更危险。

$ErrorActionPreference = 'Stop'

$swiftRoot = 'C:\Users\12510\Desktop\逆向\ps\_eval_Compositor\Compositor-main'
$projectStore = Join-Path $swiftRoot 'Compositor\IO\ProjectStore.swift'
$layerMask = Join-Path $swiftRoot 'Compositor\Document\LayerMask.swift'

if (-not (Test-Path $projectStore)) { throw "找不到 $projectStore" }
if (-not (Test-Path $layerMask)) { throw "找不到 $layerMask" }

$ps = [System.IO.File]::ReadAllLines($projectStore)
$lm = [System.IO.File]::ReadAllLines($layerMask)

# file, line, 该行必须包含的子串
$checks = @(
    @{ f = 'ProjectStore.swift'; l = 10;  t = 'importableImages' }
    @{ f = 'ProjectStore.swift'; l = 23;  t = 'resolution' }
    @{ f = 'ProjectStore.swift'; l = 30;  t = 'guides' }
    @{ f = 'ProjectStore.swift'; l = 52;  t = 'shape' }
    @{ f = 'ProjectStore.swift'; l = 54;  t = 'effects' }
    @{ f = 'ProjectStore.swift'; l = 55;  t = 'text' }
    @{ f = 'ProjectStore.swift'; l = 93;  t = 'LayerMask.isValid' }
    @{ f = 'ProjectStore.swift'; l = 98;  t = 'UTType.png.identifier' }
    @{ f = 'ProjectStore.swift'; l = 111; t = '4 * 1024 * 1024' }
    @{ f = 'ProjectStore.swift'; l = 113; t = 'manifest.json' }
    @{ f = 'ProjectStore.swift'; l = 114; t = '"images"' }
    @{ f = 'ProjectStore.swift'; l = 147; t = 'isDirectory' }
    @{ f = 'ProjectStore.swift'; l = 167; t = '512 * 1024 * 1024' }
    @{ f = 'ProjectStore.swift'; l = 174; t = 'UTType.png.identifier' }
    @{ f = 'ProjectStore.swift'; l = 175; t = 'CGImageSourceGetCount' }
    @{ f = 'ProjectStore.swift'; l = 179; t = 'kCGImagePropertyDepth' }
    @{ f = 'ProjectStore.swift'; l = 189; t = 'LayerMask.isValid' }
    @{ f = 'ProjectStore.swift'; l = 201; t = 'sRGB' }
    @{ f = 'ProjectStore.swift'; l = 223; t = 'maskFile' }
    @{ f = 'ProjectStore.swift'; l = 243; t = 'imageFile' }
    @{ f = 'LayerMask.swift';      l = 23;  t = 'isValid' }
    @{ f = 'LayerMask.swift';      l = 25;  t = 'alphaInfo' }
)

$fail = 0
foreach ($c in $checks) {
    $lines = if ($c.f -eq 'ProjectStore.swift') { $ps } else { $lm }
    $actual = $lines[$c.l - 1]

    # 必须先 escape：ProjectStore 里有 'documentID' 这类带 . 的子串，
    # 不 escape 会把 . 当通配符，产生「假通过」。
    if ($actual -match [regex]::Escape($c.t)) {
        "OK    $($c.f):$($c.l)"
    }
    else {
        $script:fail++
        "FAIL  $($c.f):$($c.l)  期望包含 [$($c.t)]，实际 [$($actual.Trim())]"
    }
}

""
if ($fail -gt 0) {
    "共 $($checks.Count) 条，失败 $fail 条。"
    exit 1
}
"共 $($checks.Count) 条，全部通过。"
