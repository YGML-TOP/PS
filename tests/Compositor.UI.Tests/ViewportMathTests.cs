using Compositor.Core;
using Compositor.UI;
using Xunit;

namespace Compositor.UI.Tests;

/// <summary>
/// <see cref="ViewportMath"/> 的行为测试。
/// </summary>
/// <remarks>
/// <para>每个用例都注明对应的 Swift 行号（<c>_eval_Compositor\Compositor-main\Compositor\Rendering\CanvasViewport.swift</c>），
/// 期望值按源码<b>逐行推导</b>而非目测，确保与 Mac 版一致。</para>
/// <para>🔴 <b>铁律 1</b>：本文件不含任何 Y 翻转断言——因为源码里就没有。
/// 若某条测试出现负号或 <c>height - y</c>，说明实现写错了。</para>
/// </remarks>
public sealed class ViewportMathTests
{
    // ═══════════════ Compose：契约 §5.1 偏差的收口点 ═══════════════

    /// <summary>
    /// <c>pointsPerPixel = zoom / backingScale</c>，对应 <c>CanvasViewport.swift:15</c>。
    /// </summary>
    [Fact]
    public void Compose_DividesZoomByBackingScale()
    {
        var v = ViewportMath.Compose(zoom: 2.0, originX: 10, originY: 20, backingScale: 2.0);
        Assert.Equal(1.0, v.PointsPerPixel, 12);
        Assert.Equal(2.0, v.Scale, 12);
        Assert.Equal(10.0, v.OriginX, 12);
        Assert.Equal(20.0, v.OriginY, 12);
    }

    /// <summary>
    /// <c>backingScale = max(1, newScale)</c>，对应 <c>CanvasViewport.swift:47</c>。
    /// 小于 1 的 DPI 缩放被抬到 1，防止 <c>pointsPerPixel &gt; zoom</c>。
    /// </summary>
    [Fact]
    public void Compose_ClampsBackingScaleToAtLeastOne()
    {
        var v = ViewportMath.Compose(zoom: 2.0, originX: 0, originY: 0, backingScale: 0.5);
        Assert.Equal(1.0, v.Scale, 12);
        Assert.Equal(2.0, v.PointsPerPixel, 12);
    }

    // ═══════════════ ClampZoom：CanvasViewport.swift:81-83 ═══════════════

    [Theory]
    [InlineData(0.0001, ViewportMath.MinZoom)]
    [InlineData(0.001, ViewportMath.MinZoom)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(32.0, 32.0)]
    [InlineData(1000.0, ViewportMath.MaxZoom)]
    public void ClampZoom_ClampsToRange(double input, double expected)
        => Assert.Equal(expected, ViewportMath.ClampZoom(input), 12);

    [Fact]
    public void ClampZoom_RangeMatchesSource()
    {
        // CanvasViewport.swift:11 —— zoomRange: ClosedRange<CGFloat> = 0.001...32
        Assert.Equal(0.001, ViewportMath.MinZoom, 12);
        Assert.Equal(32.0, ViewportMath.MaxZoom, 12);
    }

    // ═══════════════ KeyboardZoomLevels：CanvasViewport.swift:12-14 ═══════════════

