using Compositor.Core;
using Compositor.Imaging;
using SkiaSharp;

namespace Compositor.Project;

/// <summary>
/// <c>.comp</c> 包内一项资产。实现契约 v1.2 §2.3 的 <see cref="IProjectAsset"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么放 Project 而不是 Imaging：</b>契约 v1.2 把 <see cref="IProjectAsset"/> 定在 Core，
/// 而 SkiaSharp 只能待在 Imaging。<c>Compositor.Imaging</c> 若要实现该接口就得反向引用 Core，
/// 那样 Core 会被<em>传递</em>地传染上 SkiaSharp——而 Core 现在是零依赖的（csproj 无任何引用），
/// 契约层不需要、也不该知道像素格式怎么存。
/// <c>Compositor.Project</c> 同时引用两侧，是唯一能合法持有这个桥的位置。
/// </para>
/// <para>
/// <b>内存布局（契约 v1.2 §2.3）：</b>行优先、无对齐填充，
/// 长度 = <c>Width * Height * (Format == Gray8 ? 1 : 4)</c>。
/// 这一点必须在每个入口都校验——Mac 侧 <c>ProjectStore.swift:167</c> 是按文件大小限流的，
/// 长度对不上会静默读出半张图。
/// </para>
/// <para>
/// <b>铁律 2：</b><see cref="ProjectPixelFormat.PremultipliedRgba8"/> 的数据<b>是预乘的</b>。
/// 从 PNG 解码进来时由 Skia 直接给预乘；<see cref="ProjectPixelFormat.Gray8"/> 没有 alpha，不涉及预乘。
/// </para>
/// </remarks>
public sealed class ProjectAsset : IProjectAsset
{
    private ProjectAsset(int width, int height, ProjectPixelFormat format, byte[] pixels)
    {
        Width = width;
        Height = height;
        Format = format;
        Pixels = pixels;
    }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public ProjectPixelFormat Format { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Pixels { get; }

    /// <summary>每像素字节数：灰度 1，RGBA 4。</summary>
    public int BytesPerPixel => Format == ProjectPixelFormat.Gray8 ? 1 : 4;

    /// <summary>
    /// 从裸字节构造。会校验长度是否恰好等于 <c>Width*Height*BytesPerPixel</c>。
    /// </summary>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="pixels">逐像素数据。</param>
    /// <returns>资产实例。</returns>
    /// <exception cref="ArgumentException">
    /// 尺寸非正，或数据长度与尺寸/格式不匹配。
    /// </exception>
    public static ProjectAsset Create(int width, int height, ProjectPixelFormat format, ReadOnlySpan<byte> pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"资产尺寸必须为正，收到 {width}×{height}。", nameof(width));
        }

        var bpp = format == ProjectPixelFormat.Gray8 ? 1 : 4;
        var expected = (long)width * height * bpp;

        if (pixels.Length != expected)
        {
            throw new ArgumentException(
                $"像素数据长度应为 {expected}（{width}×{height}×{bpp}），实际 {pixels.Length}。",
                nameof(pixels));
        }

