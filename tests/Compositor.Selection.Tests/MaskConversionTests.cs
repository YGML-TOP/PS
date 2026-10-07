namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// 选区 ↔ 蒙版转换与羽化差异统计测试。
/// </summary>
/// <remarks>
/// 【照抄组】的期望值搬运自 Mac 版 <c>CompositorTests/SelectionTests.swift:250-265</c>
/// （<c>cmdClickingAMaskSelectsItsBlackAreas</c>）与 <c>MaskTracing.swift</c> 的阈值定义。
/// 【推导组】由源码数学定义推导。
/// </remarks>
public class MaskConversionTests
{
    /// <summary>在文档坐标下取覆盖度。</summary>
    /// <param name="plane">覆盖度平面，可为 <see langword="null"/>（此时返回 0）。</param>
    /// <param name="x">文档 X。</param>
    /// <param name="y">文档 Y。</param>
    /// <returns>覆盖度 0–255。</returns>
    private static int Coverage(CoveragePlane? plane, int x, int y) => plane?.CoverageAt(new DocPoint(x, y)) ?? 0;

    /// <summary>构造 Mac 测试里那张"白底 + 黑方块 + 白色洞"的蒙版（<c>SelectionTests.swift:234-248</c>）。</summary>
    /// <returns>100×100 的灰度蒙版。</returns>
    private static CoveragePlane MaskedLayer()
    {
        // Mac 原文：先全白，再在 (20,30,40×40) 涂黑，再在 (30,40,10×10) 涂回白。
        var plane = CoveragePlane.CreateFilled(new DocRect(new DocPoint(0, 0), new DocSize(100, 100)), 255);
        for (int y = 30; y < 70; y++)
        {
            for (int x = 20; x < 60; x++)
            {
                plane[x, y] = 0;
            }
        }

        for (int y = 40; y < 50; y++)
        {
            for (int x = 30; x < 40; x++)
            {
                plane[x, y] = 255;
            }
        }

        return plane;
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:255-258】Cmd-click 蒙版缩略图选中<b>黑色</b>区域。
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// #expect(try coverage(session, 25, 35) == 255)   // Black: selected.
    /// #expect(try coverage(session, 35, 45) == 0)     // White hole: not selected.
    /// #expect(try coverage(session, 80, 80) == 0)     // White surroundings.
    /// #expect(try coverage(session, 59, 69) == 255 &amp;&amp; coverage(session, 60, 70) == 0) // Exact pixel edges.
    /// </code>
    /// 四条断言一字未改，包括最后那条<b>像素边界精确性</b>检查：
    /// 黑方块是 <c>[20,60) × [30,70)</c>，所以 (59,69) 在内、(60,70) 在外。
    /// </remarks>
    [Fact]
    public void MaskSelectionTakesTheBlackAreas()
    {
        var loaded = MaskConversion.FromMask(MaskedLayer(), selectDark: true);

        Assert.Equal(255, Coverage(loaded, 25, 35));
        Assert.Equal(0, Coverage(loaded, 35, 45));
        Assert.Equal(0, Coverage(loaded, 80, 80));
        Assert.Equal(255, Coverage(loaded, 59, 69));
        Assert.Equal(0, Coverage(loaded, 60, 70));
    }

    /// <summary>
    /// 【照抄 · SelectionTests.swift:260-264】载入选区后仍遵守 Add / Subtract 组合模式。
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// lasso(session, square(80, 80, 10))
    /// session.loadMaskSelection(layerID: id, mode: .add)
    /// #expect(try coverage(session, 85, 85) == 255 &amp;&amp; coverage(session, 25, 35) == 255)
    /// session.loadMaskSelection(layerID: id, mode: .subtract)
    /// #expect(try coverage(session, 85, 85) == 255 &amp;&amp; coverage(session, 25, 35) == 0)
    /// </code>
    /// 注意 Mac 最后这次 subtract 的结果是"方块仍在、黑区被挖掉"，
    /// 即减选同样是"当前选区减去新形状"。
    /// </remarks>
    [Fact]
    public void LoadingAMaskStillHonoursAddAndSubtract()
    {
        var square = LassoRaster.Rasterize(
        [
            new DocPoint(80, 80),
            new DocPoint(90, 80),
            new DocPoint(90, 90),
            new DocPoint(80, 90),
        ]);

        var loaded = MaskConversion.FromMask(MaskedLayer(), selectDark: true);

        var added = BooleanOps.Combine(SelectionCombineMode.Add, square, loaded);
        Assert.Equal(255, Coverage(added, 85, 85));
        Assert.Equal(255, Coverage(added, 25, 35));

        var subtracted = BooleanOps.Combine(SelectionCombineMode.Subtract, added, loaded);
        Assert.Equal(255, Coverage(subtracted, 85, 85));
        Assert.Equal(0, Coverage(subtracted, 25, 35));
    }

    /// <summary>
    /// 【照抄 · MaskTracing.swift:6, 9, 12】50% 阈值以 128 为界，且 <c>128</c> 归入"白"。
    /// </summary>
    /// <remarks>
    /// 三个入口分别是 <c>$0 &lt; 128</c>（黑）与 <c>$0 &gt;= 128</c>（白）。
    /// 所以恰好 128 的像素<b>算白</b>，不是"一半"。这条测试专门锁死这个边界归属。
    /// </remarks>
    [Fact]
    public void ThresholdTreatsExactly128AsWhite()
    {
        var gray = CoveragePlane.CreateFilled(new DocRect(new DocPoint(0, 0), new DocSize(4, 1)), 0);
        gray[0, 0] = 127;
        gray[1, 0] = 128;
        gray[2, 0] = 129;
        gray[3, 0] = 0;

        var whites = MaskConversion.FromMask(gray, selectDark: false);
        Assert.Equal(0, whites[0, 0]);
        Assert.Equal(255, whites[1, 0]);
        Assert.Equal(255, whites[2, 0]);
        Assert.Equal(0, whites[3, 0]);

        var darks = MaskConversion.FromMask(gray, selectDark: true);
        Assert.Equal(255, darks[0, 0]);
        Assert.Equal(0, darks[1, 0]);
        Assert.Equal(0, darks[2, 0]);
        Assert.Equal(255, darks[3, 0]);
    }

    /// <summary>
    /// 【照抄 · MaskTracing.swift:77-78】全白蒙版载入选区得到"无选区"。
    /// </summary>
    /// <remarks>
    /// Mac 原文用 <c>NSSound.beep()</c> 并<b>不改动</b>现有选区
    /// （<c>SelectionTests.swift:280</c>：<c>#expect(session.selection == nil)</c> —— 全黑都没有时是 nil）。
    /// 本测试验证本实现产出<b>全零选区</b>而非 null：全白蒙版里没有黑像素，
    /// 因此取不到任何覆盖度，调用方应据此判断"什么都没有"。
    /// <para>
    /// ⚠️ <b>与 Mac 的一处语义差</b>：Mac 用 <c>nil</c> 表示"没选到东西"，
    /// 本实现用全零平面表示。<c>BooleanOps.Combine</c> 已提供把两者区分开的能力
    /// （null=无选区，全零=显式空选区），调用方需要据此分支。
    /// 已列入待报告的已知差异。
    /// </para>
    /// </remarks>
    [Fact]
    public void LoadingAnAllWhiteMaskSelectsNothing()
    {
        var allWhite = CoveragePlane.CreateFilled(new DocRect(new DocPoint(0, 0), new DocSize(50, 50)), 255);

        var loaded = MaskConversion.FromMask(allWhite, selectDark: true);

        Assert.False(loaded.HasContent());
    }

    /// <summary>
    /// 【推导】选区转蒙版默认逐值直通，不引入 alpha。
    /// </summary>
    /// <remarks>
    /// 铁律 2：选区与蒙版是同一种 8-bit 覆盖度，中间没有格式转换。
    /// 若谁在这里偷偷加 alpha 或做归一化，这条测试会失败。
    /// </remarks>
    [Fact]
    public void ToMaskPassesCoverageThroughUnchanged()
    {
        var selection = CoveragePlane.CreateZero(new DocRect(new DocPoint(0, 0), new DocSize(4, 1)));
        selection[0, 0] = 0;
        selection[1, 0] = 137;
        selection[2, 0] = 255;
        selection[3, 0] = 42;

        var mask = MaskConversion.ToMask(selection);

        Assert.Equal(0, mask[0, 0]);
        Assert.Equal(137, mask[1, 0]);
        Assert.Equal(255, mask[2, 0]);
        Assert.Equal(42, mask[3, 0]);
    }

    /// <summary>
    /// 【推导】二值化按同一阈值执行，且与 FromMask 互为逆运算（在 0/255 上）。
    /// </summary>
    [Fact]
    public void ToMaskThresholdingMatchesFromMask()
    {
        var selection = CoveragePlane.CreateZero(new DocRect(new DocPoint(0, 0), new DocSize(4, 1)));
        selection[0, 0] = 127;
        selection[1, 0] = 128;
        selection[2, 0] = 200;
        selection[3, 0] = 0;

        var mask = MaskConversion.ToMask(selection, threshold: true);

        Assert.Equal(0, mask[0, 0]);
        Assert.Equal(255, mask[1, 0]);
        Assert.Equal(255, mask[2, 0]);
        Assert.Equal(0, mask[3, 0]);
    }

    /// <summary>
    /// 【推导】羽化前后必须真的产生差异，且差异集中在边缘。
    /// </summary>
    /// <remarks>
    /// 这条对应任务书要求的"<b>羽化前后的差异</b>"。
    /// 它验证三件事：差异非零、差异不铺满整个包围盒、包围盒确实被扩张。
    /// </remarks>
    [Fact]
    public void FeatheringChangesTheEdgeAndGrowsTheBounds()
    {
        var before = CoveragePlane.CreateFilled(new DocRect(new DocPoint(50, 50), new DocSize(60, 60)), 255);

        var after = FeatherOps.Apply(before, 6);

        Assert.True(after.Bounds.Left < before.Bounds.Left, "羽化后包围盒没有向左扩张");
        Assert.True(after.Bounds.Right > before.Bounds.Right, "羽化后包围盒没有向右扩张");

        FeatherDiffStats stats = MaskConversion.FeatherDifference(before, after);

        Assert.True(stats.ChangedPixels > 0, "羽化前后没有任何像素发生变化");
        Assert.True(stats.MeanDelta > 0, "变化像素的平均差值为 0");

        // 羽化只该改边缘，内部必须原封不动。
        // ⚠️ 这里<b>不用"变化像素占比"做判据</b>：包围盒从 60×60 扩到 84×84，
        // 比较域本身就变大了，占比天然偏高（实测 66.64%），拿它设阈值没有物理意义。
        // 有意义的判据是"中心没被动过"——见下一条断言。
        Assert.True(stats.ChangedRatio < 1.0, "羽化改动了全部像素，选区内部被压暗了");

        double centerY = after.Bounds.Top + (after.Height / 2) + 0.5;
        Assert.Equal(255, after.CoverageAt(new DocPoint(80, centerY)));
    }

    /// <summary>
    /// 【推导】未羽化时差异统计必须全零。
    /// </summary>
    [Fact]
    public void FeatherDifferenceIsZeroWithoutFeathering()
    {
        var plane = CoveragePlane.CreateFilled(new DocRect(new DocPoint(0, 0), new DocSize(20, 20)), 255);

        FeatherDiffStats stats = MaskConversion.FeatherDifference(plane, plane);

        Assert.Equal(0, stats.ChangedPixels);
        Assert.Equal(0, stats.TotalAbsoluteDelta);
        Assert.Equal(0.0, stats.ChangedRatio);
    }
}