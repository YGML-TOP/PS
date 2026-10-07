using Avalonia;
using Compositor.Core;

namespace Compositor.App.Canvas;

/// <summary>
/// 画布的视图变换：文档坐标 ↔ 屏幕坐标。<b>唯一</b>允许做坐标换算的地方。
/// </summary>
/// <remarks>
/// <para>🔴 <b>Y 轴不翻转</b>（铁律 1）。文档坐标系是左上原点、Y 向下，
/// Avalonia 的屏幕坐标也是 Y 向下，两边方向一致，因此换算就是一条
/// 纯线性变换：<c>screen = doc * PointsPerPixel + Origin</c>。
/// 原项目的 <c>CanvasViewport.swift:25-33</c> 同样没有 Y 取反。
/// 任何 <c>-y</c> 或 <c>height - y</c> 出现在本文件里都是 bug。</para>
/// <para>本类型是对 <see cref="Viewport"/>（契约层提供）的一层包装：
/// 缩放与平移的<b>参数</b>存在 <see cref="Viewport"/> 里，
/// 矩形与点的<b>换算</b>走 <see cref="DocCoord"/>（契约层唯一的换算边界），
/// 这里只负责把两者接起来并把缩放夹到可显示的区间内。</para>
/// </remarks>
public sealed class CanvasViewport
{
    /// <summary>最小缩放（每文档像素对应的屏幕点数）。约等于 5% 的放大。</summary>
    public const double MinPointsPerPixel = 0.05;

    /// <summary>最大缩放。约等于 3200% 的放大。</summary>
    public const double MaxPointsPerPixel = 32.0;

    private CanvasViewport(DocSize documentSize, Viewport viewport)
    {
        DocumentSize = documentSize;
        Viewport = viewport;
    }

    /// <summary>本视图对应的文档尺寸，单位为文档像素。</summary>
    public DocSize DocumentSize { get; }

    /// <summary>契约层的视口参数（缩放与平移）。</summary>
    public Viewport Viewport { get; }

    /// <summary>缩放系数，屏幕点数 / 文档像素。恒落在
    /// [<see cref="MinPointsPerPixel"/>, <see cref="MaxPointsPerPixel"/>] 内。</summary>
    public double PointsPerPixel => Viewport.PointsPerPixel;

    /// <summary>构造一个未缩放、平移到原点的视口（1:1 且左上对齐）。</summary>
    /// <param name="documentSize">文档尺寸。</param>
    /// <returns>缩放为 1、原点为 (0, 0) 的视口。</returns>
    public static CanvasViewport Identity(DocSize documentSize) =>
        new(documentSize, new Viewport(0, 0, 1, 1));

    /// <summary>用给定的 <see cref="Viewport"/> 原样包一个视口，不做夹取。</summary>
    /// <param name="documentSize">文档尺寸。</param>
    /// <param name="viewport">契约层的视口参数。</param>
    /// <returns>包装后的视口。</returns>
    /// <remarks>
    /// 供「在已有视口上做缩放 / 平移」的场景使用。
    /// 调用方负责保证 <c>PointsPerPixel</c> 是有限正数，否则 <see cref="DocCoord"/> 会抛。
    /// </remarks>
    public static CanvasViewport FromViewport(DocSize documentSize, Viewport viewport) =>
        new(documentSize, viewport);

    /// <summary>
    /// 计算「把整张文档完整放进 <paramref name="bounds"/> 并居中」所需的视口。
    /// </summary>
    /// <param name="bounds">画布控件的可用矩形（控件局部坐标）。</param>
    /// <param name="documentSize">文档尺寸。</param>
    /// <param name="padding">四边留白，单位为屏幕点数。默认 24。</param>
    /// <returns>
    /// 视口。文档尺寸非正、或可用区域退化时返回 <see cref="Identity"/>，
    /// 而不是抛异常——渲染路径上抛异常会让整个窗口消失。
    /// </returns>
    public static CanvasViewport Fit(Rect bounds, DocSize documentSize, double padding = 24.0)
    {
        if (documentSize.Width <= 0 || documentSize.Height <= 0)
        {
            return Identity(documentSize);
        }

        double availW = bounds.Width - (2 * padding);
        double availH = bounds.Height - (2 * padding);
        if (availW <= 0 || availH <= 0)
        {
            // 控件还没布局完（尺寸为 0）或被压得比留白还小：给 1:1 原点对齐。
            return new CanvasViewport(documentSize, new Viewport(0, 0, 1, 1));
        }

        double scale = Math.Min(availW / documentSize.Width, availH / documentSize.Height);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return Identity(documentSize);
        }

        scale = Math.Clamp(scale, MinPointsPerPixel, MaxPointsPerPixel);

        // 居中：让文档在可用区域里左右上下都留等量的余量。
        double originX = bounds.X + padding + ((availW - (documentSize.Width * scale)) / 2.0);
        double originY = bounds.Y + padding + ((availH - (documentSize.Height * scale)) / 2.0);

        return new CanvasViewport(documentSize, new Viewport(originX, originY, scale, scale));
    }

    /// <summary>文档矩形 → 屏幕矩形，供绘图使用。</summary>
    /// <param name="document">文档坐标系中的矩形。</param>
    /// <returns>屏幕坐标系中的矩形。<b>不翻转 Y</b>：文档的 <c>Top</c> 一定映射到更小的 Y。</returns>
    public Rect ToScreen(DocRect document)
    {
        DocPoint topLeft = ToScreenPoint(document.Origin);
        ViewPoint bottomRight = DocCoord.ToView(
            new DocPoint(document.Right, document.Bottom), Viewport);

        return new Rect(
            topLeft.X,
            topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);
    }

    /// <summary>屏幕矩形 → 文档矩形。</summary>
    /// <param name="screen">屏幕坐标系中的矩形。</param>
    /// <returns>文档坐标系中的矩形。</returns>
    public DocRect ToDocument(Rect screen)
    {
        DocPoint topLeft = ToDocumentPoint(new Point(screen.X, screen.Y));
        DocPoint bottomRight = ToDocumentPoint(new Point(screen.Right, screen.Bottom));
        return new DocRect(topLeft, new DocSize(
            (int)Math.Round(bottomRight.X - topLeft.X),
            (int)Math.Round(bottomRight.Y - topLeft.Y)));
    }

    /// <summary>屏幕点 → 文档点。</summary>
    /// <param name="screen">屏幕坐标系中的点。</param>
    /// <returns>文档坐标系中的点，Y 向下。</returns>
    public DocPoint ToDocument(Point screen) =>
        ToDocumentPoint(new Point(screen.X, screen.Y));

    /// <summary>文档点 → 屏幕点。</summary>
    /// <param name="document">文档坐标系中的点。</param>
    /// <returns>屏幕坐标系中的点。</returns>
    public DocPoint ToScreenPoint(DocPoint document)
    {
        ViewPoint view = DocCoord.ToView(document, Viewport);
        return new DocPoint(view.X, view.Y);
    }

    /// <summary>屏幕点 → 文档点。</summary>
    private DocPoint ToDocumentPoint(Point screen)
    {
        DocPoint doc = DocCoord.FromView(new ViewPoint(screen.X, screen.Y), Viewport);
        return new DocPoint(doc.X, doc.Y);
    }
}