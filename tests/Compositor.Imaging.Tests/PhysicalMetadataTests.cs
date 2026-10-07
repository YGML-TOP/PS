using SkiaSharp;
using Xunit;

namespace Compositor.Imaging.Tests;

/// <summary>
/// PNG 物理分辨率（pHYs 块）的写入测试。
/// </summary>
/// <remarks>
/// Mac 侧没有对应测试——它把 DPI 交给 ImageIO 的属性字典，由系统写进 PNG。
/// 这里测的是 <b>我们自己手写的 pHYs 块</b>是否符合 PNG 规范（值、单位、位置、CRC），
/// 属于「按规范自证」，不是「与 Mac 比对」。Mac 写出的 pHYs 数值应由
/// <c>ai2-06</c> 的往返夹具阶段二（真实 Mac 文件）来比对。
/// </remarks>
public sealed class PhysicalMetadataTests
{
    [Fact]
    public void ApplyPngDpi_WritesPhysChunkWithCorrectValue()
    {
        var original = BuildPng();
        var stamped = PhysicalMetadata.ApplyPngDpi(original, 72d);

        var phys = FindChunk(stamped, "pHYs");
        Assert.NotNull(phys);

        var perMetre = ReadBigEndianInt32(phys, 0);
        var perMetreY = ReadBigEndianInt32(phys, 4);
        var unit = phys[8];

        // 72 dpi → 72 / 0.0254 = 2834.6 → 2835 pixels per metre
        Assert.Equal(2835, perMetre);
        Assert.Equal(perMetre, perMetreY);
        Assert.Equal((byte)1, unit); // 1 = metre
    }

    [Theory]
    [InlineData(72d)]
    [InlineData(150d)]
    [InlineData(300d)]
    [InlineData(600d)]
    public void ApplyPngDpi_IsIdempotent(double dpi)
    {
        var original = BuildPng();
        var once = PhysicalMetadata.ApplyPngDpi(original, dpi);
        var twice = PhysicalMetadata.ApplyPngDpi(once, dpi);

        Assert.Equal(once.Length, twice.Length);

        // 只能有一个 pHYs；重复调用是替换而不是叠加。
        Assert.Equal(1, CountChunks(twice, "pHYs"));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ApplyPngDpi_ProducesAFileSkiaCanStillDecode()
    {
        var original = BuildPng();
        var stamped = PhysicalMetadata.ApplyPngDpi(original, 300d);

        // CRC 或块结构写错的话，这里会直接解不出来。
        using var decoded = SKBitmap.Decode(stamped);
        Assert.NotNull(decoded);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(8, decoded.Height);
    }

    [Fact]
    public void ApplyPngDpi_PlacesPhysBeforeIdat()
    {
        var original = BuildPng();
        var stamped = PhysicalMetadata.ApplyPngDpi(original, 72d);

        var physOffset = FindChunkOffset(stamped, "pHYs");
        var idatOffset = FindChunkOffset(stamped, "IDAT");

        Assert.True(physOffset >= 0, "pHYs 未写入");
        Assert.True(idatOffset >= 0, "IDAT 未找到");
        Assert.True(physOffset < idatOffset, "pHYs 必须排在 IDAT 之前");
    }

    [Fact]
    public void ApplyPngDpi_NonPngPassthrough()
    {
        var notPng = "not a png"u8.ToArray();
        Assert.Equal(notPng, PhysicalMetadata.ApplyPngDpi(notPng, 72d));
    }

    [Fact]
    public void ApplyPngDpi_NonPositiveOrNaNDpiPassthrough()
    {
        var original = BuildPng();

        Assert.Equal(original, PhysicalMetadata.ApplyPngDpi(original, 0d));
        Assert.Equal(original, PhysicalMetadata.ApplyPngDpi(original, -72d));
        Assert.Equal(original, PhysicalMetadata.ApplyPngDpi(original, double.NaN));
    }

    private static byte[] BuildPng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static IEnumerable<(int Offset, int Length, string Type)> Chunks(byte[] png)
    {
        var offset = 8; // 跳过 8 字节签名
        while (offset + 8 <= png.Length)
        {
            var length = ReadBigEndianInt32(png, offset);
            if (length < 0 || offset + 12L + length > png.Length)
            {
                yield break;
            }

            yield return (offset, length, System.Text.Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += 12 + length;
        }
    }

    private static byte[]? FindChunk(byte[] png, string type)
    {
        foreach (var (offset, length, chunkType) in Chunks(png))
        {
            if (chunkType == type)
            {
                return png.AsSpan(offset + 8, length).ToArray();
            }
        }

        return null;
    }

    private static int FindChunkOffset(byte[] png, string type)
    {
        foreach (var (offset, _, chunkType) in Chunks(png))
        {
            if (chunkType == type)
            {
                return offset;
            }
        }

        return -1;
    }

    private static int CountChunks(byte[] png, string type) =>
        Chunks(png).Count(c => c.Type == type);

    private static int ReadBigEndianInt32(byte[] buffer, int offset) =>
        (buffer[offset] << 24)
        | (buffer[offset + 1] << 16)
        | (buffer[offset + 2] << 8)
        | buffer[offset + 3];
}