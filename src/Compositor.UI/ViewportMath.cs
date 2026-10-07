using Compositor.Core;

namespace Compositor.UI;

/// <summary>
/// 视口的<b>纯静态求解辅助</b>：消费契约 <see cref="Viewport"/>，返回新的 <see cref="Viewport"/>。
/// </summary>
/// <remarks>
/// <para><b>本类不是状态类型</b>，也不持有任何字段。
/// 权威类型始终是契约的 <see cref="Viewport"/>（见 <c>docs/画布交互拆解.md</c> §5.1）；
/// 早期版本里的 UI 自有 <c>ViewportState</c> 已按总项目管理 v1.2 裁决删除，
/// 以免出现两套视口类型各自演化。</para>
/// <para><b>🔴 契约偏差补偿</b>：契约的 <c>Viewport</c> 只存
/// <c>PointsPerPixel</c>（= <c>zoom / backingScale</c> 的<b>快照</b>，见
/// <c>CanvasViewport.swift:15</c>）与 <c>Scale</c>（= <c>backingScale</c>，见 <c>:7</c>），
/// <b>没有单独的 <c>zoom</c> 字段</b>。而 <c>fit</c> / <c>resize</c> / <c>setZoom</c>
/// 都以 <c>zoom</c> 为自变量。因此本类的每个方法都<b>显式接收 zoom 参数</b>，
/// 由调用方持有（UI 层的 <c>CanvasSurface</c>）。<b>不修改契约。</b></para>
/// <para><b>铁律 1</b>：本类所有计算<b>不含任何 Y 翻转</b>。
/// 原项目 <c>EditorCanvas.swift:670</c> 用 <c>override var isFlipped: Bool { true }</c>
/// 把 AppKit 视图翻成 Y 向下，<c>CanvasViewport.swift:25-33</c> 才可以做纯线性变换。
/// Avalonia 的控件坐标系本就 Y 向下，<b>等价物是什么都不做</b>。</para>
/// </remarks>
public static class ViewportMath
{
    /// <summary>
    /// 缩放范围下界，对应 <c>CanvasViewport.swift:11</c> 的 <c>zoomRange.lowerBound</c>（0.001）。
    /// </summary>
    /// <remarks>按 v1.2 裁决，<c>zoomRange</c> <b>不进 Core</b>，留在 UI 层。</remarks>
    public const double MinZoom = 0.001;

    /// <summary>
    /// 缩放范围上界，对应 <c>CanvasViewport.swift:11</c> 的 <c>zoomRange.upperBound</c>（32）。
    /// </summary>
    public const double MaxZoom = 32.0;

    /// <summary>
    /// 适应窗口时四周保留的边距（视图点），对应 <c>CanvasViewport.swift:37-38</c> 的 <c>- 96</c>。
    /// </summary>
    public const double FitMargin = 96.0;

    /// <summary>
    /// 键盘缩放档位，对应 <c>CanvasViewport.swift:12-14</c> 的 <c>keyboardZoomLevels</c>。
    /// </summary>
    /// <remarks>
    /// 逐值照抄，顺序不变，共 17 档；1 倍在索引 6。
    /// 原项目的 <c>keyboardZoomTarget(by:)</c>（<c>:66-73</c>）按此表在档位间跳，
    /// 容差为 <c>1e-9</c>。
    /// </remarks>
    public static IReadOnlyList<double> KeyboardZoomLevels { get; } = new[]
    {
        0.125, 1.0 / 6.0, 0.25, 1.0 / 3.0, 0.5, 2.0 / 3.0,
        1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 5.0, 6.0, 8.0, 12.0, 16.0,
    };

