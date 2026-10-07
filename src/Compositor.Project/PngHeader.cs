using System.Buffers.Binary;

namespace Compositor.Project;

/// <summary>
/// PNG 文件头的最小解析：签名 + IHDR 五个字段，外加「是否含 APNG 的 <c>acTL</c> 块」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须自己读，不能靠 <c>SKCodec</c>：</b>Mac 在
/// <c>ProjectStore.swift:179</c> 有一条 <c>(properties[kCGImagePropertyDepth] as? Int ?? 8) &lt;= 8</c> 守卫，
/// 在 <c>:174-175</c> 有一条 <c>CGImageSourceGetCount(source) == 1</c> 守卫。
/// 两条都是<b>格式级</b>判定，而 SkiaSharp 的 <c>SKCodec.Info</c> 只给
/// <c>SKColorType</c>/<c>SKAlphaType</c>——它会在解码时把 16 位静默降成 8 位、
/// 把 APNG 静默取第一帧，于是 Windows 版会「读成功」，Mac 版会「拒收」。
/// 不对称的接受比两边都拒更危险：工程在 Windows 上看着正常，拷到 Mac 就打不开。
/// </para>
/// <para>
/// <b>bit depth 在 IHDR 的第 9 个字节</b>（0-based 索引 24），color type 在索引 25。
/// 合法取值见 RFC 2083 §3.2.4：
/// 0 = grayscale、2 = truecolor、3 = indexed、4 = grayscale+alpha、6 = truecolor+alpha。
/// 只有 0 和 3 没有 alpha 通道。
/// </para>
/// <para>
/// <b>扫描边界：</b><c>acTL</c> 按规范必须出现在 IDAT 之前，所以只需扫到第一个 IDAT 为止。
/// 这样即使文件被截断也不会把整份图片读进内存——<c>checkFile</c> 已经先把体积限在 512 MB 内。
/// </para>
/// </remarks>
internal static class PngHeader
{
    /// <summary>PNG 签名 8 字节。</summary>
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>PNG 颜色类型（IHDR 偏移 25）。</summary>
    internal enum PngColorType : byte
    {
        /// <summary>0：灰度，无 alpha。</summary>
        Grayscale = 0,

        /// <summary>2：真彩色，无 alpha。</summary>
        TrueColor = 2,

        /// <summary>3：索引色，无 alpha。</summary>
        Indexed = 3,

        /// <summary>4：灰度 + alpha。</summary>
        GrayscaleAlpha = 4,

        /// <summary>6：真彩色 + alpha。</summary>
        TrueColorAlpha = 6,
    }

    /// <summary>
    /// 解析结果。
    /// </summary>
    /// <param name="Width">IHDR 宽度。</param>
    /// <param name="Height">IHDR 高度。</param>
    /// <param name="BitDepth">IHDR 位深（1/2/4/8/16）。</param>
    /// <param name="ColorType">IHDR 颜色类型。</param>
    /// <param name="IsAnimated">是否含 APNG 的 <c>acTL</c> 块。</param>
    /// <param name="HasAlphaChannel">
    /// 由颜色类型判定：只有 <see cref="PngColorType.GrayscaleAlpha"/> 与
    /// <see cref="PngColorType.TrueColorAlpha"/> 自带 alpha 通道。
    /// </param>
    internal readonly record struct Info(
        int Width,
        int Height,
        byte BitDepth,
        PngColorType ColorType,
        bool IsAnimated,
        bool HasAlphaChannel);