        return new ProjectAsset(width, height, format, pixels.ToArray());
    }

    /// <summary>
    /// 从 <see cref="SKBitmap"/> 构造。源必须是预乘 alpha。
    /// </summary>
    /// <param name="bitmap">源位图。</param>
    /// <param name="format">目标格式；传 <see cref="ProjectPixelFormat.Gray8"/> 时做灰度化。</param>
    /// <returns>资产实例。</returns>
    /// <exception cref="ArgumentException">位图为空或尺寸非正。</exception>
    public static ProjectAsset FromBitmap(SKBitmap bitmap, ProjectPixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            throw new ArgumentException($"位图尺寸必须为正，收到 {bitmap.Width}×{bitmap.Height}。", nameof(bitmap));
        }

        var bytes = bitmap.Bytes;

        if (format == ProjectPixelFormat.Gray8)
        {
            return new ProjectAsset(bitmap.Width, bitmap.Height, format, ToGray8(bytes));
        }

        return new ProjectAsset(bitmap.Width, bitmap.Height, format, bytes.ToArray());
    }

    /// <summary>
    /// 转成 <see cref="SKBitmap"/>。
    /// </summary>
    /// <returns>
    /// 位图，调用方负责释放。<see cref="ProjectPixelFormat.PremultipliedRgba8"/> 结果为预乘 alpha；
    /// <see cref="ProjectPixelFormat.Gray8"/> 结果本身就是 1 字节/像素的灰度图，
    /// <c>GetPixel()</c> 取值时 alpha 按 Skia 约定返回 255。
    /// </returns>
    /// <remarks>
    /// <b>不要在 Gray8 分支里做「展开成 RGBA」的逐像素回写。</b>
    /// 一旦 <see cref="SKColorType.Gray8"/> 选定，行字节数就是 <c>Width</c>，
    /// 此时 <c>GetPixelSpan()</c> 的长度是 <c>Width*Height</c>；按 4 字节步长写会越界 4 倍，
    /// 而且会静默踩坏托管堆后面的内存——不抛异常、不崩溃，只是迟早随机崩。
    /// 需要 RGBA 时请走 <see cref="ToRgbaBitmap"/>。
    /// </remarks>
    public SKBitmap ToBitmap()
    {
        var colorType = Format == ProjectPixelFormat.Gray8 ? SKColorType.Gray8 : SKColorType.Rgba8888;

        // Gray8 没有 alpha 通道，AlphaType 只能取 Unpremul/Opaque；写 Premul 不改变布局，
        // 但会让下游误以为存在 alpha 而做多余的反预乘。
        var alphaType = Format == ProjectPixelFormat.Gray8 ? SKAlphaType.Unpremul : SKAlphaType.Premul;
        var info = new SKImageInfo(Width, Height, colorType, alphaType);

        var bitmap = new SKBitmap(info);
        Pixels.Span.CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }

    /// <summary>
    /// 转成 <b>始终为 RGBA</b> 的 <see cref="SKBitmap"/>，便于上层统一走像素合成路径。
    /// </summary>
    /// <returns>预乘 alpha 的 RGBA 位图，调用方负责释放。</returns>
    public SKBitmap ToRgbaBitmap()
    {
        if (Format == ProjectPixelFormat.PremultipliedRgba8)
        {
            return ToBitmap();
        }

        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var target = bitmap.GetPixelSpan();
        var src = Pixels.Span;

        for (int i = 0, o = 0; i < src.Length; i++, o += 4)
        {
            target[o] = src[i];
            target[o + 1] = src[i];
            target[o + 2] = src[i];
            target[o + 3] = 255;
        }

        return bitmap;
    }

    /// <summary>
    /// 从 PNG 字节解码。<b>只接受 PNG</b>，不放行 JPEG。
    /// </summary>
    /// <param name="data">PNG 文件字节。</param>
    /// <param name="expectedFormat">期望的像素格式。</param>
    /// <returns>资产实例。</returns>
    /// <exception cref="ImageCodecException">
    /// 不是 PNG（<see cref="ImageImportError.Unsupported"/>）、解不出（<see cref="ImageImportError.Unreadable"/>）、
    /// 或像素格式与 <paramref name="expectedFormat"/> 不符（蒙版必须是 8 位无 alpha 灰度）。
    /// </exception>
    /// <remarks>
    /// <b>为什么不复用 <see cref="ImageFormatGate"/>：</b>闸门放行 PNG + JPEG，
    /// 而 Mac 在 <c>ProjectStore.swift:174</c> 对包内资源硬判 <c>== UTType.png.identifier</c>。
    /// 契约 v1.2 §2.4 明确要求 Project 侧另走一条「仅 PNG」的严格路径。
    /// </remarks>
    public static ProjectAsset DecodePng(ReadOnlySpan<byte> data, ProjectPixelFormat expectedFormat)
    {
        // ---- 第 1 关：格式级守卫（对应 ProjectStore.swift:173-179）----
        // 这些判定必须在交给 Skia 之前做完。Skia 会把 16 位降成 8 位、把 APNG 取第一帧，
        // 一旦让它先解码，我们就再也分不清「原文件是 16 位」还是「本来就是 8 位」，
        // 而 Mac 那边仍然会拒收（:179 的 depth <= 8、:175 的 count == 1）。
        if (!PngHeader.TryParse(data, out var header))
        {
            // 签名就不对 = 不是 PNG。与 ImageFormatGate 一致归到 unsupported，
            // 让 UI 能提示「包内资源不是图片」而不是「图片损坏」。
            throw new ImageCodecException(
                data.Length >= 8 && data[..8].SequenceEqual(PngSignature)
                    ? ImageImportError.Unreadable
                    : ImageImportError.Unsupported);
        }

        // ProjectStore.swift:179 —— (depth ?? 8) <= 8。
        if (header.BitDepth > 8)
        {
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        // ProjectStore.swift:175 —— CGImageSourceGetCount(source) == 1，APNG 拒收。
        if (header.IsAnimated)
        {
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        // ProjectStore.swift:189 —— 蒙版必须是 monochrome + 8bpc + 无 alpha。
        // 放在这一层而不是 Skia 之后，因为 alpha 有无是 IHDR 的属性，
        // Skia 解码成 Rgba8888 之后 alpha 恒为 255，那时就再也看不出来了。
        if (expectedFormat == ProjectPixelFormat.Gray8
            && !PngHeader.IsMaskCompatible(header.ColorType, header.BitDepth))
        {
            // 归到 Unreadable 而不新造枚举值：Mac 侧遇到同一种情况也是拒绝，
            // 保持 ImageImportError 与上游三 case 一一对应（Windows 只多一个 OutOfMemory）。
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        // ---- 第 2 关：交给 Skia 解码 ----
        // 注意这里**不**检查 SKAlphaType / alpha 有无。Mac 对图层像素没有任何 alpha 守卫
        // （ProjectStore.swift:168-190 只对 isMask 分支查 LayerMask.isValid），
        // 一张无 alpha 的 RGB PNG 作为图层是合法的；曾经在这里多加过一条守卫，已删——
        // 那会让 Windows 拒收 Mac 打得开的工程。
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var codec = SKCodec.Create(stream);

        if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
        {
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        // 尺寸以 IHDR 为准：Skia 对某些损坏流会给出与 IHDR 不符的尺寸，
        // 用 IHDR 的值才能保证与 ImageIO 一致。
        return expectedFormat == ProjectPixelFormat.Gray8
            ? DecodeGray(codec, header.Width, header.Height)
            : DecodeRgba(codec, header.Width, header.Height);
    }

    /// <summary>
    /// 编码成 PNG 字节。
    /// </summary>
    /// <returns>PNG 字节。</returns>
    /// <exception cref="ImageCodecException">编码失败。</exception>
    public byte[] EncodePng()
    {
        using var bitmap = ToBitmap();
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        if (encoded is null)
        {
            throw new ImageCodecException(ExportError.Encode);
        }

        return encoded.ToArray();
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ProjectAsset DecodeGray(SKCodec codec, int width, int height)
    {
        var target = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(target);

        var result = codec.GetPixels(target, bitmap.GetPixels());
        if (result is not SKCodecResult.Success)
        {
            // ⚠️ 只接受 Success，**不**接受 IncompleteInput。
            // Skia 在数据截断时会返回 IncompleteInput 并把已解出的那部分填进缓冲区——
            // 于是「半个文件」会被当成一张完整的图。macOS 侧的
            // CGImageSourceCreateImageAtIndex（ProjectStore.swift:182）遇到截断数据会失败，
            // 两边就会对同一个文件给出不同结论。宁可两边一起拒。
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        return Create(bitmap.Width, bitmap.Height, ProjectPixelFormat.Gray8, bitmap.Bytes);
    }

    private static ProjectAsset DecodeRgba(SKCodec codec, int width, int height)
    {
        var target = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(target);

        var result = codec.GetPixels(target, bitmap.GetPixels());
        if (result is not SKCodecResult.Success)
        {
            // 同 DecodeGray：IncompleteInput 视为失败。
            throw new ImageCodecException(ImageImportError.Unreadable);
        }

        return Create(bitmap.Width, bitmap.Height, ProjectPixelFormat.PremultipliedRgba8, bitmap.Bytes);
    }

    private static byte[] ToGray8(ReadOnlySpan<byte> premultipliedRgba)
    {
        // 灰度化用**反预乘后**的亮度，权重取 Rec.601（Mac 的 CIContext 也用这一套近似）。
        // 直接对预乘值取均值会让半透明像素整体偏暗。
        var count = premultipliedRgba.Length / 4;
        var result = new byte[count];

        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            var a = premultipliedRgba[o + 3];
            if (a == 0)
            {
                result[i] = 0;
                continue;
            }

            var (r, g, b) = PixelAlpha.UnpremultiplyPixel(
                premultipliedRgba[o], premultipliedRgba[o + 1], premultipliedRgba[o + 2], a);

            result[i] = (byte)(((r * 77) + (g * 150) + (b * 29)) >> 8);
        }

        return result;
    }
}