    /// <summary>
    /// 由 <c>zoom</c> 与 <c>backingScale</c> 合成契约 <see cref="Viewport"/>。
    /// </summary>
    /// <param name="zoom">用户缩放级别（对应 <c>CanvasViewport.swift:8</c> 的 <c>zoom</c>）。</param>
    /// <param name="originX">视口原点 X（视图点）。</param>
    /// <param name="originY">视口原点 Y（视图点）。</param>
    /// <param name="backingScale">
    /// DPI 缩放（对应 <c>CanvasViewport.swift:7</c>）。契约把它叫 <c>Scale</c>。
    /// </param>
    /// <returns>
    /// <c>PointsPerPixel = zoom / backingScale</c>（<c>CanvasViewport.swift:15</c> 的派生式）。
    /// </returns>
    /// <remarks>
    /// 这是契约 §5.1 偏差的<b>唯一收口点</b>：全项目只在这一处做除法。
    /// </remarks>
    public static Viewport Compose(double zoom, double originX, double originY, double backingScale)
    {
        double scale = Math.Max(1.0, backingScale);
        return new Viewport(originX, originY, zoom / scale, scale);
    }

    /// <summary>
    /// 把 <paramref name="viewport"/> 的缩放钳制到 <see cref="MinZoom"/>..<see cref="MaxZoom"/>。
    /// </summary>
    /// <param name="viewport">待钳制的视口。</param>
    /// <param name="backingScale">
    /// DPI 缩放。契约 <see cref="Viewport"/> 不存 <c>zoom</c>，故需外部提供以还原 <c>zoom</c>。
    /// </param>
    /// <returns>缩放被钳制、原点不变的新视口。</returns>
    /// <remarks>对应 <c>CanvasViewport.swift:81-83</c> 的 <c>clamp(_:)</c>。</remarks>
    public static Viewport Clamp(Viewport viewport, double backingScale)
    {
        double scale = Math.Max(1.0, backingScale);
        double zoom = ClampZoom(viewport.PointsPerPixel * scale);
        return new Viewport(viewport.OriginX, viewport.OriginY, zoom / scale, scale);
    }

    /// <summary>
    /// 钳制缩放级别本身。对应 <c>CanvasViewport.swift:81-83</c> 的 <c>clamp(_:)</c>。
    /// </summary>
    /// <param name="zoom">待钳制的缩放级别。</param>
    /// <returns>落在 <see cref="MinZoom"/>..<see cref="MaxZoom"/> 内的值。</returns>
    public static double ClampZoom(double zoom)
        => Math.Min(MaxZoom, Math.Max(MinZoom, zoom));

