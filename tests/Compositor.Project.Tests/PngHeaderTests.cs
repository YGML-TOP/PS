using Xunit;

namespace Compositor.Project.Tests;

/// <summary>
/// <c>PngHeader</c> 的逐值断言。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这些值不能「照抄实现」：</b>它们全部来自 <b>PNG 规范（RFC 2083 §3.2）</b>
/// 与 <b>macOS 源码</b>，不是来自本实现的运行结果。
/// 如果测试只是把 <c>PngHeader.TryParse</c> 的输出再断言一遍，它只能证明代码没崩。
/// </para>
/// <para>
/// 这组守卫的真实意义：Mac 在 <c>ProjectStore.swift:179</c> 有
/// <c>(properties[kCGImagePropertyDepth] as? Int ?? 8) &lt;= 8</c>，
/// 在 <c>:174-175</c> 有 <c>== UTType.png.identifier</c> 与 <c>CGImageSourceGetCount == 1</c>。
/// 这三条在 Windows 上<b>没有等价物</b>——Skia 会静默降位深、静默取第一帧。
/// 一旦漏判，结果是「Windows 打开正常、Mac 打不开」，而且本地永远不会报错。
/// </para>
/// </remarks>
public sealed class PngHeaderTests
{
    /// <summary>RFC 2083 的 8 字节签名。</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 造一个最小合法 PNG 头：签名 + IHDR + 一个 IDAT。
    /// </summary>
    private static byte[] BuildHeader(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        bool withAnimatedChunk = false,
        bool truncate = false)
    {
        var bytes = new List<byte>(64);
        bytes.AddRange(Signature);

        void AddChunk(string type, byte[] data)
        {
            bytes.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(type));
            bytes.AddRange(data);
            bytes.AddRange([0, 0, 0, 0]); // CRC 占位；本解析器不校验 CRC。
        }

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0; // compression: deflate
        ihdr[11] = 0; // filter: adaptive
        ihdr[12] = 0; // interlace: none
        AddChunk("IHDR", ihdr);

        if (withAnimatedChunk)
        {
            // APNG 的动画控制块。必须出现在 IDAT 之前。
            AddChunk("acTL", [0, 0, 0, 1, 0, 0, 0, 1]);
        }

        AddChunk("IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);

        var result = bytes.ToArray();
        return truncate ? result[..20] : result;
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    [Theory]
    // RFC 2083 §3.2.4 允许的全部 (colorType, bitDepth) 组合
    [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(0, 4)] [InlineData(0, 8)] [InlineData(0, 16)]
    [InlineData(2, 8)] [InlineData(2, 16)]
    [InlineData(3, 1)] [InlineData(3, 2)] [InlineData(3, 4)] [InlineData(3, 8)]
    [InlineData(4, 8)] [InlineData(4, 16)]
    [InlineData(6, 8)] [InlineData(6, 16)]
    public void TryParse_接受规范列出的每一种组合(byte colorType, byte bitDepth)
    {
        Assert.True(PngHeader.TryParse(BuildHeader(4, 3, bitDepth, colorType), out var info));

        Assert.Equal(4, info.Width);
        Assert.Equal(3, info.Height);
        Assert.Equal(bitDepth, info.BitDepth);
        Assert.Equal((PngHeader.PngColorType)colorType, info.ColorType);
        Assert.False(info.IsAnimated);
    }

    [Theory]
    // 规范里**不存在**的 colorType：1、5 是保留值
    [InlineData(1, 8)] [InlineData(5, 8)] [InlineData(7, 8)] [InlineData(9, 8)]
    public void TryParse_拒收保留的colorType(byte colorType, byte bitDepth)
    {
        Assert.False(PngHeader.TryParse(BuildHeader(4, 4, bitDepth, colorType), out _));
    }

    [Theory]
    // 位深与颜色类型不匹配，或位深本身不存在（对照 RFC 2083 §3.2.4 的合法表：
    // 灰度 1/2/4/8/16、真彩 8/16、索引 1/2/4/8、灰度+alpha 8/16、真彩+alpha 8/16）。
    // 第三个参数是「该颜色类型里真实存在的某个合法深度」，只为让规则可读，
    // 断言对象是第二个参数（喂进去的坏深度）。
    [InlineData(0, 3, 8)]    // 灰度：位深 3 不存在
    [InlineData(0, 5, 8)]    // 灰度：位深 5 不存在
    [InlineData(2, 1, 8)]    // 真彩没有 1
    [InlineData(2, 2, 8)]    // 真彩没有 2
    [InlineData(2, 4, 8)]    // 真彩没有 4
    [InlineData(3, 16, 8)]   // 索引没有 16
    [InlineData(4, 4, 8)]    // 灰度+alpha 没有 4
    [InlineData(4, 1, 16)]   // 灰度+alpha 没有 1
    [InlineData(6, 2, 8)]    // 真彩+alpha 没有 2
    [InlineData(6, 1, 16)]   // 真彩+alpha 没有 1
    public void TryParse_拒收位深与颜色类型不匹配的组合(byte colorType, byte badDepth, byte documentedValidDepth)
    {
        // 先确认这一行本身写得有意义：坏深度不应出现在该颜色类型的合法集合里。
        Assert.False(IsDepthAllowedForTest(colorType, badDepth));
        Assert.True(IsDepthAllowedForTest(colorType, documentedValidDepth));

        Assert.False(PngHeader.TryParse(BuildHeader(4, 4, badDepth, colorType), out _));
    }

    /// <summary>测试侧的合法位深表，抄自 RFC 2083 §3.2.4。</summary>
    private static bool IsDepthAllowedForTest(byte colorType, byte depth) => colorType switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        4 => depth is 8 or 16,
        6 => depth is 8 or 16,
        _ => false,
    };

