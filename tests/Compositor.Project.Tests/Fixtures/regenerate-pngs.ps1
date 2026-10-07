#requires -Version 5.1
<#
.SYNOPSIS
    按真实 PNG 规范重建 .comp 夹具里的资源字节，文件名/尺寸/颜色类型全部沿用原值。

.DESCRIPTION
    为什么需要这个脚本：

    早期的 .comp 夹具是手工拼出来的——chunk 分帧、IHDR 字段都是对的，
    但 IDAT 里塞的是任意字节（例如 66 E6 09 0B），<b>不是合法的 zlib 流</b>。
    这类文件对「只解析 manifest.json」的测试完全够用，所以问题一直没暴露；
    一旦交给 ProjectStore 真去解码就会全线失败。

    更严重的是：<b>这些夹具在 macOS 上根本打不开</b>。
    ProjectStore.swift:173 要求 CGImageSourceCreateWithData 能解出图像，
    ImageIO 对着假 IDAT 会直接失败 → 抛 .missingImage。
    也就是说，Wave 2 之前它们只能证自洽，永远不可能证互通。

    本脚本保持下列内容不变，只重建像素数据：
      - 文件名（含大写 UUID）
      - IHDR 的 width / height / bitDepth / colorType
    因此依赖这些字段的既有测试不会因为重跑本脚本而失效。

    像素内容由图层 id 决定性地生成（不随机），保证可重现：
      图层像素 = 颜色随 (x, y, id) 变化的半透明 RGBA
      蒙版     = 随 (x, y, id) 变化的灰度，覆盖 0..255 全域

.PARAMETER Root
    夹具根目录，默认取本文件所在目录。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\regenerate-pngs.ps1
#>
[CmdletBinding()]
param(
    # 不在 param 默认值里取 $PSScriptRoot：PowerShell 5.1 在参数绑定阶段
    # 还没填好 $PSScriptRoot，写在这里会静默变成空串。
    [string] $Root
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression | Out-Null

if (-not $Root) { $Root = $PSScriptRoot }

# CRC-32 / Adler-32 用 C# 实现。
#
# 为什么不用 PowerShell 算：PowerShell 的 -bxor 会把操作数按 Int32 处理，
# 0xFFFFFFFF 因此变成 -1，最后 [uint32] 转换直接抛
# "Cannot convert value -1 to type System.UInt32"。
# 用 unchecked 显式做无符号运算就没有这个问题。
Add-Type -TypeDefinition @'
using System;

public static class PngChecksums
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(byte[] data, int offset, int count)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < count; i++)
        {
            crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    public static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521u;
            b = (b + a) % 65521u;
        }
        return (b << 16) | a;
    }
}
'@ -Language CSharp

