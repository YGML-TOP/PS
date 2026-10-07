using System.Collections.Generic;

namespace Compositor.UI;

/// <summary>
/// 视口：文档坐标系 ↔ 视图坐标系之间的映射参数。
/// </summary>
/// <remarks>
/// <para>🔴 <b>铁律 1：本类型不做任何 Y 翻转。</b>
/// 原项目 <c>EditorCanvas.swift:670</c> 有 <c>override var isFlipped: Bool { true }</c>，
/// AppKit 靠这一行把视图翻成 Y 向下，<c>CanvasViewport.swift:25-33</c> 的
/// <c>documentPoint(from:)</c> 才是纯线性变换、无 Y 取反。
/// Avalonia 的控件坐标系本就 Y 向下，所以这里的等价物是<b>什么都不做</b>。</para>
/// <para>原项目的四个覆盖层（<c>CanvasLinesOverlay.swift:8</c>、<c>TransformOverlay.swift:61</c>、
/// <c>BrushCursorOverlay.swift:15</c>、<c>SampleRingOverlay.swift:7</c>）都各自声明了
/// <c>isFlipped = true</c>，说明这是全局约定而非局部补丁。</para>
/// <para><b>禁止</b>：<c>ScaleTransform(1, -1)</c>、<c>height - y</c>、<c>(canvasH - y)</c>。</para>
/// </remarks>
public sealed record ViewportState
{
    /// <summary>视口在屏幕上的原点 X（视图点）。</summary>
    public double OriginX { get; init; }

    /// <summary>视口在屏幕上的原点 Y（视图点，Y 向下）。</summary>
    public double OriginY { get; init; }

    /// <summary>每个文档像素对应多少视图点。<b>必须为有限正数</b>。</summary>
    public double PointsPerPixel { get; init; } = 1.0;

    /// <summary>
    /// 缩放倍数，供渲染使用。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是 <see cref="PointsPerPixel"/> 的重复表示，<b>只用于渲染</b>。
    /// 坐标换算一律只用 <see cref="PointsPerPixel"/>。
    /// 契约测试 <c>tests/Compositor.Core.Tests/Contract/GeometryTests.cs:173-180</c>
    /// 专门锁住了这一点：误把 <c>Scale</c> 当除数会让同一份视口在不同调用点算出不同坐标，
    /// 而且因为"两处都能跑"极难发现。
    /// </remarks>
    public double Scale { get; init; } = 1.0;

    /// <summary>缩放范围，对应 <c>CanvasViewport.swift:11</c> 的 <c>zoomRange</c>。</summary>
    public const double MinZoom = 0.001;

    /// <summary>缩放上限，对应 <c>CanvasViewport.swift:11</c> 的 <c>zoomRange</c>。</summary>
    public const double MaxZoom = 32.0;

    /// <summary>
    /// 键盘缩放档位，对应 <c>CanvasViewport.swift:12-14</c> 的 <c>keyboardZoomLevels</c>。
    /// </summary>
    /// <remarks>顺序与原项目一致，共 17 档；1 倍在第 7 位（索引 6）。</remarks>
    public static IReadOnlyList<double> KeyboardZoomLevels { get; } = new[]
    {
        0.125, 1.0 / 6.0, 0.25, 1.0 / 3.0, 0.5, 2.0 / 3.0,
        1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 5.0, 6.0, 8.0, 12.0, 16.0,
    };

    /// <summary>
    /// 适应窗口时四周保留的边距（视图点），对应 <c>CanvasViewport.swift:37-38</c> 的 <c>- 96</c>。
    /// </summary>
    public const double FitMargin = 96.0;
}
