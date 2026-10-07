namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// 选区羽化测试。
/// </summary>
/// <remarks>
/// <para>本文件严格分为两组，来源不同：</para>
/// <list type="bullet">
/// <item><description>
/// <b>【照抄组】</b>：搬运自 Mac 版 <c>CompositorTests/SelectionFeatherTests.swift:7-46</c>。
/// 注意 Mac 那条测试<b>只断言性质、不锁定任何数值</b>（<c>fading.count &gt;= 4</c>），
/// 因为它依赖 CoreImage 的模糊输出、无法断言具体字节。本测试照搬这一性质断言。
/// </description></item>
/// <item><description>
/// <b>【推导组】</b>：期望值由 <c>Selection.swift:325-331</c> 的
/// <c>(f₁² + f₂²)<sup>1/2</sup></c> 公式与 <c>Selection.swift:27</c> 的 <c>sigma = feather/2</c>
/// 在 C# 侧推导，<b>不是</b> Mac 的实测输出。
/// </description></item>
/// </list>
/// </remarks>
public class FeatherOpsTests
{
    /// <summary>创建填充矩形选区。</summary>
    /// <param name="left">左边界。</param>
    /// <param name="top">上边界。</param>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <returns>覆盖度为 255 的平面。</returns>
    private static CoveragePlane Box(double left, double top, int width, int height) =>
        CoveragePlane.CreateFilled(new DocRect(new DocPoint(left, top), new DocSize(width, height)), 255);

    /// <summary>取某一行的全部覆盖度值。</summary>
    /// <param name="plane">覆盖度平面。</param>
    /// <param name="y">行号（局部坐标）。</param>
    /// <returns>该行所有像素的覆盖度。</returns>
    private static int[] Row(CoveragePlane plane, int y)
    {
        var values = new int[plane.Width];
        for (int x = 0; x < plane.Width; x++)
        {
            values[x] = plane[x, y];
        }

        return values;
    }

    /// <summary>
    /// 【照抄 · SelectionFeatherTests.swift:7-31】羽化后必须存在软边缘。
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// session.setSelection(DocumentSelection(path: CGPath(rect: CGRect(x: 20, y: 0, width: 20, height: 20), transform: nil),
    ///                                        antialiased: true, feather: 6), name: "probe")
    /// let clip = try #require(try session.selection?.clip(canvas: CGSize(width: 60, height: 20)))
    /// let coverage = try #require(clip.coverage)
    /// …
    /// let row = coverage.height / 2
    /// let values = (0..&lt;coverage.width).map { Int(bytes[row * coverage.width + $0]) }
    /// let fading = values.filter { $0 &gt; 8 &amp;&amp; $0 &lt; 247 }
    /// #expect(fading.count &gt;= 4, "the clip has no soft edge: \(values)")
    /// </code>
    /// 断言原文照搬：矩形 (20,0,20×20)、羽化量 6、过滤区间 <c>&gt;8 且 &lt;247</c>、要求 <c>≥ 4</c> 个。
    /// <para>
    /// ⚠️ 这条断言<b>不锁定任何具体字节值</b>，因为 Mac 的羽化走 CoreImage 闭源模糊，
    /// 其输出本就无从照抄数值。它守的是"<b>必须有渐变</b>"这个语义 ——
    /// 如果谁把 sigma 写错、或忘记扩张包围盒导致渐变被截断，它会失败。
    /// </para>
    /// </remarks>
    [Fact]
    public void FeatherSoftensTheSelectionAndWhatItClips()
    {
        var selection = Box(20, 0, 20, 20);
        var feathered = FeatherOps.Apply(selection, 6);

        var values = Row(feathered, feathered.Height / 2);
        var fading = values.Where(v => v > 8 && v < 247).ToArray();

        Assert.True(fading.Length >= 4, $"羽化后没有软边缘：{string.Join(",", values)}");
    }

