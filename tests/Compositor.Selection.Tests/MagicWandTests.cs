namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// 魔棒测试。
/// </summary>
/// <remarks>
/// 🔴 <b>本文件的方法全部来自【照抄组】</b>：期望值搬运自 Mac 版
/// <c>CompositorTests/MagicWandTests.swift:43-90</c> 的前四个测试，方法名与 Swift 行号一一对应。
/// 这四个都是纯算法测试，不依赖 <c>EditorSession</c>、图层或撤销栈，因此可完整照抄。
/// <para>
/// Mac 版第六个测试（<c>theWandReadsTheActiveLayerOrEveryVisibleLayer…</c>，
/// <c>MagicWandTests.swift:92-115</c>）与最后一个（<c>clickingInsideASelection…</c>，<c>:117-146</c>）
/// 依赖图层读取、合成器与 UI 事件，<b>未照抄</b>，已列入交付报告的「未完成」。
/// </para>
/// </remarks>
public class MagicWandTests
{
    /// <summary>Mac 版测试里的红色（<c>MagicWandTests.swift:7</c>）。</summary>
    private static readonly byte[] Red = [255, 0, 0, 255];

    /// <summary>Mac 版测试里的蓝色（<c>MagicWandTests.swift:8</c>）。</summary>
    private static readonly byte[] Blue = [0, 0, 255, 255];

