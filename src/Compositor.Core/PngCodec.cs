using System.IO.Compression;

namespace Compositor.Core;

/// <summary>
/// 最小 PNG 编解码器，仅供 <see cref="Golden"/> 的黄金样本比对使用。
/// </summary>
/// <remarks>
/// 🔴 <b>为什么自己写：</b>任务书禁止引入第三方依赖（SkiaSharp / ImageSharp 均不可用），
/// <c>System.Drawing.Common</c> 在 .NET 6+ 已弃用且非跨平台。
/// <c>ZLibStream</c> 是 .NET 自带的，所以只需实现 PNG 的分块与反滤波，约 200 行。
///
/// <para><b>支持范围（刻意收窄，只覆盖黄金样本会遇到的形态）：</b></para>
/// <list type="bullet">
/// <item>位深 <b>仅 8</b>（黄金样本一律 8-bit RGBA8，16-bit 会引入无关精度差异）</item>
/// <item>颜色类型 0/2/3/4/6，即灰度 / RGB / 调色板 / 灰度+A / RGBA</item>
/// <item><b>不支持隔行（Adam7）</b>：见到 <c>interlace != 0</c> 直接抛 <see cref="NotSupportedException"/></item>
/// <item>编码只输出 8-bit RGBA（颜色类型 6）</item>
/// </list>
///
/// <para><b>未编译验证</b>：本机无 .NET SDK，本文件与 <see cref="Golden"/> 一样一行都没编译过。
/// SDK 就位后应优先跑 <c>GoldenTests</c> 验证。</para>
/// </remarks>
internal static class PngCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>解码 PNG 为直通（非预乘）RGBA8。</summary>
    /// <param name="png">PNG 文件的完整字节。</param>
    /// <returns>宽、高与 RGBA8 数据（行优先，长度 = 宽 × 高 × 4）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="png"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="InvalidDataException">不是 PNG，或结构被截断。</exception>
    /// <exception cref="NotSupportedException">位深不是 8，或使用了隔行。</exception>
    public static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("不是 PNG 文件（签名不匹配）。");
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        int interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;

        using var idat = new MemoryStream();
        int pos = Signature.Length;
        bool sawEnd = false;
        while (pos + 8 <= png.Length)
        {
            uint chunkLength = ReadU32(png, pos);
            pos += 4;
            string type = System.Text.Encoding.ASCII.GetString(png, pos, 4);
            pos += 4;
            if (pos + chunkLength + 4 > png.Length)
            {
                throw new InvalidDataException($"PNG 数据块 '{type}' 被截断。");
            }

            switch (type)
            {
                case "IHDR":
                    // IHDR 的长度按规范恒为 13。不校验就读 pos+8..pos+12，短块会越界或读到下一个块的内容。
                    if (chunkLength != 13)
                    {
                        throw new InvalidDataException($"IHDR 块长度应为 13，实际为 {chunkLength}。");
                    }

                    width = (int)ReadU32(png, pos);
                    height = (int)ReadU32(png, pos + 4);
                    bitDepth = png[pos + 8];
                    colorType = png[pos + 9];
                    interlace = png[pos + 12];
                    break;
                case "PLTE":
                    palette = png.AsSpan(pos, (int)chunkLength).ToArray();
                    break;
                case "tRNS":
                    transparency = png.AsSpan(pos, (int)chunkLength).ToArray();
                    break;
                case "IDAT":
                    idat.Write(png, pos, (int)chunkLength);
                    break;
                case "IEND":
                    sawEnd = true;
                    pos = png.Length;
                    continue;
            }

            pos += (int)chunkLength + 4;
        }

        // 尾部残块（不足 8 字节的头）原来会被 while 条件静默跳过，
        // 结果是一张被截断的图解出"看起来合理"的错像素 —— 对黄金样本比对是危险的。
        if (pos != png.Length)
        {
            throw new InvalidDataException($"PNG 尾部有 {png.Length - pos} 字节残块。");
        }

        if (!sawEnd)
        {
            throw new InvalidDataException("PNG 缺少 IEND 结束块。");
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG 缺少有效的 IHDR。");
        }

        if (bitDepth != 8)
        {
            throw new NotSupportedException($"只支持 8 位深的 PNG，此文件为 {bitDepth} 位。");
        }

        if (interlace != 0)
        {
            throw new NotSupportedException("不支持隔行（Adam7）PNG。");
        }

        int channels = ChannelsForColorType(colorType);
        if (channels == 0)
        {
            throw new InvalidDataException($"未知的 PNG 颜色类型 {colorType}。");
        }

        if (colorType == 3 && palette is null)
        {
            throw new InvalidDataException("调色板 PNG 缺少 PLTE 块。");
        }

        // 解压 IDAT
        idat.Position = 0;
        byte[] raw;
        using (var inflate = new ZLibStream(idat, CompressionMode.Decompress))
        using (var inflated = new MemoryStream())
        {
            inflate.CopyTo(inflated);
            raw = inflated.ToArray();
        }

        // 反滤波：每行 1 个滤波器字节 + w * channels 个像素字节
        long rowBytes = (long)width * channels;
        long expected = (rowBytes + 1) * height;
        if (raw.Length < expected)
        {
            throw new InvalidDataException(
                $"PNG 解压后数据不足：得到 {raw.Length} 字节，至少需要 {expected} 字节。");
        }

        byte[] planes = raw;
        Unfilter(planes, height, channels, rowBytes);

        // 展开成 RGBA8
        var rgba = new byte[(int)(rowBytes / channels) * 4 * height];
        int outPos = 0;
        for (int y = 0; y < height; y++)
        {
            int rowStart = (int)(y * (rowBytes + 1)) + 1;
            for (int x = 0; x < width; x++)
            {
                int i = rowStart + x * channels;
                byte r;
                byte g;
                byte b;
                byte a = 255;
                switch (colorType)
                {
                    case 0: // 灰度
                        r = g = b = planes[i];
                        // tRNS 在灰度下是 2 字节大端灰度值，等于该值的像素视为全透明
                        if (transparency is not null && transparency.Length >= 2)
                        {
                            int key = (transparency[0] << 8) | transparency[1];
                            if (planes[i] == key)
                            {
                                a = 0;
                            }
                        }

                        break;
                    case 2: // RGB
                        r = planes[i];
                        g = planes[i + 1];
                        b = planes[i + 2];
                        // RGB 的 tRNS 是 6 字节：R/G/B 各一个大端 u16，指定"这一色视为全透明"。
                        // 漏掉它会把带透明色的 RGB 图解成全不透明 —— 黄金样本于是永远对不上。
                        if (transparency is not null && transparency.Length >= 6)
                        {
                            int kr = (transparency[0] << 8) | transparency[1];
                            int kg = (transparency[2] << 8) | transparency[3];
                            int kb = (transparency[4] << 8) | transparency[5];
                            if (r == kr && g == kg && b == kb)
                            {
                                a = 0;
                            }
                        }

                        break;
                    case 3: // 调色板
                    {
                        int idx = planes[i];
                        if (palette is null || idx * 3 + 2 >= palette.Length)
                        {
                            throw new InvalidDataException($"调色板索引 {idx} 越界。");
                        }

                        r = palette[idx * 3];
                        g = palette[idx * 3 + 1];
                        b = palette[idx * 3 + 2];
                        if (transparency is not null && idx < transparency.Length)
                        {
                            a = transparency[idx];
                        }

                        break;
                    }

                    case 4: // 灰度 + A
                        r = g = b = planes[i];
                        a = planes[i + 1];
                        break;
                    default: // 6 = RGBA
                        r = planes[i];
                        g = planes[i + 1];
                        b = planes[i + 2];
                        a = planes[i + 3];
                        break;
                }

                rgba[outPos++] = r;
                rgba[outPos++] = g;
                rgba[outPos++] = b;
                rgba[outPos++] = a;
            }
        }

        return (width, height, rgba);
    }

    /// <summary>编码为 8-bit RGBA PNG（颜色类型 6，每行滤波器固定为 None）。</summary>
    /// <param name="width">宽度像素，&gt; 0。</param>
    /// <param name="height">高度像素，&gt; 0。</param>
    /// <param name="rgba">直通 RGBA8 数据，长度须 ≥ 宽 × 高 × 4；多余尾部被忽略（与 <see cref="PixelBuffer.FromStraightRgba"/> 同一宽松口径）。</param>
    /// <returns>PNG 文件字节。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rgba"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">宽或高不为正，或总字节数溢出 <see cref="int"/>。</exception>
    /// <exception cref="ArgumentException"><paramref name="rgba"/> 太短。</exception>
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);

        // ⚠️ 必须用 long 算，且必须先校验尺寸：
        //   1) `width * height * 4` 在 30000×30000（DocumentLimits 的真实 maxSide）下是 3.6e9，
        //      已超 int.MaxValue —— 用 int 算会溢出成负数，让下面的长度校验被静默绕过。
        //   2) 不校验宽高的话，Encode(0, 0, …) 会产出一张**它自己的 Decode 都拒绝**的 PNG
        //      （Decode 对 width<=0 抛 InvalidDataException），Encode(-1, 5, …) 则抛 OverflowException
        //      这种非契约异常。Encode 必须只抛 ArgumentOutOfRangeException。
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "宽度必须为正。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "高度必须为正。");
        }

        long need = (long)width * height * 4;
        if (need > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"尺寸过大：{width}×{height} 需要 {need} 字节，超过 int 上限。");
        }

        if (rgba.Length < need)
        {
            throw new ArgumentException($"RGBA 数据不足：{rgba.Length} < {need}。", nameof(rgba));
        }

        // 每行前置一个 0（滤波器类型 None），未压缩长度 = h * (1 + w*4)
        int stride = width * 4;
        var raw = new byte[(int)((long)height * (stride + 1))];
        for (int y = 0; y < height; y++)
        {
            int dst = y * (stride + 1);
            raw[dst] = 0;
            Buffer.BlockCopy(rgba, (long)y * stride, raw, dst + 1, stride);
        }

        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var deflate = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }

            compressed = compressedStream.ToArray();
        }

        var ihdr = new byte[13];
        WriteU32(ihdr, 0, (uint)width);
        WriteU32(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // 位深
        ihdr[9] = 6;  // 颜色类型 RGBA
        ihdr[10] = 0; // 压缩方法 deflate
        ihdr[11] = 0; // 滤波方法 adaptive
        ihdr[12] = 0; // 非隔行

        using var output = new MemoryStream();
        output.Write(Signature, 0, Signature.Length);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    /// <summary>PNG 每行的 5 种反滤波。原地把 <paramref name="data"/> 的像素区改成未滤波值。</summary>
    private static void Unfilter(byte[] data, int height, int channels, long rowBytes)
    {
        int bpp = channels; // 仅支持位深 8，故每像素字节数 = 通道数
        var cur = new byte[rowBytes];
        byte[]? prev = null;

        for (int y = 0; y < height; y++)
        {
            int rowStart = (int)(y * (rowBytes + 1));
            byte filter = data[rowStart];
            Buffer.BlockCopy(data, rowStart + 1, cur, 0, (int)rowBytes);

            for (int i = 0; i < rowBytes; i++)
            {
                byte a = i >= bpp ? cur[i - bpp] : (byte)0;
                byte b = prev is not null ? prev[i] : (byte)0;
                byte c = prev is not null && i >= bpp ? prev[i - bpp] : (byte)0;
                byte add;
                switch (filter)
                {
                    case 0:
                        add = 0;
                        break;
                    case 1:
                        add = a;
                        break;
                    case 2:
                        add = b;
                        break;
                    case 3:
                        add = (byte)((a + b) >> 1);
                        break;
                    case 4:
                        add = Paeth(a, b, c);
                        break;
                    default:
                        throw new InvalidDataException($"未知的 PNG 行滤波器类型 {filter}。");
                }

                cur[i] = (byte)(cur[i] + add);
            }

            // 必须写回，**第 0 行也要**：上一行不存在时 a/b/c 全为 0，
            // 但滤波器类型仍可能是 Sub/Paeth，结果与 data 里的原值不同。
            Buffer.BlockCopy(cur, 0, data, rowStart + 1, (int)rowBytes);

            // 交换双缓冲：下一行的"上一行"就是刚反滤波完的这一行。
            // 第二个缓冲区只在第 0 行时新建一次，之后靠交换复用，避免逐行分配。
            byte[]? spare = prev;
            prev = cur;
            cur = spare ?? new byte[rowBytes];
        }
    }

    /// <summary>PNG 的 Paeth 预测器。</summary>
    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    /// <summary>颜色类型对应的通道数。未知类型返回 0。</summary>
    private static int ChannelsForColorType(int colorType) => colorType switch
    {
        0 => 1,
        2 => 3,
        3 => 1,
        4 => 2,
        6 => 4,
        _ => 0,
    };

    private static uint ReadU32(byte[] b, int offset)
        => (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);

    /// <summary>写 4 字节大端无符号整数。形参用 <see cref="Span{T}"/> 而不是 <c>byte[]</c>，
    /// 这样 <c>WriteChunk</c> 里的 <c>stackalloc</c> 缓冲区也能直接传进来（数组不能）。</summary>
    private static void WriteU32(Span<byte> b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteU32(length, 0, (uint)data.Length);
        output.Write(length);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, typeBytes.Length);
        output.Write(data, 0, data.Length);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteU32(crcBytes, 0, crc);
        output.Write(crcBytes);
    }

    /// <summary>CRC-32/IEEE（PNG 用的那条：反射式多项式 0xEDB88320，初值与终值均异或 0xFFFFFFFF）。
    /// 作用范围是「块类型 4 字节 + 块数据」，<b>不含</b>长度字段。</summary>
    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in a)
        {
            c = CrcTable[(int)((c ^ x) & 0xFF)] ^ (c >> 8);
        }

        foreach (byte x in b)
        {
            c = CrcTable[(int)((c ^ x) & 0xFF)] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (int n = 0; n < 256; n++)
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