    /// <summary>
    /// 适应窗口：算出文档居中铺满（留 <see cref="FitMargin"/> 边距）时的视口原点与缩放。
    /// </summary>
    /// <param name="zoom">计算结果；调用方应取返回值覆盖它。</param>
    /// <param name="viewSizeX">视图宽度（点）。</param>
    /// <param name="viewSizeY">视图高度（点）。</param>
    /// <param name="documentWidth">文档宽度（像素）。</param>
    /// <param name="documentHeight">文档高度（像素）。</param>
    /// <param name="backingScale">DPI 缩放（<c>CanvasViewport.swift:7</c>）。</param>
    /// <param name="originX">结果原点 X。</param>
    /// <param name="originY">结果原点 Y。</param>
    /// <remarks>
    /// 对应 <c>CanvasViewport.swift:35-41</c> 的 <c>fit(documentSize:)</c>：
    /// <list type="bullet">
    /// <item><c>max(1, viewSize.width - 96)</c> / <c>documentSize.width</c>（<c>:37</c>）——
    /// ⚠️ <c>max</c> 包住的是<b>视图尺寸减边距</b>，<b>不是除法结果</b>。
    /// 写成 <c>max(1, (W-96)/docW)</c> 会让文档远大于视图时缩到 0.126 而非 1，
    /// 画布会小到看不见。这是本方法最初的 bug。</item>
    /// <item><c>clamp(...)</c>（<c>:38</c>）—— 结果仍受 <see cref="MinZoom"/>..<see cref="MaxZoom"/> 约束。</item>
    /// <item><c>* backingScale</c>（<c>:38</c>）—— 注意乘的是 backingScale，得回 zoom。</item>
    /// <item><c>pan = .zero</c>（<c>:39</c>）—— 即原点回到视图中心，见返回的 origin。</item>
    /// </list>
    /// </remarks>
    public static void Fit(
        ref double zoom,
        double viewSizeX,
        double viewSizeY,
        double documentWidth,
        double documentHeight,
        double backingScale,
        out double originX,
        out double originY)
    {
        originX = 0.0;
        originY = 0.0;

        if (viewSizeX <= 0.0 || viewSizeY <= 0.0)
        {
            // 对应 CanvasViewport.swift:36 —— 视图尺寸未知，保持 followsFit 并放弃本次计算。
            return;
        }

        if (documentWidth <= 0.0 || documentHeight <= 0.0)
        {
            // 契约 DocRect 的不变量要求 Size 为正；此处 defensive，避免除零产生 Infinity。
            return;
        }

        double fitted = Math.Min(
            Math.Max(1.0, viewSizeX - FitMargin) / documentWidth,
            Math.Max(1.0, viewSizeY - FitMargin) / documentHeight);

        double scale = Math.Max(1.0, backingScale);
        double newZoom = ClampZoom(fitted * scale);
        zoom = newZoom;

        double pointsPerPixel = newZoom / scale;
        // 对应 CanvasViewport.swift:20-22 的居中公式，pan 归零即 origin = center - scaled/2。
        // 无 Y 翻转（铁律 1）。
        double scaledWidth = documentWidth * pointsPerPixel;
        double scaledHeight = documentHeight * pointsPerPixel;
        originX = viewSizeX / 2.0 - scaledWidth / 2.0;
        originY = viewSizeY / 2.0 - scaledHeight / 2.0;
    }

    /// <summary>
    /// 按 <paramref name="targetZoom"/> 缩放，并锚定在 <paramref name="anchor"/> 上。
    /// </summary>
    /// <param name="viewport">当前视口。</param>
    /// <param name="anchor">
    /// 锚点（<b>视图坐标</b>，Y 向下），通常取指针位置。原样传给契约
    /// <see cref="DocCoord.ToView"/> / <see cref="DocCoord.FromView"/>。
    /// </param>
    /// <param name="targetZoom">目标缩放级别。非有限值时原样返回当前视口。</param>
    /// <param name="resultOriginX">结果原点 X。</param>
    /// <param name="resultOriginY">结果原点 Y。</param>
    /// <returns>缩放后、锚点已补偿的新视口。</returns>
    /// <remarks>
    /// 对应 <c>CanvasViewport.swift:56-64</c> 的 <c>setZoom(_:anchoredAt:documentSize:)</c>。
    /// <para><b>结果：缩放后光标下的文档点不动。</b>这是所有画布程序的必备行为。</para>
    /// <para><c>followsFit = false</c>（<c>:63</c>）由调用方记录，本类不持有状态。</para>
    /// <para><b>🔴 坐标系基准陷阱（本方法最初的 bug）</b>：
    /// Swift 的 <c>pan</c> 是相对<b>居中基点</b>的偏移——
    /// <c>documentRect</c>（<c>:20-21</c>）用 <c>center - scaled/2 + pan</c> 合成 origin；
    /// <c>setZoom</c>（<c>:61-62</c>）做的是 <c>pan += anchor - moved</c>。
    /// 而契约 <see cref="Viewport.OriginX"/> 存的是<b>已合成的绝对 origin</b>，
    /// 基准与 <c>pan</c> 不同，<b>不能直接照搬 <c>pan +=</c> 的增量形式</b>。</para>
    /// <para>正确的绝对形式（本实现采用）：
    /// <code>
    /// newOrigin = anchor - pixel * newPointsPerPixel
    /// </code>
    /// 其中 <c>pixel</c> 是锚点当前的文档坐标。它等价于 Swift 的
    /// <c>center - scaled/2 + pan</c> 求解结果，但绕开了基准换算。</para>
    /// </remarks>
    public static Viewport SetZoom(
        Viewport viewport,
        ViewPoint anchor,
        double targetZoom,
        out double resultOriginX,
        out double resultOriginY)
    {
        if (!double.IsFinite(targetZoom))
        {
            // 对应 CanvasViewport.swift:57 —— guard value.isFinite else { return }
            resultOriginX = viewport.OriginX;
            resultOriginY = viewport.OriginY;
            return viewport;
        }

        // 锚点当前的文档坐标（CanvasViewport.swift:58）。
        var pixel = DocCoord.FromView(anchor, viewport);

        double newZoom = ClampZoom(targetZoom);
        double newScale = Math.Max(1.0, viewport.Scale);
        double newPointsPerPixel = newZoom / newScale;

        // 绝对 origin 形式：令 anchor 反算后仍指向 pixel（见 remarks 的基准陷阱说明）。
        resultOriginX = anchor.X - (pixel.X * newPointsPerPixel);
        resultOriginY = anchor.Y - (pixel.Y * newPointsPerPixel);
        return new Viewport(resultOriginX, resultOriginY, newPointsPerPixel, newScale);
    }

