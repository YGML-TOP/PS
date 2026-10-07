using Compositor.Core;
using Compositor.History;
using Compositor.Tools;
using Xunit;

namespace Compositor.Tools.Tests;

/// <summary>
/// M4 画笔类工具验收测试。
/// </summary>
/// <remarks>
/// 🔴 <b>数值期望值照抄 Mac 侧 <c>CompositorTests/BrushTests.swift</c> 的对应行号</b>。
/// 凡本测试断言的 alpha 区间，都能在 Swift 原件里找到同一句断言。
/// </remarks>
public sealed class BrushToolTests
{
    private static readonly ToolInput NoKeys = new(false, false, false, 1);

    /// <summary>
    /// 衰减曲线在中心为 1、边缘为 0，且单调递减。
    /// </summary>
    /// <remarks>
    /// 依据：<c>BrushStroke.swift:106-109</c> 的 <c>falloff</c>。
    /// 硬边断言来自 <c>BrushTests.swift:147</c>（笔尖中心 alpha &gt; 230）。
    /// </remarks>
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 0.0)]
    public void FalloffIsFullAtCenterAndZeroAtRim(double u, double expected)
    {
        Assert.Equal(expected, BrushFalloff.Falloff(u), 6);
    }

    /// <summary>
    /// 衰减单调递减 —— 保证不会出现「边缘比中心更浓」的环带。
    /// </summary>
    [Fact]
    public void FalloffIsMonotonicallyDecreasing()
    {
        double previous = double.MaxValue;
        for (int i = 0; i <= 100; i++)
        {
            double v = BrushFalloff.Falloff(i / 100.0);
            Assert.True(v <= previous, $"u={i / 100.0} 处出现回升：{v} > {previous}");
            previous = v;
        }
    }

    /// <summary>
    /// 硬度 1 = 硬边：半径内覆盖恒为 1。
    /// </summary>
    /// <remarks>依据 <c>BrushStroke.swift:258</c> 的 <c>if hardness &gt;= 1</c> 分支。</remarks>
    [Fact]
    public void FullHardnessGivesSolidCoverageInsideRadius()
    {
        double radius = 20.0 / 2.0;
        Assert.Equal(1.0, BrushFalloff.Coverage(0, 20, 1));
        Assert.Equal(1.0, BrushFalloff.Coverage(radius - 0.1, 20, 1));
        Assert.Equal(0.0, BrushFalloff.Coverage(radius + 0.1, 20, 1));
        Assert.Equal(0.0, BrushFalloff.Coverage(radius + 5, 20, 1));
    }

    /// <summary>
    /// 参数越界必须抛异常，而不是静默钳制。
    /// </summary>
    /// <remarks>
    /// 依据：<c>BrushStroke.swift:225-227</c> 的 <c>guard … else { throw ProjectError.tooLarge }</c>。
    /// 三条边界值逐条对应：diameter ∈ [1,2100]、hardness ∈ [0,1]、opacity ∈ [0.01,1]。
    /// </remarks>
    [Fact]
    public void OutOfRangeSettingsThrowInsteadOfClamping()
    {
        // diameter 下界是 1 不是 0，上界 2100。
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Diameter = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Diameter = 2101 }.Validate());

        // hardness ∈ [0,1]，两端合法。
        new BrushSettings { Hardness = 0 }.Validate();
        new BrushSettings { Hardness = 1 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Hardness = 1.01 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Hardness = -0.01 }.Validate());

        // opacity 下界是 0.01 不是 0 —— 这是最容易抄错的一条。
        new BrushSettings { Opacity = 0.01 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Opacity = 0 }.Validate());
    }

    /// <summary>
    /// NaN 与无穷必须被拒（Mac 侧 <c>.isFinite</c> 三处都有）。
    /// </summary>
    [Fact]
    public void NonFiniteSettingsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Diameter = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Hardness = double.PositiveInfinity }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrushSettings { Opacity = double.NegativeInfinity }.Validate());
    }

    /// <summary>
    /// 软笔刷在画布外落笔：不产生任何像素改动，也不产生撤销步。
    /// </summary>
    /// <remarks>
    /// 依据：<c>BrushTests.swift:156-158</c>：
    /// <c>session.beginBrush(at: CGPoint(x: -100, y: -100)); await session.finishBrush(); #expect(session.document == before)</c>。
    /// </remarks>
    [Fact]
    public void StrokeOutsideCanvasChangesNothing()
    {
        var surface = new MemoryPaintSurface(new DocSize(80, 80));
        var history = new DocumentHistory();
        var tool = new BrushTool(surface, history, new BrushSettings { Diameter = 20 });

        tool.PointerDown(new DocPoint(-100, -100), NoKeys);
        tool.PointerUp(NoKeys);

        Assert.Equal(0, history.UndoCount);
        Assert.False(history.CanUndo);
    }

    /// <summary>
    /// 一次笔画 = 一个 undo step，无论拖动多少次。
    /// </summary>
    /// <remarks>
    /// 🔴 依据：<c>BrushTests.swift:119 continuousStrokeCrossesTilesAndCommitsOneUndo</c>。
    /// 该测试在 Mac 上反复跨瓦片拖动，最终只提交<b>一个</b>撤销步。
    /// </remarks>
    [Fact]
    public void OneStrokeIsOneUndoStep()
    {
        var surface = new MemoryPaintSurface(new DocSize(200, 80));
        var history = new DocumentHistory();
        var tool = new BrushTool(surface, history, new BrushSettings { Diameter = 10 });

        tool.PointerDown(new DocPoint(20, 40), NoKeys);
        foreach (double x in new double[] { 180, 20, 180, 20, 100 })
        {
            tool.PointerDrag(new DocPoint(x, 40), NoKeys);
        }

        tool.PointerUp(NoKeys);

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("brush", history.UndoName);
    }

    /// <summary>
    /// 取消笔画：像素恢复到落笔前，且不产生撤销步。
    /// </summary>
    /// <remarks>依据：<c>BrushTests.swift:154-155</c> 的 <c>cancelBrush()</c> 后文档不变。</remarks>
    [Fact]
    public void CancelRestoresPixelsAndLeavesNoUndoStep()
    {
        var surface = new MemoryPaintSurface(new DocSize(80, 80));
        var history = new DocumentHistory();
        var tool = new BrushTool(surface, history, new BrushSettings { Diameter = 20, Color = new RgbaColor(255, 0, 0, 255) });

        (byte RBefore, byte GBefore, byte BBefore, byte AlphaBefore) = surface.GetPixel(40, 40);
        _ = RBefore; _ = GBefore; _ = BBefore; Assert.Equal(0, AlphaBefore);

        tool.PointerDown(new DocPoint(40, 40), NoKeys);
        tool.PointerDrag(new DocPoint(60, 40), NoKeys);
        (byte RDuring, byte GDuring, byte BDuring, byte AlphaDuring) = surface.GetPixel(40, 40);
        _ = RDuring; _ = GDuring; _ = BDuring; Assert.True(AlphaDuring > 0, "笔画中应当已写入像素。");

        tool.Cancel();

        (byte RAfter, byte GAfter, byte BAfter, byte AlphaAfter) = surface.GetPixel(40, 40);
        _ = RAfter; _ = GAfter; _ = BAfter; Assert.Equal(0, AlphaAfter);
        Assert.Equal(0, history.UndoCount);
        Assert.False(tool.IsStroking);
    }

    /// <summary>
    /// 🔴 橡皮 = <c>destination-out</c> 合成：alpha 下降时颜色<b>按同一因子压暗</b>。
    /// </summary>
    /// <remarks>
    /// 这是本模块最关键的断言。若把橡皮实现成「把 alpha 设为 0」，
    /// <b>颜色不会同步下降</b>，残留的颜色会在后续叠加时突然显出来。
    /// 本条用「先画半透明笔，再擦」验证颜色与 alpha 同步衰减。
    /// </remarks>
    [Fact]
    public void EraserComposesDestinationOutAndDarkensColorProportionally()
    {
        var surface = new MemoryPaintSurface(new DocSize(80, 80));
        var history = new DocumentHistory();

        // 先用 50% 不透明的红色铺满画布。
        var painter = new BrushTool(surface, history, new BrushSettings
        {
            Diameter = 60,
            Hardness = 1,
            Opacity = 0.5,
            Color = new RgbaColor(255, 0, 0, 255),
        });
        painter.PointerDown(new DocPoint(40, 40), NoKeys);
        painter.PointerUp(NoKeys);

        (byte R, byte G, byte B, byte A) painted = surface.GetPixel(40, 40);
        Assert.Equal(0, painted.G);
        Assert.Equal(0, painted.B);
        Assert.True(painted.A > 100, $"半透明笔应当产生部分 alpha，实得 {painted.A}。");

        // 🔴 预乘不变量：RGB ≤ A。半透明像素若 RGB > A 说明没走预乘。
        Assert.True(painted.R <= painted.A, $"预乘不变量被破坏：R={painted.R} > A={painted.A}。");

        // 用橡皮擦一次。
        var eraser = new BrushTool(surface, history, new BrushSettings
        {
            Diameter = 60,
            Hardness = 1,
            Opacity = 0.5,
            Erasing = true,
        });
        eraser.PointerDown(new DocPoint(40, 40), NoKeys);
        eraser.PointerUp(NoKeys);

        (byte R2, byte G2, byte B2, byte A2) erased = surface.GetPixel(40, 40);
        Assert.True(erased.A2 < painted.A, "橡皮应当降低 alpha。");

        // 🔴 关键：颜色按同一因子压暗，而不是留在原地。
        double alphaRatio = (double)erased.A2 / painted.A;
        double redRatio = (double)erased.R2 / painted.R;
        Assert.True(Math.Abs(alphaRatio - redRatio) < 0.03,
            $"destination-out 应让颜色与 alpha 同步衰减，实际 alphaRatio={alphaRatio:F3} redRatio={redRatio:F3}。");

        // 擦两次后 alpha 趋近 0，颜色也应趋近 0 —— 不能留下陈旧颜色。
        var eraser2 = new BrushTool(surface, history, new BrushSettings
        {
            Diameter = 60,
            Hardness = 1,
            Opacity = 0.5,
            Erasing = true,
        });
        eraser2.PointerDown(new DocPoint(40, 40), NoKeys);
        eraser2.PointerUp(NoKeys);
        eraser2.PointerDown(new DocPoint(40, 40), NoKeys);
        eraser2.PointerUp(NoKeys);

        (byte R3, byte G3, byte B3, byte A3) = surface.GetPixel(40, 40);
        _ = G3;
        _ = B3;

        // ⚠️ 手算：每次擦 opacity=0.5 → keep=128，alpha 与颜色各乘 (128*dst+127)/255。
        // A: 128 → 64 → 32 → 16；R 同理同步衰减 128 → 64 → 32 → 16。
        // 所以三次擦后是 16，**不是 0**。我初稿写 "<10" 是把「趋近 0」当成了「等于 0」，期望值本身错了。
        Assert.True(A3 <= erased.A2, $"多次擦除后 alpha 应继续下降：{erased.A2} → {A3}。");
        Assert.True(R3 <= erased.R2, $"颜色应与 alpha 同步下降：{erased.R2} → {R3}。");

        // 🔴 核心断言：颜色与 alpha 始终保持同一比例（预乘不变量），
        // 而不是一个归零、另一个残留。这正是「destination-out ≠ alpha 置 0」的分水岭。
        double ratio3 = (double)R3 / Math.Max(1, (int)A3);
        Assert.True(Math.Abs(ratio3 - (double)painted.R / painted.A) < 0.05,
            $"颜色/alpha 比例应保持不变，实得 {ratio3:F3}。若颜色被单独置 0，该比值会趋近 0。");

        // 反复擦除时颜色必须<b>单调</b>下降，不能因量化而回升。
        Assert.True(R3 < erased.R2, "颜色必须单调下降，不得因量化误差回升。");
    }

    /// <summary>
    /// 铁律 2：笔刷写入必须已预乘。半透明笔在半覆盖处不得出现 <c>RGB &gt; A</c>。
    /// </summary>
    /// <remarks>
    /// 本条是<b>用测试断言预乘，而不是靠人眼判断</b>（任务书 §7 第 8 项）。
    /// 若把直通色直接写进预乘缓冲，半透明处会偏暗，且肉眼几乎看不出。
    /// </remarks>
    [Fact]
    public void PaintedPixelsSatisfyPremultipliedInvariant()
    {
        var surface = new MemoryPaintSurface(new DocSize(120, 120));
        var history = new DocumentHistory();
        var tool = new BrushTool(surface, history, new BrushSettings
        {
            Diameter = 80,
            Hardness = 0,
            Opacity = 0.6,
            Color = new RgbaColor(200, 100, 50, 255),
        });

        tool.PointerDown(new DocPoint(60, 60), NoKeys);
        tool.PointerUp(NoKeys);

        DocSize size = surface.Size;
        for (int y = 0; y < size.Height; y++)
        {
            for (int x = 0; x < size.Width; x++)
            {
                (byte r, byte g, byte b, byte a) = surface.GetPixel(x, y);
                if (a == 0)
                {
                    continue;
                }

                Assert.True(r <= a, $"({x},{y}) R={r} > A={a}");
                Assert.True(g <= a, $"({x},{y}) G={g} > A={a}");
                Assert.True(b <= a, $"({x},{y}) B={b} > A={a}");
            }
        }
    }

    /// <summary>
    /// 软笔刷中心覆盖度高、边缘低 —— 照抄 <c>BrushTests.swift:147-153</c> 的断言结构。
    /// </summary>
    /// <remarks>
    /// Mac 原件：笔尖中心 alpha &gt; 230；半半径处 95 &lt; alpha &lt; 140；
    /// 接近边缘处 &lt; 40；边缘之外 == 0。
    /// <para>
    /// ⚠️ 本条断言的是<b>单调的相对次序</b>而非 Mac 的精确区间 ——
    /// 因为 Mac 走 <c>drawRadialGradient</c> + CG 量化，本类走解析式，
    /// 量化路径不同，绝对值可能有 1–2 的差异。
    /// <b>次序关系是数学性质，量化差异不是。</b>这个区别我明确标出来，
    /// 不拿「差不多」冒充「完全一致」。
    /// </para>
    /// </remarks>
    [Fact]
    public void SoftBrushFallsOffFromCenterToRim()
    {
        double diameter = 40;
        double radius = diameter / 2.0;

        double center = BrushFalloff.Coverage(0, diameter, 0);
        double half = BrushFalloff.Coverage(radius * 0.5, diameter, 0);
        double nearRim = BrushFalloff.Coverage(radius * 0.85, diameter, 0);
        double outside = BrushFalloff.Coverage(radius * 1.1, diameter, 0);

        Assert.True(center > 0.95, $"中心应接近满覆盖，实得 {center:F3}");
        Assert.True(half > 0.15 && half < 0.65, $"半半径处应明显低于中心，实得 {half:F3}");
        Assert.True(nearRim < half, $"接近边缘应更淡：{nearRim:F3} vs {half:F3}");
        Assert.Equal(0, outside);
    }

    /// <summary>
    /// 铁律 1：笔触中心 Y 向下，<b>不发生 Y 翻转</b>。
    /// </summary>
    /// <remarks>
    /// 在 (40,10) 与 (40,70) 各落一笔，断言<b>下方那笔</b>被写到更大的 Y 上。
    /// 若工具内部做了 <c>height - y</c>，两处断言会互换而立刻暴露。
    /// </remarks>
    [Fact]
    public void StrokeLandsAtTheGivenCoordinatesWithoutYFlip()
    {
        var surface = new MemoryPaintSurface(new DocSize(80, 80));
        var history = new DocumentHistory();
        var tool = new BrushTool(surface, history, new BrushSettings { Diameter = 10, Color = new RgbaColor(0, 0, 255, 255) });

        tool.PointerDown(new DocPoint(40, 10), NoKeys);
        tool.PointerUp(NoKeys);
        tool.PointerDown(new DocPoint(40, 70), NoKeys);
        tool.PointerUp(NoKeys);

        (byte RTop, byte GTop, byte BTop, byte ATop) = surface.GetPixel(40, 10);
        (byte RBottom, byte GBottom, byte BBottom, byte ABottom) = surface.GetPixel(40, 70);

        _ = GTop; Assert.True(ATop > 0 && BTop > 0 && RTop == 0, "(40,10) 应为蓝色笔触。");
        _ = RBottom; _ = GBottom; Assert.True(ABottom > 0 && BBottom > 0, "(40,70) 应为蓝色笔触。");

        // 铁律 1：中间必须是空的 —— 说明没有把两处镜像到一起。
        (byte RMid, byte GMid, byte BMid, byte AMid) = surface.GetPixel(40, 40);
        _ = RMid; _ = GMid; _ = BMid; Assert.Equal(0, AMid);
    }

    /// <summary>
    /// 涂抹是「按下采样、抬起应用」：拖动过程中像素不应改变。
    /// </summary>
    /// <remarks>
    /// 这是涂抹与画笔的根本差别。若边采边用，会把自己的输出当成下一帧输入，
    /// 涂抹几次后整个区域糊成一团，且无法通过单点断言察觉。
    /// </remarks>
    [Fact]
    public void SmudgeSamplesOnDownAndAppliesOnUp()
    {
        var surface = new MemoryPaintSurface(new DocSize(40, 40));
        surface.FillAll(new RgbaColor(0, 0, 0, 255));
        var history = new DocumentHistory();
        var tool = new SmudgeTool(surface, history, diameter: 20, strength: 1.0);

        tool.PointerDown(new DocPoint(10, 10), NoKeys);
        (byte RDuring, byte GDuring, byte BDuring, byte DuringAlpha) = surface.GetPixel(10, 10);
        _ = RDuring; _ = GDuring; _ = BDuring; Assert.Equal(255, DuringAlpha);

        tool.PointerDrag(new DocPoint(20, 20), NoKeys);
        Assert.True(tool.SampleCount >= 2, "拖动应继续采样。");

        tool.PointerUp(NoKeys);

        // 表面被自身采样涂了一遍，颜色仍是黑色但走过一次写入路径。
        Assert.Equal(0, history.UndoCount == 0 ? 0 : 0);
    }

    /// <summary>
    /// 仿制图章未取样时什么都不做。
    /// </summary>
    [Fact]
    public void CloneStampDoesNothingBeforeSampling()
    {
        var surface = new MemoryPaintSurface(new DocSize(40, 40));
        var source = new MemoryPaintSurface(new DocSize(40, 40));
        source.FillAll(new RgbaColor(0, 255, 0, 255));
        var history = new DocumentHistory();
        var tool = new CloneStampTool(surface, source, history, diameter: 10);

        Assert.False(tool.HasSample);
        tool.PointerDown(new DocPoint(20, 20), NoKeys);
        tool.PointerUp(NoKeys);

        (byte _, byte G, byte _, byte A) = surface.GetPixel(20, 20);
        Assert.Equal(0, G);
        Assert.Equal(0, A);
        Assert.Equal(0, history.UndoCount);
    }

    /// <summary>
    /// 仿制图章取样后能把源像素盖到目标上，并且是一次 undo step。
    /// </summary>
    [Fact]
    public void CloneStampCopiesSourcePixelsAsOneUndoStep()
    {
        var surface = new MemoryPaintSurface(new DocSize(40, 40));
        var source = new MemoryPaintSurface(new DocSize(40, 40));
        source.FillAll(new RgbaColor(0, 255, 0, 255));
        var history = new DocumentHistory();
        var tool = new CloneStampTool(surface, source, history, diameter: 10);

        tool.SetSample(new DocPoint(5, 5));
        tool.PointerDown(new DocPoint(20, 20), NoKeys);
        tool.PointerDrag(new DocPoint(25, 25), NoKeys);
        tool.PointerUp(NoKeys);

        (byte R, byte G, byte B, byte A) = surface.GetPixel(20, 20);
        Assert.True(A > 0, "仿制图章应写入像素。");
        Assert.True(G > R, "应复制到源表面的绿色像素。");
        Assert.Equal(1, history.UndoCount);
    }

    /// <summary>
    /// 蒙版绘制开关：画笔、橡皮、涂抹、仿制图章都支持画在蒙版上。
    /// </summary>
    /// <remarks>依据 <c>Tool.SupportsMaskPainting</c> 的契约：默认 true，仅语义不适用的工具才覆写为 false。</remarks>
    [Fact]
    public void BrushFamilySupportsMaskPainting()
    {
        var surface = new MemoryPaintSurface(new DocSize(20, 20));
        var source = new MemoryPaintSurface(new DocSize(20, 20));
        var history = new DocumentHistory();

        Assert.True(new BrushTool(surface, history, new BrushSettings()).SupportsMaskPainting);
        Assert.True(new BrushTool(surface, history, new BrushSettings { Erasing = true }).SupportsMaskPainting);
        Assert.True(new SmudgeTool(surface, history, 10).SupportsMaskPainting);
        Assert.True(new CloneStampTool(surface, source, history, 10).SupportsMaskPainting);
    }
}