    /// <summary>构造预乘 RGBA 图像，行优先、无对齐填充（stride = width*4）。</summary>
    /// <param name="width">宽。</param>
    /// <param name="height">高。</param>
    /// <param name="color">按 (x, y) 返回 4 字节像素。</param>
    /// <returns>预乘 RGBA 字节。</returns>
    private static byte[] Image(int width, int height, Func<int, int, byte[]> color)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte[] px = color(x, y);
                for (int c = 0; c < 4; c++)
                {
                    bytes[((y * width) + x) * 4 + c] = px[c];
                }
            }
        }

        return bytes;
    }

    /// <summary>把选区转成"被覆盖的像素索引集合"，对应 Mac 测试的 <c>pixels(_:width:height:)</c>。</summary>
    /// <param name="plane">选区，可为 <see langword="null"/>。</param>
    /// <param name="imageWidth">原图宽度，用于换算 <c>y * width + x</c> 索引。</param>
    /// <returns>覆盖度 ≥ 128 的像素索引集合。</returns>
    /// <remarks>
    /// Mac 的助手是从<b>路径</b>光栅化后取 <c>bytes[$0] &gt;= 128</c>（<c>MagicWandTests.swift:30</c>）；
    /// 本实现直接已有覆盖度，取阈值相同（128）。
    /// ⚠️ 索引必须用<b>原图</b>宽度换算，不能用选区包围盒的宽度 ——
    /// 包围盒经 <c>TrimToContent</c> 收缩后通常比原图窄，用错会整体错位。
    /// </remarks>
    private static SortedSet<int> Pixels(CoveragePlane? plane, int imageWidth)
    {
        var set = new SortedSet<int>();
        if (plane is null)
        {
            return set;
        }

        int baseX = (int)plane.Bounds.Left;
        int baseY = (int)plane.Bounds.Top;
        for (int y = 0; y < plane.Height; y++)
        {
            for (int x = 0; x < plane.Width; x++)
            {
                if (plane[x, y] >= 128)
                {
                    set.Add(((baseY + y) * imageWidth) + baseX + x);
                }
            }
        }

        return set;
    }

    /// <summary>构造像素索引块，对应 Mac 测试的 <c>block(columns:rows:width:)</c>。</summary>
    /// <param name="colStart">起始列（含）。</param>
    /// <param name="colEnd">结束列（不含）。</param>
    /// <param name="rowStart">起始行（含）。</param>
    /// <param name="rowEnd">结束行（不含）。</param>
    /// <param name="width">原图宽度。</param>
    /// <returns>索引集合。</returns>
    private static SortedSet<int> Block(int colStart, int colEnd, int rowStart, int rowEnd, int width)
    {
        var set = new SortedSet<int>();
        for (int y = rowStart; y < rowEnd; y++)
        {
            for (int x = colStart; x < colEnd; x++)
            {
                set.Add((y * width) + x);
            }
        }

        return set;
    }

    /// <summary>
    /// 【照抄 · MagicWandTests.swift:43-56】<c>contiguousStopsAtOtherColorsWhileNonContiguousFindsEveryMatch</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文全部断言照抄，含最后那条<b>行方向</b>检查：
    /// <c>banded</c> 是 4×3、y==0 为红色，点 (1,0) 必须选中第一行（索引 0..3），
    /// 点 (9,0) 越界必须返回 nil。
    /// <para>
    /// ⚠️ 这条测试同时锁死了 <c>rounded(.down)</c>：点 <c>(1, 0)</c> 落在像素 (1,0)，
    /// 若误用 AwayFromZero 取整，y=0 仍是 0，本例恰好测不出；
    /// 但 <c>(1.5, 2.5)</c> 两个方向都会被 floor 到 (1,2)，与 AwayFromZero 一致。
    /// 取整规则的真正差异由 <see cref="SeedUsesFloorNotRoundToNearest"/> 单独覆盖。
    /// </para>
    /// </remarks>
    [Fact]
    public void ContiguousStopsAtOtherColorsWhileNonContiguousFindsEveryMatch()
    {
        byte[] stripes = Image(10, 4, (x, _) => x < 3 || x >= 6 ? Red : Blue);
        var left = Block(0, 3, 0, 4, 10);
        var right = Block(6, 10, 0, 4, 10);

        var connected = MagicWand.Select(stripes, 10, 4, 40, 1.5, 2.5, WandOptions.Default);
        Assert.Equal(left, Pixels(connected, 10));

        var everywhere = MagicWand.Select(stripes, 10, 4, 40, 1.5, 2.5, new WandOptions(Contiguous: false));

        // ⚠️ 必须重新包成 SortedSet：LINQ 的 Union() 返回 IEnumerable<int>，
        // 与 SortedSet<int> 是不同类型，Assert.Equal 会因类型不同直接判不等，
        // 哪怕元素完全一样也过不了（这与 Mac 的 Set.union 返回同类型不同）。
        Assert.Equal(new SortedSet<int>(left.Union(right)), Pixels(everywhere, 10));

        byte[] banded = Image(4, 3, (_, y) => y == 0 ? Red : Blue);
        var top = MagicWand.Select(banded, 4, 3, 16, 1, 0, WandOptions.Default);
        Assert.Equal(new SortedSet<int> { 0, 1, 2, 3 }, Pixels(top, 4));

        Assert.Null(MagicWand.Select(banded, 4, 3, 16, 9, 0, WandOptions.Default));
    }

    /// <summary>
    /// 【照抄 · MagicWandTests.swift:58-68】<c>toleranceAppliesToEveryChannelIncludingAlpha</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// let columns: [[UInt8]] = [[100,100,100,255], [132,100,100,255], [133,100,100,255], [100,100,100,222]]
    /// #expect(try run(0) == [0])
    /// #expect(try run(32) == [0, 1])
    /// #expect(try run(33) == [0, 1, 2, 3])
    /// </code>
    /// 逐条照抄，三档容差一字未改。这条测试同时锁死了三件事：
    /// <b>逐通道绝对差</b>、<b>含 alpha 通道</b>（第 4 列 alpha 222，差 33）、
    /// 以及<b>比较是 ≤ 而非 &lt;</b>（132−100=32 在容差 32 时被选中）。
    /// </remarks>
    [Fact]
    public void ToleranceAppliesToEveryChannelIncludingAlpha()
    {
        byte[][] columns =
        [
            [100, 100, 100, 255],
            [132, 100, 100, 255],
            [133, 100, 100, 255],
            [100, 100, 100, 222],
        ];

        byte[] row = Image(4, 1, (x, _) => columns[x]);

        SortedSet<int> Run(int tolerance) =>
            Pixels(MagicWand.Select(row, 4, 1, 16, 0.5, 0.5,
                new WandOptions(Tolerance: tolerance, Contiguous: false)), 4);

        Assert.Equal(new SortedSet<int> { 0 }, Run(0));
        Assert.Equal(new SortedSet<int> { 0, 1 }, Run(32));
        Assert.Equal(new SortedSet<int> { 0, 1, 2, 3 }, Run(33));
    }

    /// <summary>
    /// 【照抄 · MagicWandTests.swift:70-78】<c>sampleSizeAveragesThePixelsAroundTheClick</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文：
    /// <code>
    /// let dot = try image(width: 5, height: 5) { x, y in x == 2 &amp;&amp; y == 2 ? [255,255,255,255] : [0,0,0,255] }
    /// #expect(try pixels(point, width: 5, height: 5) == [12])
    /// // A 3 × 3 average is gray 28: black is within 30 of it, the white center is not.
    /// #expect(try pixels(averaged, width: 5, height: 5) == Set(0..&lt;25).subtracting([12]))
    /// </code>
    /// 两条断言照抄。Mac 注释里的 "gray 28" 也被本测试间接锁定：
    /// 若平均改成四舍五入（255/9=28.33 → 28）或截断，结果都是 28；
    /// 若改成先求和再除以 9 并向上取整（29），黑与 29 的差仍是 29 ≤ 30，选区不变 ——
    /// <b>所以本测试锁不死取整方向</b>，它锁的是"3×3 平均确实生效且只有中心被排除"。
    /// </remarks>
    [Fact]
    public void SampleSizeAveragesThePixelsAroundTheClick()
    {
        byte[] dot = Image(5, 5, (x, y) => x == 2 && y == 2 ? [255, 255, 255, 255] : [0, 0, 0, 255]);

        var point = MagicWand.Select(dot, 5, 5, 20, 2.5, 2.5,
            new WandOptions(Tolerance: 10, Contiguous: false));
        Assert.Equal(new SortedSet<int> { 12 }, Pixels(point, 5));

        var averaged = MagicWand.Select(dot, 5, 5, 20, 2.5, 2.5,
            new WandOptions(Tolerance: 30, SampleSize: WandSampleSize.ThreeByThree, Contiguous: false));
        Assert.Equal(new SortedSet<int>(Enumerable.Range(0, 25).Where(i => i != 12)),
            Pixels(averaged, 5));
    }

    /// <summary>
    /// 【照抄 · MagicWandTests.swift:80-90】<c>outlinesReproduceTheirPixelsWithHolesAndCornerTouches</c>
    /// </summary>
    /// <remarks>
    /// Mac 原文构造一个 8×6 掩码：左上角 3×3 环（中心 (1,1) 挖空），
    /// 外加两个<b>只对角相接</b>的像素 (5,4) 与 (6,5)，断言 outline 复现全部非零像素，
    /// 且全零掩码返回 nil。
    /// <para>
    /// ⚠️ <b>这是裁剪后的照抄</b>：Mac 走的是「掩码 → <c>wand_trace</c> 描边 → CGPath → 再光栅化」，
    /// 本实现没有路径中间表示（<c>MagicWand.FromMask</c> 直接产出覆盖度）。
    /// 因此本测试锁住的是<b>可比的等价物</b>：掩码里的每个非零像素都出现在结果中，
    /// 包括那个对角相接的孤立像素 —— 它正是用来暴露"4 邻域连通假设"的。
    /// 「中心空洞」也一并验证。
    /// 已如实标注为裁剪照抄，不冒充完整移植。
    /// </para>
    /// </remarks>
    [Fact]
    public void OutlinesReproduceTheirPixelsWithHolesAndCornerTouches()
    {
        const int width = 8, height = 6;
        var mask = new byte[width * height];

        // 3 × 3 环，中心挖空。
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                if (x == 1 && y == 1)
                {
                    continue;
                }

                mask[(y * width) + x] = 255;
            }
        }

        // 两个只在对角方向相接的像素。
        mask[(4 * width) + 5] = 255;
        mask[(5 * width) + 6] = 255;

        var expected = new SortedSet<int>(
            Enumerable.Range(0, mask.Length).Where(i => mask[i] != 0));

        var plane = MagicWand.FromMask(mask, width, height);

        Assert.NotNull(plane);
        Assert.Equal(expected, Pixels(plane, width));

        // 中心空洞必须仍然是空洞。
        Assert.Equal(0, plane!.CoverageAt(new DocPoint(1, 1)));
        // 对角相接的孤立像素不能被当成"不属于选区"。
        Assert.Equal(255, plane.CoverageAt(new DocPoint(5, 4)));
        Assert.Equal(255, plane.CoverageAt(new DocPoint(6, 5)));

        Assert.Null(MagicWand.FromMask(new byte[4], 2, 2));
    }

    /// <summary>
    /// 【推导 · MagicWand.swift:38】种子坐标必须<b>向下取整</b>，不是四舍五入。
    /// </summary>
    /// <remarks>
    /// 推导依据：<c>MagicWand.swift:38</c> 写的是 <c>Int(point.x.rounded(.down))</c>。
    /// 这与 <c>DragBox</c> 的 <c>.rounded()</c>（AwayFromZero）<b>是两种规则</b>，
    /// 且都出现在选区代码里，抄错任一处种子都会差一行/一列。
    /// <para>
    /// 本测试用一条只有<b>第 0 行</b>是红色的图像来区分：点 <c>(1.5, 1.5)</c>
    /// 向下取整落在 (1,1)（蓝色），而四舍五入会落在 (2,2) —— 但那条也为蓝色，
    /// 所以换一张"只有最后一行是红色"的图：向下取整落在非红行 → 选中空集；
    /// 四舍五入落到红行 → 选中那一行。
    /// </para>
    /// </remarks>
    [Fact]
    public void SeedUsesFloorNotRoundToNearest()
    {
        // 4 行，只有第 3 行（最后一行）是红色。
        byte[] img = Image(4, 4, (_, y) => y == 3 ? Red : Blue);

        // 点 (1.5, 1.5)：floor → (1,1) 蓝色，seed 是蓝色 → 选中蓝色区域
        // （contiguous=false 时全图蓝色像素都会被选中）。
        var floored = MagicWand.Select(img, 4, 4, 16, 1.5, 1.5, new WandOptions(Contiguous: false));
        Assert.NotNull(floored);
        Assert.DoesNotContain(12, Pixels(floored, 4));   // 索引 12 = (0,3)，红色行，不应被选中

        // 点 (1.5, 2.5)：floor → (1,2) 仍是蓝色；换点到红行验证对照。
        var onRed = MagicWand.Select(img, 4, 4, 16, 1.5, 3.5, new WandOptions(Contiguous: false));
        var redPixels = Pixels(onRed, 4);
        Assert.Contains(12, redPixels);
        Assert.Contains(13, redPixels);
        Assert.Contains(14, redPixels);
        Assert.Contains(15, redPixels);
    }
}