    /// <summary>
    /// 按 <paramref name="targetZoom"/> 缩放并锚定；便捷重载，假定当前 origin 为 0。
    /// </summary>
    /// <param name="zoom">当前缩放级别。</param>
    /// <param name="anchor">锚点（视图坐标）。</param>
    /// <param name="targetZoom">目标缩放级别。</param>
    /// <param name="backingScale">DPI 缩放。</param>
    /// <param name="originX">结果原点 X。</param>
    /// <param name="originY">结果原点 Y。</param>
    /// <returns>缩放后的新视口。</returns>
    public static Viewport SetZoom(
        double zoom,
        ViewPoint anchor,
        double targetZoom,
        double backingScale,
        out double originX,
        out double originY)
        => SetZoom(Compose(zoom, 0.0, 0.0, backingScale), anchor, targetZoom, out originX, out originY);

    /// <summary>
    /// 平移视口（拖动画布）。
    /// </summary>
    /// <param name="viewport">当前视口。</param>
    /// <param name="deltaX">视图坐标下的 X 位移。</param>
    /// <param name="deltaY">视图坐标下的 Y 位移。<b>无 Y 翻转</b>（铁律 1）。</param>
    /// <param name="backingScale">DPI 缩放，用于保持 <c>Scale</c> 一致。</param>
    /// <returns>原点已平移的新视口。</returns>
    /// <remarks>对应 <c>CanvasViewport.swift:75-79</c> 的 <c>translate(by:)</c>。</remarks>
    public static Viewport Translate(Viewport viewport, double deltaX, double deltaY, double backingScale)
        => new(viewport.OriginX + deltaX, viewport.OriginY + deltaY, viewport.PointsPerPixel,
            Math.Max(1.0, backingScale));

