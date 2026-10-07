using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Compositor.App.Canvas;
using Compositor.Core;

namespace Compositor.App.Controls;

/// <summary>
/// 画布控件：把一张 <see cref="PixelBuffer"/> 直接画到屏幕上。
/// </summary>
/// <remarks>
/// <para>为什么自己重写 <c>Render</c> 而不是用现成的 <c>Image</c> 控件：
/// <see cref="Image"/> 要先经过 PNG 编码/解码或 <c>WriteableBitmap</c> 的公开构造，
/// 而本控件要拿到的是<b>像素缓冲的引用本身</b>——内容变化时不必重新编码，
/// 也不必把 RGBA 转成别的什么东西再转回来。</para>
/// <para>数据通路（这是本控件存在的全部意义）：</para>
/// <code>
/// PixelBuffer (预乘 RGBA8, stride = W*4)
///   → EnsureBitmap: 逐字节换成 BGRA 写进 WriteableBitmap.Lock() 的帧缓冲
///   → Render:        CanvasViewport.ToScreen 算出屏幕矩形
///   → DrawingContext.DrawImage(bitmap, sourceRect, screenRect)
/// </code>
/// <para>🔴 <b>坐标契约</b>：文档左上原点、Y 向下，Avalonia 屏幕坐标也是 Y 向下，
/// 因此<b>不做 Y 翻转</b>。换算全部委托给 <see cref="CanvasViewport"/>。</para>
/// </remarks>
public class CanvasControl : Control
{
    /// <summary>透明棋盘格底：偶数格浅、奇数格深，用来把「透明」画成可见的图案。</summary>
    private static readonly IBrush WorkspaceBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    /// <summary>文档外框线。</summary>
    private static readonly IPen DocumentBorderPen =
        new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1);

    private static readonly IBrush TransparencyBrush = BuildTransparencyBrush(16);
    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

    private PixelBuffer? _bitmapSource;
    private WriteableBitmap? _bitmap;

    /// <summary>定义 <see cref="Pixels"/> 附加属性。</summary>
    public static readonly StyledProperty<PixelBuffer?> PixelsProperty =
        AvaloniaProperty.Register<CanvasControl, PixelBuffer?>(nameof(Pixels));

    /// <summary>定义 <see cref="Zoom"/> 附加属性。</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<CanvasControl, double>(nameof(Zoom), 1.0);

    static CanvasControl()
    {
        AffectsRender<CanvasControl>(PixelsProperty, ZoomProperty);
    }

    /// <summary>要显示的像素缓冲。为 <see langword="null"/> 时画占位提示。</summary>
    public PixelBuffer? Pixels
    {
        get => GetValue(PixelsProperty);
        set => SetValue(PixelsProperty, value);
    }

    /// <summary>相对「自适应铺满」的额外缩放倍率。1 表示刚好铺满可用区域。</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>画布控件留白，单位为屏幕点数。</summary>
    public double ViewportPadding { get; set; } = 24.0;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);

        Rect bounds = new(Bounds.Size);
        context.FillRectangle(WorkspaceBrush, bounds);

        PixelBuffer? pixels = Pixels;
        if (pixels is null)
        {
            DrawPlaceholder(context, bounds);
            return;
        }

        EnsureBitmap(pixels);

        var docSize = new DocSize(pixels.Width, pixels.Height);
        CanvasViewport viewport = CanvasViewport.Fit(bounds, docSize, ViewportPadding);
        double zoom = Zoom;
        if (double.IsFinite(zoom) && zoom > 0 && Math.Abs(zoom - 1.0) > 1e-9)
        {
            viewport = ScaleAboutCenter(viewport, zoom);
        }

        // 文档矩形覆盖整个像素缓冲（原点 0,0，尺寸即缓冲尺寸）。
        var docRect = new DocRect(new DocPoint(0, 0), docSize);
        Rect screen = viewport.ToScreen(docRect);

        // 透明棋盘格画在图像<b>下面</b>：图像里 alpha = 0 的格子会透出底纹，
        // 不透明格子会盖住底纹——这样透明区与不透明区在屏幕上一眼可辨。
        // CanvasViewport.Fit 已保证 screen 落在 bounds 的 padding 内，所以这里不再手工裁剪。
        if (_bitmap is not null)
        {
            context.FillRectangle(TransparencyBrush, screen);
            context.DrawImage(_bitmap, new Rect(0, 0, pixels.Width, pixels.Height), screen);
        }

        context.DrawRectangle(null, DocumentBorderPen, screen);
    }

    /// <summary>
    /// 把 <paramref name="pixels"/> 的字节灌进一张 <see cref="WriteableBitmap"/>。
    /// 缓冲没换引用、尺寸也没变时直接返回，不重复上传。
    /// </summary>
    private void EnsureBitmap(PixelBuffer pixels)
    {
        var size = new PixelSize(pixels.Width, pixels.Height);
        if (_bitmap is not null && ReferenceEquals(_bitmapSource, pixels) && _bitmap.PixelSize == size)
        {
            return;
        }

        // Bgra8888 + Premul：Avalonia 在 Windows 上的默认组合，也是最保险的一条路径。
        // SkiaSharp 的版本由 Avalonia.Skia 传递带入，这里<b>不能</b>自己写 SkiaSharp 的
        // PackageReference（会触发 NU1605 降级错误）。
        var bitmap = new WriteableBitmap(
            size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (ILockedFramebuffer frame = bitmap.Lock())
        {
            int rowBytes = frame.RowBytes;
            int height = frame.Size.Height;

            // 🔴 AllowUnsafeBlocks=false：不能写 unsafe 块，也不能把 IntPtr 强转成 void*，
            // 于是走 Marshal.Copy 这条全托管路径：先在托管数组里摆好 BGRA，
            // 再整块拷进帧缓冲。代价是每次内容变化多一个临时数组，
            // 换来的是不碰 unsafe、也不依赖指针算术。
            byte[] staging = new byte[checked(rowBytes * height)];
            Blit(pixels, staging, rowBytes);
            Marshal.Copy(staging, 0, frame.Address, staging.Length);
        }

        _bitmap = bitmap;
        _bitmapSource = pixels;
    }

    /// <summary>
    /// 预乘 RGBA8 → BGRA8，逐行拷贝。
    /// </summary>
    /// <remarks>
    /// 只交换 R 与 B，<b>不重新预乘</b>：<see cref="PixelBuffer"/> 里存的已经是预乘值，
    /// 再乘一次 alpha 会让所有半透明像素偏暗。
    /// <paramref name="rowBytes"/> 可能大于 <c>Width * 4</c>（行对齐填充），
    /// 所以必须逐行走，不能整块 <c>CopyTo</c>。
    /// </remarks>
    private static void Blit(PixelBuffer src, byte[] staging, int rowBytes)
    {
        Span<byte> dst = staging;
        ReadOnlySpan<byte> rgba = src.Rgba;
        int srcStride = src.Stride;
        int width = src.Width;

        for (int y = 0; y < src.Height; y++)
        {
            int si = y * srcStride;
            int di = y * rowBytes;

            for (int x = 0; x < width; x++)
            {
                dst[di] = rgba[si + 2];            // B
                dst[di + 1] = rgba[si + 1];        // G
                dst[di + 2] = rgba[si];            // R
                dst[di + 3] = rgba[si + 3];        // A
                si += 4;
                di += 4;
            }
        }
    }

    /// <summary>以可用区域中心为锚点做缩放，保持平移后的中心不动。</summary>
    private static CanvasViewport ScaleAboutCenter(CanvasViewport viewport, double factor)
    {
        Viewport v = viewport.Viewport;
        double scale = Math.Clamp(v.PointsPerPixel * factor, CanvasViewport.MinPointsPerPixel, CanvasViewport.MaxPointsPerPixel);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return viewport;
        }

        // 平移原点让文档中心在屏幕上保持不动：原点增量 = 中心 × (1 - 缩放比)。
        double ratio = 1.0 - (scale / v.PointsPerPixel);
        double originX = v.OriginX + ((v.OriginX + (v.PointsPerPixel / 2.0)) * ratio);
        double originY = v.OriginY + ((v.OriginY + (v.PointsPerPixel / 2.0)) * ratio);

        return CanvasViewport.FromViewport(viewport.DocumentSize, new Viewport(originX, originY, scale, scale));
    }

    /// <summary>没有像素缓冲时的占位提示。</summary>
    private static void DrawPlaceholder(DrawingContext context, Rect bounds)
    {
        var dashed = new Rect(bounds.X + 24, bounds.Y + 24, bounds.Width - 48, bounds.Height - 48);
        context.DrawRectangle(null, new Pen(PlaceholderBrush, 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) }, dashed);

        var text = new FormattedText(
            "画布为空 —— 没有像素缓冲可显示",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default),
            14,
            PlaceholderBrush);
        context.DrawText(text, new Point(
            dashed.X + ((dashed.Width - text.Width) / 2),
            dashed.Y + ((dashed.Height - text.Height) / 2)));
    }

    /// <summary>造一块可平铺的透明棋盘格画刷。</summary>
    private static IBrush BuildTransparencyBrush(double tile)
    {
        double half = tile / 2.0;
        Geometry geo = Geometry.Combine(
            new RectangleGeometry(new Rect(0, 0, half, half)),
            new RectangleGeometry(new Rect(half, half, half, half)),
            GeometryCombineMode.Union);

        var drawing = new GeometryDrawing
        {
            Geometry = geo,
            Brush = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
        };

        // Avalonia 12 的 DrawingBrush 没有 TileRect；不平铺区域时按 Drawing 自身的
        // 包围盒（这里正好是 0,0–tile,tile）当成一格。
        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
        };
    }
}