using SkiaSharp;

namespace Compositor.Imaging;

/// <summary>
/// PNG 的读写。承载 <c>.comp</c> 工程里存放的全部资源数据。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 PNG 是 M2 的生死线：</b><c>.comp</c> 是 manifest + 一堆资源文件构成的目录包，
/// 每个图层的 <c>imageFile</c> 与蒙版的 <c>maskFile</c> 指向的都是 PNG。
/// 没有能读能写的 PNG，这条互通路线直接断掉。
/// </para>
/// <para>
/// <b>alpha 纪律：</b>解码产出预乘位图（<see cref="SKAlphaType.Premul"/>），
/// 编码前若传入直通位图会先反预乘。任何一层再乘一次就会边缘发黑，
/// 而这种错误在纯数值断言里抓不住。
/// </para>
/// </remarks>
public static class PngCodec
{
    /// <summary>缩略图长边上限，与 Mac 版 <c>ImageImporter.decode</c> 的 <c>96 / max(w,h)</c> 一致。</summary>
    public const int ThumbnailLongSide = 96;

    /// <summary>
    /// 解码 PNG（以及闸门放行的 JPEG）。
    /// </summary>
    /// <param name="stream">输入流。</param>
    /// <param name="remainingPixels">本次导入后本层预算的剩余像素数。</param>
    /// <param name="name">图层名，调用方传文件名去扩展名的结果。</param>
    /// <returns>导入结果，含全尺寸位图与缩略图，均为预乘 alpha。</returns>
    /// <exception cref="ImageCodecException">
    /// 文件读不出（<see cref="ImageImportError.Unreadable"/>）、格式不放行
    /// （<see cref="ImageImportError.Unsupported"/>）、或超过限额
    /// （<see cref="ImageImportError.TooLarge"/>）。
    /// </exception>
    public static ImportedImage Decode(Stream stream, int remainingPixels, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(name);

        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        if (!ImageFormatGate.TryAccept(codec.EncodedFormat, out var format))
        {
            throw new ImageCodecException(ImageImportError.Unsupported);
        }

        var info = codec.Info;

        // 限额必须在解码前判定：一张 60000×60000 的图若先解码再判，进程已经吃掉了 14 GB。
        if (!ImagingLimits.WithinImportLimits(info.Width, info.Height, remainingPixels))
        {
            throw new ImageCodecException(ImageImportError.TooLarge);
        }

        // 内存护栏同样必须在分配之前。格式上限回答"这是不是合法工程"（与机器无关），
        // 护栏回答"这台机器现在扛不扛得住"（与机器有关），两者不能互相替代。
        ImagingLimits.EnsureWithinMemoryBudget((long)info.Width * info.Height);

        if (!TryDecodePremultiplied(codec, info, out var bitmap))
        {
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        SKBitmap thumbnail;
        try
        {
            thumbnail = CreateThumbnail(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        return new ImportedImage(bitmap, thumbnail, name, format);
    }

    /// <summary>
    /// 把位图编码成 PNG 字节。
    /// </summary>
    /// <param name="bitmap">源位图。可为预乘或直通 alpha，内部会归一到直通后再写。</param>
    /// <param name="resolution">写入 pHYs 的 DPI，取值同 Mac 版的 <c>kCGImagePropertyDPIWidth/Height</c>。</param>
    /// <returns>PNG 字节流。</returns>
    /// <exception cref="ImageCodecException">编码失败。</exception>
    public static byte[] Encode(SKBitmap bitmap, double resolution = 72d)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null)
        {
            throw new ImageCodecException(ExportError.Encode);
        }

        return PhysicalMetadata.ApplyPngDpi(data.ToArray(), resolution);
    }

    private static bool TryDecodePremultiplied(SKCodec codec, SKImageInfo info, out SKBitmap bitmap)
    {
        bitmap = null!;

        var target = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var candidate = new SKBitmap(target);
        var result = codec.GetPixels(target, candidate.GetPixels());

        // IncompleteInput 是截断文件：Skia 已解出能解的部分。
        // 这里按 unreadable 拒掉——Mac 版走 CoreGraphics 会得到 nil，同样落 unreadable。
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            candidate.Dispose();
            return false;
        }

        bitmap = candidate;
        return true;
    }

    /// <summary>
    /// 造缩略图。长边缩到 96 以内，不放大。
    /// </summary>
    /// <param name="source">源位图。</param>
    /// <returns>缩略图位图。</returns>
    /// <remarks>
    /// 逐字照抄 Mac 版 <c>ImageImporter.decode</c>：
    /// <c>let scale = min(1, 96 / max(oriented.extent.width, oriented.extent.height))</c>，
    /// 尺寸 <c>max(1, Int((原尺寸 * scale).rounded()))</c>。
    /// 不放大是刻意的：小图放大只会糊掉，白占内存。
    /// </remarks>
    private static SKBitmap CreateThumbnail(SKBitmap source)
    {
        var longSide = System.Math.Max(source.Width, source.Height);
        var scale = System.Math.Min(1d, ThumbnailLongSide / (double)longSide);
        var width = System.Math.Max(1, (int)System.Math.Round(source.Width * scale, MidpointRounding.AwayFromZero));
        var height = System.Math.Max(1, (int)System.Math.Round(source.Height * scale, MidpointRounding.AwayFromZero));

        var thumbnail = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

        // 源与目标同为预乘 alpha，用 canvas 缩放不会引入额外换算。
        // 采样取 Linear（SkiaSharp 4.x 已用 SKSamplingOptions 取代旧的 FilterQuality）。
        using (var canvas = new SKCanvas(thumbnail))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(
                source,
                new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                paint);
        }

        return thumbnail;
    }
}