    [Theory]
    [InlineData(0)] [InlineData(4)]
    public void TryParse_拒收零尺寸(byte colorType)
    {
        Assert.False(PngHeader.TryParse(BuildHeader(0, 4, 8, colorType), out _));
        Assert.False(PngHeader.TryParse(BuildHeader(4, 0, 8, colorType), out _));
    }

    [Fact]
    public void TryParse_签名错误一律拒收()
    {
        var bytes = BuildHeader(4, 4, 8, 6);
        bytes[1] = 0x51; // P -> Q

        Assert.False(PngHeader.TryParse(bytes, out _));
    }

    [Fact]
    public void TryParse_空数据不抛异常()
    {
        Assert.False(PngHeader.TryParse([], out _));
        Assert.False(PngHeader.TryParse([0x89, 0x50], out _));
    }

    [Fact]
    public void TryParse_文件被截断时不抛异常()
    {
        // 只给了 20 字节：签名 + IHDR 长度 + 类型 + 前 4 字节宽度。
        Assert.False(PngHeader.TryParse(BuildHeader(4, 4, 8, 6, truncate: true), out _));
    }

    [Fact]
    public void TryParse_识别APNG的acTL块()
    {
        // ProjectStore.swift:175 的 CGImageSourceGetCount(source) == 1。
        // APNG 有多帧，Mac 会拒收；Skia 只给第一帧，不显式判就会静默放行。
        Assert.True(PngHeader.TryParse(BuildHeader(4, 4, 8, 6, withAnimatedChunk: true), out var info));
        Assert.True(info.IsAnimated);
    }

    [Fact]
    public void TryParse_没有acTL时不算动画()
    {
        Assert.True(PngHeader.TryParse(BuildHeader(4, 4, 8, 6), out var info));
        Assert.False(info.IsAnimated);
    }

    [Fact]
    public void TryParse_不扫描IDAT之后的块()
    {
        // acTL 若出现在 IDAT 之后，按规范本就非法；解析器不该为它扫到文件尾。
        // 这里只验「不误报」：正常 PNG 的 IsAnimated 必须为 false。
        Assert.True(PngHeader.TryParse(BuildHeader(8, 8, 8, 6), out var info));
        Assert.False(info.IsAnimated);
    }

    [Theory]
    // alpha 有无由 colorType 决定：只有 4 和 6 自带通道（RFC 2083 §3.2.4）
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(6, true)]
    public void HasAlphaChannel_只认颜色类型(byte colorType, bool expected)
    {
        Assert.Equal(expected, PngHeader.HasAlpha((PngHeader.PngColorType)colorType));
    }

    [Theory]
    // LayerMask.swift:23-26 —— monochrome + 8bpc + alphaInfo == .none
    [InlineData(0, 8, true)]   // 8 位灰度：唯一完全符合的组合
    [InlineData(3, 8, true)]   // 8 位索引：也无 alpha 通道，ImageIO 会读成 monochrome
    [InlineData(0, 16, false)] // 16 位灰度：bitsPerComponent != 8
    [InlineData(0, 4, false)]  // 4 位灰度：bitsPerComponent != 8
    [InlineData(2, 8, false)]  // 真彩色：颜色空间不是 monochrome
    [InlineData(4, 8, false)]  // 灰度+alpha：alphaInfo != .none
    [InlineData(6, 8, false)]  // 真彩色+alpha：两项都不符
    public void IsMaskCompatible_照LayerMaskIsValid判定(byte colorType, byte bitDepth, bool expected)
    {
        Assert.Equal(expected, PngHeader.IsMaskCompatible((PngHeader.PngColorType)colorType, bitDepth));
    }
}