    /// <summary>
    /// 解析 PNG 头。
    /// </summary>
    /// <param name="data">文件字节；只需包含到第一个 IDAT 之前即可判定 APNG。</param>
    /// <param name="result">解析结果。</param>
    /// <returns>
    /// 签名、IHDR、颜色类型、位深、分块长度全部合法时返回 <see langword="true"/>；
    /// 任何一项不合法返回 <see langword="false"/>，调用方应按「读不出来」处理，
    /// 不要试图从损坏的头上猜尺寸。
    /// </returns>
    internal static bool TryParse(ReadOnlySpan<byte> data, out Info result)
    {
        result = default;

        // 8（签名）+ 4（IHDR 长度）+ 4（"IHDR"）+ 13（IHDR 数据）+ 4（CRC）
        const int HeaderBytes = 33;
        if (data.Length < HeaderBytes || !data[..8].SequenceEqual(Signature))
        {
            return false;
        }

        if (!data[12..16].SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(data[8..12]) != 13)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
        var bitDepth = data[24];
        var colorTypeByte = data[25];

        if (width <= 0 || height <= 0 || !Enum.IsDefined(typeof(PngColorType), colorTypeByte))
        {
            return false;
        }

        // 位深合法性由颜色类型决定（RFC 2083 §3.2.4 表）。这里整张表判，
        // 不给「位深看着奇怪但大概没事」留口子——Mac 那条守卫是 <= 8，我们照抄，
        // 但不能反过来放行 1/2/4/16：Skia 解码成 Rgba8888 后这些差异已经被抹平，
        // 再放行就等于两台机器对同一份文件给出不同像素。
        var colorType = (PngColorType)colorTypeByte;
        if (!IsDepthValid(colorType, bitDepth))
        {
            return false;
        }

        var animated = ContainsAnimatedChunk(data);

        result = new Info(
            width,
            height,
            bitDepth,
            colorType,
            animated,
            HasAlpha(colorType));

        return true;
    }

    /// <summary>
    /// 该颜色类型是否自带 alpha 通道。
    /// </summary>
    /// <param name="colorType">IHDR 颜色类型。</param>
    /// <returns>带 alpha 返回 <see langword="true"/>。</returns>
    internal static bool HasAlpha(PngColorType colorType)
        => colorType is PngColorType.GrayscaleAlpha or PngColorType.TrueColorAlpha;

    /// <summary>
    /// 该颜色类型是否适合当<b>图层蒙版</b>。
    /// </summary>
    /// <param name="colorType">IHDR 颜色类型。</param>
    /// <param name="bitDepth">IHDR 位深。</param>
    /// <returns>
    /// 无 alpha 且是 8 位灰度时返回 <see langword="true"/>。
    /// 对应 Mac 的 <c>LayerMask.isValid</c>（<c>LayerMask.swift:23-26</c>）：
    /// <c>colorSpace.model == .monochrome &amp;&amp; bitsPerComponent == 8 &amp;&amp; alphaInfo == .none</c>。
    /// </returns>
    internal static bool IsMaskCompatible(PngColorType colorType, byte bitDepth)
        => colorType is PngColorType.Grayscale or PngColorType.Indexed
            && bitDepth == 8;

    private static bool IsDepthValid(PngColorType colorType, byte bitDepth) => colorType switch
    {
        PngColorType.Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
        PngColorType.TrueColor => bitDepth is 8 or 16,
        PngColorType.Indexed => bitDepth is 1 or 2 or 4 or 8,
        PngColorType.GrayscaleAlpha => bitDepth is 8 or 16,
        PngColorType.TrueColorAlpha => bitDepth is 8 or 16,
        _ => false,
    };

    /// <summary>
    /// 从第一个 IDAT 之前扫一遍，找 APNG 的 <c>acTL</c> 块。
    /// </summary>
    private static bool ContainsAnimatedChunk(ReadOnlySpan<byte> data)
    {
        var offset = 8;

        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..(offset + 4)]);
            var type = data.Slice(offset + 4, 4);

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                return false;
            }

            if (type.SequenceEqual("acTL"u8))
            {
                return true;
            }

            // length 是 uint32，加 12（4 长度 + 4 类型 + 4 CRC）可能溢出 int。
            // 溢出或越界都当成「这个文件我不认」，而不是无限循环。
            var advance = (long)length + 12;
            if (advance > int.MaxValue || offset + advance > data.Length)
            {
                return false;
            }

            offset += (int)advance;
        }

        return false;
    }
}