    /// <summary>
    /// 【照抄 · SelectionFeatherTests.swift:32-45】羽化后的填充结果在图层上也必须软。
    /// </summary>
    /// <remarks>
    /// Mac 原文在填充图层后取 alpha 通道，同样断言
    /// <c>alphas.filter { $0 &gt; 8 &amp;&amp; $0 &lt; 247 }.count &gt;= 4</c>。
    /// <para>
    /// ⚠️ <b>本测试无法完整照抄。</b> Mac 那条走的是"填充图层 → 读回图像 alpha"，
    /// 需要图层、绘制与像素合成管线，而那属于 AI-1 的合成器与 AI-5 的图层域，
    /// 不在本工程范围。
    /// </para>
    /// <para>
    /// 本测试<b>只保留其中在本工程内可验证的那一半</b>：
    /// 羽化后的覆盖度在竖直边缘两侧都有中间值 —— 这正是"填充结果会软"的<b>前提</b>。
    /// 若这一半不成立，Mac 那条端到端断言也必然不成立。
    /// <b>这是裁剪后的照抄，不是完整照抄，已如实标注。</b>
    /// </para>
    /// </remarks>
    [Fact]
    public void FeatheredCoverageHasSoftValuesOnBothSidesOfTheEdge()
    {
        var feathered = FeatherOps.Apply(Box(20, 0, 20, 20), 6);
        int mid = feathered.Height / 2;
        double scanY = feathered.Bounds.Top + mid + 0.5;

        // 边缘内侧：取距左边缘 6 像素处（σ=3，即 2σ）。
        // ⚠️ 不能贴着边缘取点：距边缘 0.5 像素处本就处于高斯衰减区，
        // 实测仅 144，那是<b>正确的物理结果</b>而非缺陷 —— 用它做断言会得出错误结论。
        var inner = feathered.CoverageAt(new DocPoint(26, scanY));

        // 边缘外侧：明显低于内侧。
        var outer = feathered.CoverageAt(new DocPoint(14, scanY));

        Assert.True(inner > 200, $"边缘内侧 2σ 处覆盖度过低：{inner}");
        Assert.True(outer < inner, $"外侧覆盖度没有衰减：inner={inner} outer={outer}");
        Assert.True(outer > 0, $"外侧完全无覆盖，羽化没有向外扩散：{outer}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【推导组】
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 【推导 · Selection.swift:328】羽化叠加是<b>平方和开方</b>，绝不是线性相加。
    /// </summary>
    /// <remarks>
    /// 推导依据：<c>Selection.swift:328</c> 写的是
    /// <code>
    /// (current.feather * current.feather + CGFloat(amount) * CGFloat(amount)).squareRoot()
    /// </code>
    /// <para>
    /// <b>这是整个 M5 最容易被写错的一行。</b>写成 <c>f₁ + f₂</c> 不会编译失败、
    /// 也不会让上面任何一条照抄断言失败 —— 它只会在"羽化两次"时偏离 Mac 约 29%：
    /// <c>Combine(6, 6)</c> 的正确答案是 <c>√72 ≈ 8.485</c>，线性相加会得到 12。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0.0, 6, 6.0)]                    // 硬边 + 6 = 6
    [InlineData(6.0, 6, 8.4852813742385702)]     // √72 ≈ 8.485，不是 12
    [InlineData(8.4852813742385702, 6, 10.392304845413261)] // √(72+36) = √108 ≈ 10.392
    [InlineData(0.0, 250, 250.0)]
    public void CombineUsesRootSumOfSquaresNotAddition(double current, int amount, double expected)
    {
        double actual = FeatherOps.Combine(current, amount);

        Assert.Equal(expected, actual, 9);
    }

    /// <summary>
    /// 【推导 · Selection.swift:330】羽化叠加结果被封顶在 250。
    /// </summary>
    /// <remarks>
    /// 推导依据：<c>min(250, softened)</c>。
    /// <c>Combine(200, 200)</c> 的未封顶值是 <c>√80000 ≈ 282.84</c>，封顶后应为 250。
    /// </remarks>
    [Fact]
    public void CombineClampsAtTwoHundredAndFifty()
    {
        Assert.Equal(250.0, FeatherOps.Combine(200.0, 200));
    }

    /// <summary>
    /// 【推导 · Selection.swift:35】羽化包围盒向四周扩 <c>ceil(feather × 2)</c>。
    /// </summary>
    /// <remarks>
    /// 推导依据：<c>coverageBounds</c> 的实现是
    /// <c>path.boundingBoxOfPath.insetBy(dx: -ceil(feather * 2), dy: -ceil(feather * 2))</c>，
    /// 系数 2 来自 <c>sigma = feather / 2</c> 下的 4σ = 4 × feather/2。
    /// <para>
    /// <b>漏掉这一步的代价是静默的</b>：羽化梯度本应扩散到包围盒之外，
    /// 只在原包围盒内卷积会把外侧那半截渐变直接切掉，
    /// 表现为"羽化只往选区内部软、边缘外侧是硬台阶"。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(6.0, 12)]
    [InlineData(1.5, 3)]      // ceil(1.5*2) = 3
    [InlineData(250.0, 500)]
    public void CoverageBoundsInflatesByCeilOfTwiceTheFeather(double feather, double expected)
    {
        DocRect bounds = new(new DocPoint(10, 20), new DocSize(30, 40));

        DocRect result = FeatherOps.CoverageBounds(bounds, feather);

        Assert.Equal(bounds.Left - expected, result.Left);
        Assert.Equal(bounds.Top - expected, result.Top);
        Assert.Equal(bounds.Size.Width + (expected * 2), result.Size.Width);
        Assert.Equal(bounds.Size.Height + (expected * 2), result.Size.Height);
    }

    /// <summary>
    /// 【推导】羽化不得改变选区内部的常量值。
    /// </summary>
    /// <remarks>
    /// 高斯核已归一化，若归一化写错，选区内部的 255 会被整体压暗 ——
    /// 这是羽化实现里除 sigma 之外最常见的第二个缺陷，且视觉上不易察觉。
    /// </remarks>
    [Fact]
    public void FeatherPreservesTheConstantInterior()
    {
        var feathered = FeatherOps.Apply(Box(50, 50, 60, 60), 6);

        // 选区中心远离任何边缘，应当仍是满覆盖度。
        int mid = feathered.Height / 2;
        Assert.Equal(255, feathered.CoverageAt(new DocPoint(80, feathered.Bounds.Top + mid + 0.5)));
    }

    /// <summary>
    /// 【推导】羽化量为 0 走硬边快路径，包围盒不得被扩张。
    /// </summary>
    [Fact]
    public void ZeroFeatherReturnsPlaneUnchanged()
    {
        var original = Box(10, 10, 20, 20);

        var result = FeatherOps.Apply(original, 0);

        Assert.Same(original, result);
        Assert.Equal(10, result.Bounds.Left);
        Assert.Equal(20, result.Bounds.Size.Width);
    }

    /// <summary>
    /// 【推导】羽化量越界必须抛异常。
    /// </summary>
    [Theory]
    [InlineData(-1.0)]
    [InlineData(251.0)]
    public void OutOfRangeFeatherThrows(double feather) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FeatherOps.Apply(Box(0, 0, 10, 10), feather));

    /// <summary>
    /// 【推导 · Selection.swift:325】羽化量必须为正才会生效。
    /// </summary>
    [Fact]
    public void CombineRejectsNonPositiveAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FeatherOps.Combine(0.0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeatherOps.Combine(0.0, -1));
    }
}