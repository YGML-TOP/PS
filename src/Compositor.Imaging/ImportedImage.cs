using SkiaSharp;

namespace Compositor.Imaging;

/// <summary>
/// 一张导入成功的位图。对应 macOS 版 <c>ImportedImage</c> 的 <c>image</c> / <c>thumbnail</c> / <c>name</c>。
/// </summary>
/// <remarks>
/// 两个位图都是<b>预乘 alpha</b>，这是全项目不变量，不要在下游再乘一次。
/// </remarks>
public sealed class ImportedImage : IDisposable
{
    private bool _disposed;

    /// <summary>构造一个导入结果。</summary>
    /// <param name="image">全尺寸位图，预乘 alpha。</param>
    /// <param name="thumbnail">缩略图位图，预乘 alpha。</param>
    /// <param name="name">不带扩展名的文件名，Mac 版用 <c>deletingPathExtension().lastPathComponent</c>。</param>
    /// <param name="format">识别出的格式。</param>
    public ImportedImage(
        SKBitmap image,
        SKBitmap thumbnail,
        string name,
        SupportedImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(thumbnail);

        Image = image;
        Thumbnail = thumbnail;
        Name = name;
        Format = format;
    }

    /// <summary>全尺寸位图，预乘 alpha。</summary>
    public SKBitmap Image { get; }

    /// <summary>缩略图位图，预乘 alpha。</summary>
    public SKBitmap Thumbnail { get; }

    /// <summary>图层名（文件名去扩展名）。</summary>
    public string Name { get; }

    /// <summary>识别出的格式。</summary>
    public SupportedImageFormat Format { get; }

    /// <summary>全尺寸位图的像素宽。</summary>
    public int Width => Image.Width;

    /// <summary>全尺寸位图的像素高。</summary>
    public int Height => Image.Height;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Image.Dispose();
        Thumbnail.Dispose();
    }
}