    /// <summary>
    /// 视图尺寸变化后重算原点（用于 <c>layout</c> / 换显示器）。
    /// </summary>
    /// <param name="viewport">当前视口。</param>
    /// <param name="viewSizeX">新视图宽度（点）。</param>
    /// <param name="viewSizeY">新视图高度（点）。</param>
    /// <param name="backingScale">新 DPI 缩放。</param>
    /// <param name="followsFit">
    /// 是否处于"跟随适应窗口"状态（对应 <c>CanvasViewport.swift:10</c> 的 <c>followsFit</c>）。
    /// </param>
    /// <param name="documentWidth">文档宽度，用于 <c>followsFit</c> 时重新 fit。</param>
    /// <param name="documentHeight">文档高度。</param>
    /// <returns>重算后的新视口。</returns>
    /// <remarks>
    /// 对应 <c>CanvasViewport.swift:43-54</c> 的 <c>resize(to:backingScale:documentSize:)</c>：
    /// <list type="bullet">
    /// <item><c>followsFit</c> 时重新 <c>fit</c>（<c>:48-50</c>）。</item>
    /// <item>否则按缩放比例缩放 <c>pan</c>（<c>:51-53</c>），以保住中心文档点
    /// （注释 <c>:44</c>："Preserve the center document point when moving between displays"）。</item>
    /// </list>
    /// </remarks>
    public static Viewport Resize(
        Viewport viewport,
        double viewSizeX,
        double viewSizeY,
        double backingScale,
        bool followsFit,
        double documentWidth,
        double documentHeight)
    {
        double scale = Math.Max(1.0, backingScale);
        double oldPointsPerPixel = viewport.PointsPerPixel;

        if (followsFit)
        {
            double zoom = oldPointsPerPixel * scale;
            Fit(ref zoom, viewSizeX, viewSizeY, documentWidth, documentHeight, scale,
                out double fitX, out double fitY);
            return Compose(zoom, fitX, fitY, scale);
        }

        // CanvasViewport.swift:51-53 —— pan 按 ratio 缩放。
        // 🔴 同 SetZoom 的基准陷阱：Swift 缩的是 pan（相对居中基点），
        // 契约存的是绝对 origin，绝对 origin 也要按同一 ratio 缩放，
        // 这样"中心文档点不动"（注释 :44 的设计意图）才成立。
        double ratio = scale / viewport.Scale;
        double newPointsPerPixel = oldPointsPerPixel * ratio;
        return new Viewport(
            viewport.OriginX * ratio,
            viewport.OriginY * ratio,
            newPointsPerPixel,
            scale);
    }

    /// <summary>
    /// 按键盘档位求下一/上一缩放级别。
    /// </summary>
    /// <param name="zoom">当前缩放级别。</param>
    /// <param name="step">正数放大，负数缩小，0 表示不变。</param>
    /// <returns>目标缩放级别；<paramref name="step"/> 为 0 时原样返回。</returns>
    /// <remarks>
    /// 对应 <c>CanvasViewport.swift:66-73</c> 的 <c>keyboardZoomTarget(by:)</c>。
    /// 容差取 <c>max(1e-9, |zoom| * 1e-9)</c>，与原实现（<c>:68</c>）一致。
    /// </remarks>
    public static double KeyboardZoomTarget(double zoom, int step)
    {
        if (step == 0)
        {
            return zoom;
        }

        double tolerance = Math.Max(0.000000001, Math.Abs(zoom) * 0.000000001);

        if (step > 0)
        {
            foreach (double level in KeyboardZoomLevels)
            {
                if (level > zoom + tolerance)
                {
                    return level;
                }
            }

            return zoom;
        }

        for (int i = KeyboardZoomLevels.Count - 1; i >= 0; i--)
        {
            double level = KeyboardZoomLevels[i];
            if (level < zoom - tolerance)
            {
                return level;
            }
        }

        return zoom;
    }

    /// <summary>
    /// 一个视图单位的物理像素宽度（hairline 线宽）。
    /// </summary>
    /// <param name="viewport">视口。</param>
    /// <returns><c>1 / backingScale</c>，即一个物理屏幕像素在视图点下的宽度。</returns>
    /// <remarks>
    /// 对应 <c>EditorCanvas.swift:1421</c> 的 <c>let hairline = 1 / viewport.backingScale</c>
    /// 与 <c>TransformOverlay.swift:200</c> 的 <c>1 / max(session.viewport.backingScale, 1)</c>。
    /// 像素网格与覆盖层线条都必须用它，否则在 150% DPI 上线会粗一倍。
    /// </remarks>
    public static double Hairline(Viewport viewport)
        => 1.0 / Math.Max(viewport.Scale, 1.0);
}