function New-ZlibStream {
    param([byte[]] $Raw)

    # zlib 包装 = 2 字节头 + 原始 deflate + 4 字节 Adler-32（大端）
    # 0x78 0x01：CMF 的 CM=8（deflate）、CINFO=7（32K 窗口）；FLG 让 (0x78*256+0x01) % 31 == 0。
    $ms = [System.IO.MemoryStream]::new()
    $ms.WriteByte(0x78)
    $ms.WriteByte(0x01)

    $deflate = [System.IO.Compression.DeflateStream]::new($ms, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $deflate.Write($Raw, 0, $Raw.Length)
    $deflate.Dispose()

    $adler = [PngChecksums]::Adler32($Raw)
    $ms.WriteByte([byte](($adler -shr 24) -band 0xFF))
    $ms.WriteByte([byte](($adler -shr 16) -band 0xFF))
    $ms.WriteByte([byte](($adler -shr 8) -band 0xFF))
    $ms.WriteByte([byte]($adler -band 0xFF))

    return $ms.ToArray()
}

function New-PngChunk {
    param([string] $Type, [byte[]] $Data)

    $typeBytes = [System.Text.Encoding]::ASCII.GetBytes($Type)
    $out = [System.IO.MemoryStream]::new()

    $len = $Data.Length
    $out.WriteByte([byte](($len -shr 24) -band 0xFF))
    $out.WriteByte([byte](($len -shr 16) -band 0xFF))
    $out.WriteByte([byte](($len -shr 8) -band 0xFF))
    $out.WriteByte([byte]($len -band 0xFF))
    $out.Write($typeBytes, 0, 4)
    $out.Write($Data, 0, $Data.Length)

    $crcInput = [byte[]]::new($typeBytes.Length + $Data.Length)
    [Array]::Copy($typeBytes, 0, $crcInput, 0, 4)
    [Array]::Copy($Data, 0, $crcInput, 4, $Data.Length)
    $crc = [PngChecksums]::Crc32($crcInput, 0, $crcInput.Length)

    $out.WriteByte([byte](($crc -shr 24) -band 0xFF))
    $out.WriteByte([byte](($crc -shr 16) -band 0xFF))
    $out.WriteByte([byte](($crc -shr 8) -band 0xFF))
    $out.WriteByte([byte]($crc -band 0xFF))

    return $out.ToArray()
}

function Read-Ihdr {
    param([byte[]] $Bytes)

    $w = ($Bytes[16] -shl 24) -bor ($Bytes[17] -shl 16) -bor ($Bytes[18] -shl 8) -bor $Bytes[19]
    $h = ($Bytes[20] -shl 24) -bor ($Bytes[21] -shl 16) -bor ($Bytes[22] -shl 8) -bor $Bytes[23]
    return [pscustomobject]@{ Width = $w; Height = $h; BitDepth = $Bytes[24]; ColorType = $Bytes[25] }
}

function New-Png {
    param(
        [int] $Width,
        [int] $Height,
        [byte] $ColorType,
        [byte] $BitDepth,
        [byte[]] $Scanlines
    )

    $ms = [System.IO.MemoryStream]::new()
    $ms.Write([byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), 0, 8)

    $ihdr = [byte[]]::new(13)
    $ihdr[0] = [byte](($Width -shr 24) -band 0xFF)
    $ihdr[1] = [byte](($Width -shr 16) -band 0xFF)
    $ihdr[2] = [byte](($Width -shr 8) -band 0xFF)
    $ihdr[3] = [byte]($Width -band 0xFF)
    $ihdr[4] = [byte](($Height -shr 24) -band 0xFF)
    $ihdr[5] = [byte](($Height -shr 16) -band 0xFF)
    $ihdr[6] = [byte](($Height -shr 8) -band 0xFF)
    $ihdr[7] = [byte]($Height -band 0xFF)
    $ihdr[8] = $BitDepth
    $ihdr[9] = $ColorType
    $ihdr[10] = 0
    $ihdr[11] = 0
    $ihdr[12] = 0

    $chunk = New-PngChunk -Type 'IHDR' -Data $ihdr
    $ms.Write($chunk, 0, $chunk.Length)

    $idat = New-PngChunk -Type 'IDAT' -Data (New-ZlibStream -Raw $Scanlines)
    $ms.Write($idat, 0, $idat.Length)

    $iend = New-PngChunk -Type 'IEND' -Data ([byte[]]::new(0))
    $ms.Write($iend, 0, $iend.Length)

    return $ms.ToArray()
}

function New-RgbaScanlines {
    param([int] $Width, [int] $Height, [int] $Seed)

    $raw = [byte[]]::new($Height * (1 + $Width * 4))
    $o = 0
    for ($y = 0; $y -lt $Height; $y++) {
        $raw[$o] = 0; $o++   # filter type 0 = None
        for ($x = 0; $x -lt $Width; $x++) {
            $raw[$o] = [byte]((($x * 7) + $Seed) % 256)
            $raw[$o + 1] = [byte]((($y * 11) + $Seed * 2) % 256)
            $raw[$o + 2] = [byte](((($x + $y) * 5)) % 256)
            # alpha 在 0x20..0xFF 之间轮转：既覆盖全不透明，也覆盖半透明，
            # 这样「预乘」这条铁律在夹具里就有真实数据可验，而不是全 255。
            $raw[$o + 3] = [byte](0x20 + (($x + $y + $Seed) % 224))
            $o += 4
        }
    }
    return $raw
}

function New-GrayScanlines {
    param([int] $Width, [int] $Height, [int] $Seed)

    $raw = [byte[]]::new($Height * (1 + $Width))
    $o = 0
    for ($y = 0; $y -lt $Height; $y++) {
        $raw[$o] = 0; $o++
        for ($x = 0; $x -lt $Width; $x++) {
            $raw[$o] = [byte](((($x * 3) + ($y * 5) + $Seed) % 256))
            $o++
        }
    }
    return $raw
}

$packages = Get-ChildItem -Path $Root -Directory -Filter '*.comp' | Sort-Object Name
if (-not $packages) { throw "在 $Root 下没有找到任何 *.comp 夹具。" }

$total = 0

foreach ($pkg in $packages) {
    $manifestPath = Join-Path $pkg.FullName 'manifest.json'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $imagesDir = Join-Path $pkg.FullName 'images'
    if (-not (Test-Path $imagesDir)) { New-Item -ItemType Directory -Path $imagesDir | Out-Null }

    $layerByImage = @{}
    $layerByMask = @{}

    foreach ($layer in $manifest.layers) {
        if ($layer.imageFile) { $layerByImage[$layer.imageFile.ToUpperInvariant()] = $layer }
        if ($layer.maskFile) { $layerByMask[$layer.maskFile.ToUpperInvariant()] = $layer }
    }

    Get-ChildItem -Path $imagesDir -Filter '*.png' | ForEach-Object {
        $target = $_.FullName
        $old = [System.IO.File]::ReadAllBytes($target)
        $ihdr = Read-Ihdr -Bytes $old

        $seed = 0
        foreach ($ch in $_.Name.ToCharArray()) { $seed = ($seed * 31 + [int]$ch) % 251 }

        if ($_.Name.ToUpperInvariant().EndsWith('.MASK.PNG')) {
            if ($ihdr.ColorType -ne 0 -or $ihdr.BitDepth -ne 8) {
                throw "$($_.Name)：蒙版必须是 colorType=0 / bitDepth=8，实际 $($ihdr.ColorType)/$($ihdr.BitDepth)"
            }
            $raw = New-GrayScanlines -Width $ihdr.Width -Height $ihdr.Height -Seed $seed
            $png = New-Png -Width $ihdr.Width -Height $ihdr.Height -ColorType 0 -BitDepth 8 -Scanlines $raw
        }
        else {
            if ($ihdr.ColorType -ne 6 -or $ihdr.BitDepth -ne 8) {
                throw "$($_.Name)：图层像素必须是 colorType=6 / bitDepth=8，实际 $($ihdr.ColorType)/$($ihdr.BitDepth)"
            }
            $raw = New-RgbaScanlines -Width $ihdr.Width -Height $ihdr.Height -Seed $seed
            $png = New-Png -Width $ihdr.Width -Height $ihdr.Height -ColorType 6 -BitDepth 8 -Scanlines $raw
        }

        [System.IO.File]::WriteAllBytes($target, $png)
        $script:total++
        Write-Host ("  {0}\{1}: {2}x{3} color={4} {5} B -> {6} B" -f `
            $pkg.Name, $_.Name, $ihdr.Width, $ihdr.Height, $ihdr.ColorType, $old.Length, $png.Length)
    }
}

Write-Host "已重建 $total 个 PNG 资源。"
