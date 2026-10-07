using SkiaSharp;
using Xunit;

namespace Compositor.Imaging.Tests;

/// <summary>
/// 移植 <c>ImageImportTests.swift</c> 中与编解码直接相关的用例。
/// </summary>
/// <remarks>
/// <para>
/// <b>期望值纪律：</b>所有阈值与断言都<b>逐字抄自</b> macOS 版
/// <c>CompositorTests/ImageImportTests.swift</c>，行号标在每条用例的注释里。
/// 禁止自己另定阈值。
/// </para>
/// <para>
/// <b>已知无法移植的用例（如实标注，不假装通过）：</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <c>supportedFormats</c>（:27-37）在 Mac 上参数化跑 PNG/JPEG/TIFF/HEIC 四种。
/// SkiaSharp 官方二进制不含 TIFF/HEIC 解码器，Windows 上这两种<b>跑不了</b>。
/// 本工程只跑 PNG/JPEG 两个参数，缺口记在 <c>ImageFormatGate.MacOnlyFormats</c>。
/// </item>
/// <item>
/// <c>orientationAndColorConversion</c>（:39-46）依赖 EXIF orientation 6 把 64×32 转成 32×64，
/// 外加 Display P3 → sRGB 转换。SkiaSharp 解码不自动套用 EXIF 方向。
/// 该能力<b>尚未实现</b>，不在本文件里假装覆盖。
/// </item>
/// </list>
/// </remarks>
public sealed class ImageImportCodecTests
{
    /// <summary>
    /// 造一张 64×32 的测试图：左半 32×32 纯红不透明，右半全透明。
    /// </summary>
    /// <remarks>
    /// 逐字照抄 <c>ImageImportTests.swift:13-17</c> 的 fixture：64×32 的 premultipliedLast 上下文，
    /// <c>fill(CGRect(x:0, y:0, width:32, height:32))</c> 纯红。
    /// </remarks>
    private static byte[] BuildFixturePng()
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(64, 32, SKColorType.Rgba8888, SKAlphaType.Premul));

        var pixels = bitmap.GetPixelSpan();
        pixels.Clear();

        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var i = ((y * 64) + x) * 4;
                pixels[i] = 255;     // red
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                pixels[i + 3] = 255; // opaque
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// 一张 1×1 的最小合法 GIF。
    /// </summary>
    /// <remarks>
    /// 必须手写而不是用 Skia 编码：<b>SkiaSharp 只能解 GIF，不能编 GIF</b>
    /// （<c>image.Encode(SKEncodedImageFormat.Gif, …)</c> 返回 null）。
    /// 这本身也是一条信息：Mac 版能编 GIF（CoreGraphics 支持）而 Windows 编不了，
    /// 但本项目只导入不导出 GIF，所以不构成缺口。
    /// </remarks>
    private static byte[] BuildFixtureGif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,             // "GIF89a"
        0x01, 0x00, 0x01, 0x00,                         // 1 × 1
        0x80, 0x00, 0x00,                               // 全局颜色表，2 项
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,             // 黑、白
        0x21, 0xF9, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, // 图形控制扩展
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, // 图像描述符
        0x02, 0x02, 0x44, 0x01, 0x00,                   // LZW 最小码长 2 + 1 像素数据
        0x3B,                                           // 结束符
    ];

    /// <summary>
    /// 对应 <c>ImageImportTests.swift:27-37 supportedFormats</c>。
    /// 期望值：宽 64、高 32、缩略图两边都 ≤ 96。
    /// </summary>
    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    public void SupportedFormat_DecodesToExpectedSizeAndThumbnail(string format)
    {
        var fixture = BuildFixturePng();
        var payload = format == "jpeg"
            ? TranscodeToJpeg(fixture)
            : fixture;

        using var stream = new MemoryStream(payload);
        using var result = PngCodec.Decode(stream, ImagingLimits.DocumentPixelBudget, "fixture");

        Assert.Equal(64, result.Width);
        Assert.Equal(32, result.Height);

        // :35-36 —— thumbnail.width <= 96 / thumbnail.height <= 96
        Assert.True(result.Thumbnail.Width <= 96, $"thumbnail width was {result.Thumbnail.Width}");
        Assert.True(result.Thumbnail.Height <= 96, $"thumbnail height was {result.Thumbnail.Height}");
    }

    /// <summary>
    /// 对应 <c>ImageImportTests.swift:48-60 pngPreservesTransparency</c>。
    /// 期望值逐字抄：<c>bytes[3] == 255</c>、<c>bytes[0] >= 250</c>、<c>bytes[63 * 4 + 3] == 0</c>。
    /// </summary>
    [Fact]
    public void PngPreservesTransparency()
    {
        var fixture = BuildFixturePng();
        using var stream = new MemoryStream(fixture);
        using var result = PngCodec.Decode(stream, ImagingLimits.DocumentPixelBudget, "fixture");

        var bytes = result.Image.GetPixelSpan();

        // 像素 0 = 左上角，纯红不透明
        Assert.Equal((byte)255, bytes[3]);
        Assert.True(bytes[0] >= 250, $"red channel was {bytes[0]}");

        // 像素 63 = 顶行最右，落在透明区
        Assert.Equal((byte)0, bytes[(63 * 4) + 3]);
    }

    /// <summary>
    /// 对应 <c>ImageImportTests.swift:62-75</c> 的限额与无效文件两段。
    /// </summary>
    [Fact]
    public void OverBudgetImageIsTooLarge()
    {
        var fixture = BuildFixturePng();
        using var stream = new MemoryStream(fixture);

        // :66 —— decode(url, remainingPixels: 10) 应抛 tooLarge
        var error = Assert.Throws<ImageCodecException>(
            () => PngCodec.Decode(stream, remainingPixels: 10, "fixture"));
        Assert.Equal(ImageImportError.TooLarge, error.Kind);
    }

    /// <summary>
    /// 对应 <c>ImageImportTests.swift:69-75</c>：写入 <c>"not an image"</c> 应抛 unreadable。
    /// </summary>
    [Fact]
    public void InvalidFileIsUnreadable()
    {
        using var stream = new MemoryStream("not an image"u8.ToArray());

        var error = Assert.Throws<ImageCodecException>(
            () => PngCodec.Decode(stream, ImagingLimits.DocumentPixelBudget, "fixture"));
        Assert.Equal(ImageImportError.Unreadable, error.Kind);
    }

    /// <summary>
    /// 对应 <c>ImageImportTests.swift:76-81</c>：GIF 必须被判 unsupported。
    /// </summary>
    /// <remarks>
    /// <b>这条用例的存在理由：</b>SkiaSharp <b>是能</b>解 GIF 的。如果闸门直接信
    /// <c>SKCodec.EncodedFormat</c> 就放行，Windows 版会接受 Mac 版拒收的格式，
    /// 产出 Mac 打不开的工程。这里把"必须拒"钉成断言。
    /// </remarks>
    [Fact]
    public void GifIsUnsupported_EvenThoughSkiaCanDecodeIt()
    {
        var gif = BuildFixtureGif();
        using var stream = new MemoryStream(gif);

        var error = Assert.Throws<ImageCodecException>(
            () => PngCodec.Decode(stream, ImagingLimits.DocumentPixelBudget, "fixture"));
        Assert.Equal(ImageImportError.Unsupported, error.Kind);
    }

    private static byte[] TranscodeToJpeg(byte[] source)
    {
        using var input = SKBitmap.Decode(source);
        using var image = SKImage.FromBitmap(input);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}