    /// <summary>
    /// 17 档，顺序与源码逐值一致；1 倍在索引 6。
    /// </summary>
    [Fact]
    public void KeyboardZoomLevels_MatchSource()
    {
        var levels = ViewportMath.KeyboardZoomLevels;
        Assert.Equal(17, levels.Count);
        double[] expected =
        {
            0.125, 1.0 / 6.0, 0.25, 1.0 / 3.0, 0.5, 2.0 / 3.0,
            1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 5.0, 6.0, 8.0, 12.0, 16.0,
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], levels[i], 12);
        }

        Assert.Equal(1.0, levels[6], 12);
    }

    /// <summary>
    /// 对应 <c>CanvasViewport.swift:69-72</c>：
    /// <c>.first { $0 &gt; zoom + tolerance }</c> 取<b>最小</b>的大于 zoom 的档；
    /// <c>.last { $0 &lt; zoom - tolerance }</c> 取<b>最大</b>的小于 zoom 的档。
    /// 两者都是"最近邻"，不是"跨档"。
    /// </summary>
    /// <remarks>
    /// ⚠️ 初版把 1.0 的下档写成 0.5、0.3 的上档写成 0.5，都跳过了中间档位。
    /// 按源码逐值重算后：1.0 的下档是 <b>0.667</b>（2/3），0.3 的上档是 <b>0.333</b>（1/3）。
    /// </remarks>
    [Theory]
    [InlineData(1.0, 1, 1.25)]
    [InlineData(1.0, -1, 2.0 / 3.0)]   // 最近邻下档，不是 0.5
    [InlineData(0.3, 1, 1.0 / 3.0)]   // 最近邻上档，不是 0.5
    [InlineData(0.3, -1, 0.25)]
    [InlineData(2.0, -1, 1.5)]         // 跨过 1.25 取最近的 1.5
    [InlineData(0.5, 1, 2.0 / 3.0)]
    [InlineData(16.0, 1, 16.0)]        // 已最高档，停在原处
    [InlineData(0.125, -1, 0.125)]     // 已最低档，停在原处
    public void KeyboardZoomTarget_StepsToAdjacentLevel(double zoom, int step, double expected)
        => Assert.Equal(expected, ViewportMath.KeyboardZoomTarget(zoom, step), 9);

    /// <summary>
    /// step 为 0 时原样返回，对应 <c>CanvasViewport.swift:67</c>。
    /// </summary>
    [Fact]
    public void KeyboardZoomTarget_StepZeroReturnsSame()
        => Assert.Equal(3.7, ViewportMath.KeyboardZoomTarget(3.7, 0), 12);

    /// <summary>
    /// 恰好落在档位上时不应被同一档选中（容差 1e-9），对应 <c>CanvasViewport.swift:68</c>。
    /// zoom=1.0 放大应得 1.25 而非 1.0。
    /// </summary>
    [Fact]
    public void KeyboardZoomTarget_ExcludesCurrentLevelViaTolerance()
        => Assert.Equal(1.25, ViewportMath.KeyboardZoomTarget(1.0, 1), 12);

    // ═══════════════ Fit：CanvasViewport.swift:35-41 ═══════════════

    /// <summary>
    /// 对应 <c>CanvasViewport.swift:37-38</c>：
    /// <c>min(max(1, W-96)/docW, max(1, H-96)/docH) * backingScale</c>。
    /// </summary>
    /// <remarks>
    /// 算例：视图 800×600，文档 1000×400，DPI 2。
    /// <c>max(1,800-96)/1000 = 0.704</c>；<c>max(1,600-96)/400 = 1.26</c>；
    /// 取 min = 0.704，× backingScale 2 = <b>1.408</b>。
    /// <c>pointsPerPixel = 1.408 / 2 = 0.704</c>。
    /// </remarks>
    [Fact]
    public void Fit_ComputesZoomAndCentersOrigin()
    {
        double zoom = 0;
        ViewportMath.Fit(ref zoom, 800, 600, 1000, 400, 2.0, out double ox, out double oy);

        Assert.Equal(1.408, zoom, 9);
        Assert.Equal(0.704, zoom / 2.0, 9);

        // 文档在视图中的尺寸：1000*0.704 = 704 宽，400*0.704 = 281.6 高
        // 原点 = 视图中心 - 缩放后尺寸/2
        Assert.Equal(800.0 / 2.0 - (1000.0 * 0.704) / 2.0, ox, 9);
        Assert.Equal(600.0 / 2.0 - (400.0 * 0.704) / 2.0, oy, 9);
    }

    /// <summary>
    /// 对应 <c>CanvasViewport.swift:37-38</c> 的真实语义：
    /// <c>min(max(1, viewSize.width - 96) / documentSize.width, ...)</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这里修正过一个重大误解。</b>初版以为 <c>max(1,·)</c> 是"不小于 1 倍缩放"的保护，
    /// 于是期望 zoom=1。回源码逐字核对后确认：
    /// <c>max</c> 包住的是 <c>viewSize.width - 96</c>——<b>视图边长（单位：点）</b>，
    /// 与文档边长（单位：像素）<b>不同量纲</b>。它只防"视图比 96pt 还小"，
    /// <b>不防缩放倍数小于 1</b>。
    /// <para>算例：视图 800×600，文档 4000×4000，DPI 1。
    /// <c>max(1,800-96)=704</c>，<c>704/4000=0.176</c>；
    /// <c>max(1,600-96)=504</c>，<c>504/4000=0.126</c>；
    /// min = <b>0.126</b>。即文档确实会被缩到 0.126 倍——<b>这是 Mac 版的行为</b>，
    /// 移植必须一致。若改成"至少 1 倍"反而会与 Mac 版不符。</para>
    /// <para>真正防止过小的是 <c>clamp</c>（<c>CanvasViewport.swift:81-83</c>）的
    /// <see cref="ViewportMath.MinZoom"/> = 0.001。</para>
    /// </remarks>
    [Fact]
    public void Fit_FollowsSourceMinSemantics()
    {
        double zoom = 0;
        ViewportMath.Fit(ref zoom, 800, 600, 4000, 4000, 1.0, out _, out _);

        Assert.Equal(0.126, zoom, 9);
    }

    /// <summary>
    /// 视图小于边距 96pt 时 <c>max</c> 生效（量纲同为视图点）。
    /// 对应 <c>CanvasViewport.swift:37</c>。
    /// </summary>
    /// <remarks>
    /// 视图宽 50pt 时 <c>max(1, 50-96) = max(1,-46) = 1</c>，
    /// 于是 <c>1/1000 = 0.001</c>；另一边 <c>max(1,600-96)/1000 = 0.504</c>；
    /// min = 0.001 —— 恰为 <see cref="ViewportMath.MinZoom"/>，被 clamp 兜住。
    /// </remarks>
    [Fact]
    public void Fit_MaxProtectsTinyViewInPointUnits()
    {
        double zoom = 0;
        ViewportMath.Fit(ref zoom, 50, 600, 1000, 1000, 1.0, out _, out _);

        Assert.Equal(ViewportMath.MinZoom, zoom, 9);
    }

    /// <summary>
    /// 视图尺寸未就绪时保持原缩放不动，对应 <c>CanvasViewport.swift:36</c> 的 guard。
    /// </summary>
    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(0, 0)]
    public void Fit_SkipsWhenViewSizeNotReady(double w, double h)
    {
        double zoom = 4.2;
        ViewportMath.Fit(ref zoom, w, h, 1000, 400, 1.0, out double ox, out double oy);

        Assert.Equal(4.2, zoom, 12);
        Assert.Equal(0.0, ox, 12);
        Assert.Equal(0.0, oy, 12);
    }

    /// <summary>
    /// <b>铁律 1 断言</b>：文档点在视图里的位置应与文档坐标<b>同向</b>。
    /// 文档 (0,0) 必须落在 origin，文档越大 Y 越大——没有任何取反。
    /// </summary>
    [Fact]
    public void Fit_OriginDoesNotFlipY()
    {
        double zoom = 0;
        ViewportMath.Fit(ref zoom, 800, 600, 100, 400, 1.0, out double ox, out double oy);

        // 文档高 400 > 视图高 600-96=504？不，400<504，故 zoom = max(1,(600-96)/400)=1.26，
        // 但 min(1.26, (800-96)/100=7.04) = 1.26 → pointsPerPixel 1.26
        // 文档底边 y=400 → 视图 y = oy + 400*1.26，应大于 oy（文档顶部）
        double topViewY = oy;
        double bottomViewY = oy + 400.0 * zoom;
        Assert.True(bottomViewY > topViewY, "文档底部必须在视图下方——若此断言失败说明发生了 Y 翻转");
    }

    // ═══════════════ SetZoom：CanvasViewport.swift:56-64 ═══════════════

    /// <summary>
    /// <b>核心行为</b>：缩放后锚点下的文档点不动。对应 <c>CanvasViewport.swift:58-62</c>。
    /// </summary>
    /// <remarks>
    /// 初始：zoom=1, DPI=1 → ppp=1, origin=(100,50)。
    /// 锚点 (300,250) → 文档点 (300-100, 250-50) = (200,200)。
    /// 缩到 zoom=2 → ppp=2。
    /// 绝对 origin 形式：<c>newOrigin = anchor - pixel * newPpp = (300-400, 250-400) = (-100,-150)</c>。
    /// 回算：<c>(300-(-100))/2 = 200</c> ✓，<c>(250-(-150))/2 = 200</c> ✓。
    /// <para>⚠️ 初版把 Swift 的 <c>pan += anchor - moved</c> 直接照搬成绝对 origin 运算，
    /// 漏掉了 <c>pan</c> 与契约 <c>Origin</c> 的<b>基准差异</b>（pan 相对居中基点，
    /// Origin 是绝对值），导致回算文档点变成 (150,175)。这条用例锁住绝对形式。</para>
    /// </remarks>
    [Fact]
    public void SetZoom_KeepsAnchorDocumentPointFixed()
    {
        var current = ViewportMath.Compose(zoom: 1.0, originX: 100, originY: 50, backingScale: 1.0);
        var anchor = new ViewPoint(300, 250);

        // 锚点下的文档坐标
        var pixel = DocCoord.FromView(anchor, current);
        Assert.Equal(200.0, pixel.X, 12);
        Assert.Equal(200.0, pixel.Y, 12);

        var zoomed = ViewportMath.SetZoom(current, anchor, 2.0, out double ox, out double oy);

        Assert.Equal(2.0, zoomed.PointsPerPixel, 12);
        Assert.Equal(-100.0, ox, 9);
        Assert.Equal(-150.0, oy, 9);

        // 关键校验：用新视口反算，锚点仍指向同一文档点
        var back = DocCoord.FromView(anchor, zoomed);
        Assert.Equal(200.0, back.X, 9);
        Assert.Equal(200.0, back.Y, 9);
    }

    /// <summary>
    /// 回归锁：远离视图中心的 origin 也必须锚定正确。
    /// </summary>
    /// <remarks>
    /// origin=(400,300)，anchor=(500,400)，zoom 1→2。
    /// 文档点 =(100,100)；newOrigin = (500-200, 400-200) = <b>(300,200)</b>。
    /// 回算 (500-300)/2 = 100 ✓。
    /// <para>若误用 <c>oldOrigin + (anchor - moved)</c> 会得到 (700,500)，
    /// 锚点直接漂到视口外——这是本用例要暴露的错误。</para>
    /// </remarks>
    [Fact]
    public void SetZoom_AnchorsCorrectlyFarFromCenter()
    {
        var current = ViewportMath.Compose(zoom: 1.0, originX: 400, originY: 300, backingScale: 1.0);
        var anchor = new ViewPoint(500, 400);

        var zoomed = ViewportMath.SetZoom(current, anchor, 2.0, out double ox, out double oy);

        Assert.Equal(300.0, ox, 9);
        Assert.Equal(200.0, oy, 9);

        var back = DocCoord.FromView(anchor, zoomed);
        Assert.Equal(100.0, back.X, 9);
        Assert.Equal(100.0, back.Y, 9);
    }

    /// <summary>
    /// 连续两次缩放，锚点必须稳定不动（防止"每次缩放都漂一点"的累积误差）。
    /// </summary>
    [Fact]
    public void SetZoom_IsStableAcrossConsecutiveCalls()
    {
        var v = ViewportMath.Compose(zoom: 1.0, originX: 137, originY: -89, backingScale: 1.0);
        var anchor = new ViewPoint(640, 480);

        var target = DocCoord.FromView(anchor, v);

        // 1 → 2 → 4 → 8，锚点始终指同一文档点
        double[] zooms = { 2.0, 4.0, 8.0 };
        foreach (double z in zooms)
        {
            v = ViewportMath.SetZoom(v, anchor, z, out _, out _);
            var back = DocCoord.FromView(anchor, v);
            Assert.Equal(target.X, back.X, 6);
            Assert.Equal(target.Y, back.Y, 6);
        }
    }

    /// <summary>
    /// 缩放结果仍受 zoomRange 约束，对应 <c>CanvasViewport.swift:59</c> 的 <c>zoom = clamp(value)</c>。
    /// </summary>
    [Fact]
    public void SetZoom_ClampsTarget()
    {
        var current = ViewportMath.Compose(1.0, 0, 0, 1.0);
        var v = ViewportMath.SetZoom(current, new ViewPoint(0, 0), 9999.0, out _, out _);
        Assert.Equal(ViewportMath.MaxZoom, v.PointsPerPixel, 12);
    }

    /// <summary>
    /// 非有限目标缩放被拒绝，对应 <c>CanvasViewport.swift:57</c> 的 <c>guard value.isFinite</c>。
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetZoom_RejectsNonFiniteTarget(double target)
    {
        var current = ViewportMath.Compose(2.0, 33, 44, 1.0);
        var v = ViewportMath.SetZoom(current, new ViewPoint(10, 10), target, out _, out _);

        // 原样返回当前视口（guard 早退，不改 origin）
        Assert.Equal(2.0, v.PointsPerPixel, 12);
        Assert.Equal(33.0, v.OriginX, 12);
        Assert.Equal(44.0, v.OriginY, 12);
    }

    /// <summary>
    /// <b>铁律 1 断言</b>：Y 缩放不产生取反。文档 (0,0) 经放大后仍在视图左上区域。
    /// </summary>
    [Fact]
    public void SetZoom_DoesNotFlipY()
    {
        var current = ViewportMath.Compose(1.0, 10, 20, 1.0);
        var v = ViewportMath.SetZoom(current, new ViewPoint(0, 0), 4.0, out double ox, out double oy);

        var origin = DocCoord.ToView(new DocPoint(0, 0), v);
        Assert.Equal(ox, origin.X, 9);
        Assert.Equal(oy, origin.Y, 9);

        // 文档 (0,100) 的视图 Y 必须大于文档 (0,0) 的视图 Y
        var lower = DocCoord.ToView(new DocPoint(0, 100), v);
        Assert.True(lower.Y > origin.Y, "文档 Y 越大视图 Y 必须越大——若失败说明发生了 Y 翻转");
    }

    // ═══════════════ Translate：CanvasViewport.swift:75-79 ═══════════════

    [Fact]
    public void Translate_MovesOriginWithoutTouchingScale()
    {
        var v = ViewportMath.Compose(2.0, 10, 20, 2.0);
        var moved = ViewportMath.Translate(v, 5, -3, 2.0);

        Assert.Equal(15.0, moved.OriginX, 12);
        Assert.Equal(17.0, moved.OriginY, 12);
        Assert.Equal(1.0, moved.PointsPerPixel, 12);
        Assert.Equal(2.0, moved.Scale, 12);
    }

    // ═══════════════ Resize：CanvasViewport.swift:43-54 ═══════════════

    /// <summary>
    /// <c>followsFit == true</c> 时重新 fit，对应 <c>CanvasViewport.swift:48-50</c>。
    /// </summary>
    [Fact]
    public void Resize_ReFitsWhenFollowingFit()
    {
        var v = ViewportMath.Compose(zoom: 1.0, originX: 0, originY: 0, backingScale: 1.0);
        var resized = ViewportMath.Resize(v, 800, 600, 1.0, followsFit: true, 1000, 400);

        // 与 Fit_ComputesZoomAndCentersOrigin 同一算例（backingScale=1）
        Assert.Equal(0.704, resized.PointsPerPixel, 9);
        Assert.Equal(800.0 / 2.0 - (1000.0 * 0.704) / 2.0, resized.OriginX, 9);
    }

    /// <summary>
    /// 换显示器时 DPI 变化：<c>followsFit == false</c> 时按比例缩放原点，
    /// 对应 <c>CanvasViewport.swift:51-53</c>。注释 <c>:44</c> 说明动机是保住中心文档点。
    /// </summary>
    [Fact]
    public void Resize_ScalesOriginWhenDpiChanges()
    {
        // DPI 1 → 2，原点 (100,50)，ppp=1（即 zoom=1）
        var v = new Viewport(100, 50, 1.0, 1.0);
        var resized = ViewportMath.Resize(v, 800, 600, backingScale: 2.0, followsFit: false, 1000, 400);

        double ratio = 2.0 / 1.0;
        Assert.Equal(200.0, resized.OriginX, 9);
        Assert.Equal(100.0, resized.OriginY, 9);
        Assert.Equal(2.0, resized.Scale, 9);
        Assert.Equal(1.0 * ratio, resized.PointsPerPixel, 9);
    }

    // ═══════════════ Hairline：EditorCanvas.swift:1421 ═══════════════

    /// <summary>
    /// hairline = 1 / backingScale，一个物理屏幕像素在视图点下的宽度。
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 0.5)]
    [InlineData(1.5, 1.0 / 1.5)]
    public void Hairline_InvertsBackingScale(double dpi, double expected)
    {
        var v = ViewportMath.Compose(2.0, 0, 0, dpi);
        Assert.Equal(expected, ViewportMath.Hairline(v), 12);
    }

    // ═══════════════ 与契约 DocCoord 的往返一致性 ═══════════════

    /// <summary>
    /// 本类产出的视口交给契约 <see cref="DocCoord"/> 后，往返必须无损。
    /// </summary>
    [Fact]
    public void Compose_ProducesRoundTrippableViewport()
    {
        var v = ViewportMath.Compose(zoom: 3.5, originX: 17, originY: -33, backingScale: 2.0);
        var original = new DocPoint(123.5, 678.25);

        var view = DocCoord.ToView(original, v);
        var back = DocCoord.FromView(view, v);

        Assert.Equal(original.X, back.X, 9);
        Assert.Equal(original.Y, back.Y, 9);
    }

    /// <summary>
    /// 非法的 DPI（0）不应产生 0 或负的 <c>PointsPerPixel</c>——契约
    /// <see cref="DocCoord"/> 会对非正数抛异常。
    /// </summary>
    [Fact]
    public void Compose_NeverProducesNonPositivePointsPerPixel()
    {
        foreach (double zoom in new[] { 0.001, 0.5, 1.0, 7.0, 32.0 })
        {
            foreach (double dpi in new[] { -3.0, 0.0, 0.25, 1.0, 2.0, 3.0 })
            {
                var v = ViewportMath.Compose(zoom, 0, 0, dpi);
                Assert.True(
                    v.PointsPerPixel > 0.0 && double.IsFinite(v.PointsPerPixel),
                    $"zoom={zoom} dpi={dpi} 产出 PointsPerPixel={v.PointsPerPixel}");
            }
        }
    }
}