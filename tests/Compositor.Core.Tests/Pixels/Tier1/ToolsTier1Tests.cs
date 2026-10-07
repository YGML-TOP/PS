using Compositor.Core.Pixels;
using System.Linq;
using Xunit;

namespace Compositor.Core.Tests.Pixels.Tier1;

/// <summary>
/// Tier 1 验收基准：Swift 测试 → xunit 的<b>逐条</b>翻译。
/// </summary>
/// <remarks>
/// <para><b>来源（只读）</b>：<c>CompositorTests/DitherTests.swift</c>（4 测试 / 9 断言）、
/// <c>MagicWandTests.swift</c>（6 测试 / 19 断言）、<c>SpotHealingTests.swift</c>（1 测试 / 3 断言）、
/// <c>DistortTests.swift</c>（3 测试 / 19 断言），共 50 条断言。</para>
///
/// <para><b>铁律</b>：期望值与容差<b>只来自 Swift 原断言</b>，原样照抄；不自定容差、不猜期望、不放宽。
/// 若断言在 C# 实现上会失败，那是实现的问题——照抄断言并在交付说明里报"预计失败"。</para>
///
/// <para><b>翻译状态</b>：已翻译并断言 20 条（断言语句数）；<b>[Fact(Skip = …)] 标记为"未翻译"30 条</b>，
/// 每条都在方法注释里写明原因。<b>跳过的测试不代表通过</b>，它只是"这条断言当前无法在 C# 侧成立"的显式记录。</para>
///
/// <para><b>Swift 包装层的处理</b>：<c>DitherSettings</c> / <c>WandSettings</c> 的默认值与到
/// <c>DitherParams</c> / <c>wand_mask</c> 参数的映射，照抄成<b>本测试类内的 private helper</b>，
/// 不进 <c>src\</c>；CGImage / CGContext 用 <c>byte[]</c> + 预乘 RGBA8 直接替代，
/// CGBitmap 的 <c>bytesPerRow</c> 对应 <c>stride</c>，逐行读取的索引数值原样照抄
/// （如 <c>data[row * bytesPerRow + 5 * 4]</c> → <c>bytes[row * stride + 5 * 4]</c>）。</para>
///
/// <para><b>两处必须向验收方声明的偏离</b>（不是弱化断言，而是换到 C 函数层面）：</para>
/// <list type="number">
/// <item><b>魔棒</b>：Swift 的断言比较的是 <c>MagicWand.select</c> 的最终结果，而它的返回值是
/// <c>CGPath</c>；<c>pixels(path)</c> 还要再经过 <c>DocumentSelection.coverage</c>（CGPath 光栅化）
/// 才能回到像素集。<b>C# 侧没有 CGPath 光栅化器</b>，但 <c>WandPixels.Mask</c> 正是
/// <c>MagicWand.select</c> 内部直接调用的那个 C 函数（<c>wand_mask</c>）。
/// 故这 10 条断言改为：构造同样的输入 → 调 <c>WandPixels.Mask</c> → 断言<b>原期望值</b>（原样照抄的像素集合）。
/// 轮廓 → 像素的<b>往返</b>等价性需要光栅化，无法翻译（见 <c>OutlineRoundTrip…</c> 的 Skip 原因）。</item>
/// <item><b>污点修复的笔刷覆盖</b>：Swift 的 coverage 是 <c>BrushSettings(diameter: 24, hardness: 1)</c>
/// 经 <c>BrushStroke</c> 盖出的笔刷印（macOS 侧未移植）。C# 侧 <c>BrushPixels</c> 没有 coverage 光栅函数，
/// 故按"直径 24 ⇒ 半径 12 的实心圆盘"构造（<c>spot_heal</c> 只判 <c>coverage != 0</c>）。
/// 3 个断言点全在半径 12 深处、5 个"不许动"的点全在半径 30 以外，故断言对该构造不敏感；
/// 但它与真实笔刷并非逐字节相同，此处显式记录。</item>
/// </list>
///
/// <para><b>本文件未经编译验证</b>：交付环境没有 .NET SDK，无法 build / 无法跑测试。</para>
/// </remarks>
public sealed class ToolsTier1Tests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  DitherTests.swift
    // ═══════════════════════════════════════════════════════════════════════

    // Swift DitherSettings 默认值 → C DitherParams 的映射（照抄 Dither.swift:169-193）：
    //   normalized 后：levels 2 / diffusion 100 / density 0 / contrast 0 / angle 45°
    //   colors = .blackWhite ⇒ dark = (0,0,0)、light = (255,255,255)
    //   lightOnDark = true；cell = Int(lineSpacing)（仅 scanlines 用）
    //   dots = dots / 100；wobble = wobble（像素）
    // 注意：C# 侧 DitherStyle / DitherParams 在命名空间层级，**不**嵌套在 DitherPixels 内。
    private static readonly byte[] BlackRgb = { 0, 0, 0 };
    private static readonly byte[] WhiteRgb = { 255, 255, 255 };

    private static DitherParams Scanlines(int lineSpacing, float dotsPercent = 0f, float wobblePixels = 0f)
    {
        // 非 scanlines 风格才用到的字形表，这里恒为空（glyphCount = 0），与 Swift 一致。
        byte[] glyphs = Array.Empty<byte>();
        float[] glyphCoverage = Array.Empty<float>();

        return new DitherParams(
            DitherStyle.Scanlines,
            levels: 2,
            diffusion: 1f,
            density: 0f,
            contrast: 0f,
            cell: lineSpacing,
            angle: (float)(45.0 * Math.PI / 180.0),
            lightOnDark: 1,
            originalColors: 0,
            dark: BlackRgb,
            light: WhiteRgb,
            glyphWidth: 1,
            glyphHeight: 1,
            glyphs: glyphs,
            glyphCoverage: glyphCoverage,
            glyphCount: 0,
            dots: dotsPercent / 100f,
            wobble: wobblePixels);
    }

    // CGContext 填 sRGB 纯色（alpha = 1）落到预乘 RGBA8 上就是 R = G = B = round(gray * 255)、A = 255。
    private static byte[] FlatGray(int width, int height, int stride, byte level)
    {
        var rgba = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 4;
                rgba[p] = level;
                rgba[p + 1] = level;
                rgba[p + 2] = level;
                rgba[p + 3] = 255;
            }

        return rgba;
    }

    // Swift private func scanlines(gray:spacing:glow:)（DitherTests.swift:7-18）
    // glow 传 0 时 Swift 的 apply() 不进 glowing() 分支，故此处只需 dither_apply（即 DitherPixels.Apply）。
    private static int[] ScanlinesColumn(double gray, int spacing)
    {
        const int Width = 16, Height = 32, Stride = Width * 4;
        byte level = (byte)Math.Round(gray * 255.0, MidpointRounding.AwayFromZero);
        var rgba = FlatGray(Width, Height, Stride, level);
        var p = Scanlines(spacing);
        DitherPixels.Apply(rgba, Width, Height, Stride, in p);

        // data[$0 * result.bytesPerRow + 5 * 4] —— 索引数值照抄
        var column = new int[Height];
        for (int row = 0; row < Height; row++) column[row] = rgba[row * Stride + 5 * 4];

        return column;
    }

    private static byte[] FlatWhite(int width, int height, int stride)
        => FlatGray(width, height, stride, 255);

    // 读一整行某一通道（Swift middle 闭包：r.data[4 * r.row + $0 * 4]）
    private static int[] Row(byte[] rgba, int y, int stride, int count)
    {
        var values = new int[count];
        for (int x = 0; x < count; x++) values[x] = rgba[y * stride + x * 4];

        return values;
    }

    /// <summary>Swift <c>flat(64, 64)</c>：先铺黑，再在 x ∈ [32, 64) 铺白（DitherTests.swift:69）。</summary>
    private static byte[] HalfWhite(int width, int height, int stride)
    {
        var rgba = FlatGray(width, height, stride, 0);
        for (int y = 0; y < height; y++)
            for (int x = 32; x < width; x++)
            {
                int p = y * stride + x * 4;
                rgba[p] = 255;
                rgba[p + 1] = 255;
                rgba[p + 2] = 255;
                rgba[p + 3] = 255;
            }

        return rgba;
    }

    // Swift edges(_ wobble:)（DitherTests.swift:70-73）：逐条扫描线取第一列 R > 128，找不到记 -1。
    private static int[] Edges(byte[] edge, int wobble)
    {
        const int Width = 64, Height = 64, Stride = Width * 4;
        var rgba = (byte[])edge.Clone();
        var p = Scanlines(8, wobblePixels: wobble);
        DitherPixels.Apply(rgba, Width, Height, Stride, in p);

        var found = new List<int>();
        for (int line = 0; line < 8; line++)
        {
            int x = 0;
            while (x < Width && rgba[(line * 8 + 4) * Stride + x * 4] <= 128) x++;
            found.Add(x < Width ? x : -1);
        }

        return found.Distinct().OrderBy(v => v).ToArray();
    }

    /// <summary>
    /// 原 Swift：<c>DitherTests.swift:22 scanlinesAreLinesOfLightThatBloomWithBrightness()</c> —— 4 条断言
    /// （其中前两条在 <c>for band in 0..&lt;4</c> 里各跑 4 次，运行时 10 次断言）。
    /// </summary>
    [Fact]
    public void DitherTests_ScanlinesAreLinesOfLightThatBloomWithBrightness()
    {
        int[] white = ScanlinesColumn(1, 8);
        int[] gray = ScanlinesColumn(0.35, 8);
        int[] black = ScanlinesColumn(0, 8);

        for (int band = 0; band < 4; band++)
        {
            int[] rows = white.Skip(band * 8).Take(8).ToArray();

            // 译：白线从中间透亮——白色扫描线的中段是点亮的：<rows>
            Assert.True(
                rows[3] == 255 && rows[4] == 255,
                $"a white line is lit through its middle: {string.Join(",", rows)}");

            // 译：线与线之间是暗屏：<rows>
            Assert.True(
                rows.Any(v => v < 40),
                $"with dark screen between lines: {string.Join(",", rows)}");
        }

        // 译：灰线比白线更细更暗
        Assert.True(
            gray.Sum() * 2 < white.Sum(),
            "a gray line is thinner and dimmer than a white one");

        Assert.All(black, v => Assert.Equal(0, v));
    }

    /// <summary>
    /// 原 Swift：<c>DitherTests.swift:52 glowLightsBetweenTheLines()</c> —— 1 条断言，<b>未翻译</b>。
    /// </summary>
    /// <remarks>
    /// 唯一断言 <c>#expect(glowing[0] &gt; plain[0] + 40)</c> 依赖 Swift 的
    /// <c>DitherSettings.glowing()</c>（Dither.swift:146-163）：它用 CoreImage
    /// <c>CIImage.clampedToExtent().applyingGaussianBlur(sigma:)</c> 生成辉光源，
    /// 再调 <c>dither_glow</c>。CoreImage 是 macOS 专有，本项目 C# 侧<b>无任何等价实现</b>
    /// （<c>DitherPixels.Glow</c> 要求调用方自备已模糊好的辉光缓冲，模糊本身没有）。
    /// 期望值依赖模糊结果，无法在 C 函数层面复现 → 不翻译、也绝不弱化。
    /// </remarks>
    [Fact(Skip = "未翻译：glow 依赖 macOS CoreImage 的 CIImage.applyingGaussianBlur，C# 侧无等价实现（见方法注释）")]
    public void DitherTests_GlowLightsBetweenTheLines()
    {
    }

    /// <summary>
    /// 原 Swift：<c>DitherTests.swift:58 dotsBreakTheLinesIntoBeads()</c> —— 2 条断言。
    /// </summary>
    [Fact]
    public void DitherTests_DotsBreakTheLinesIntoBeads()
    {
        const int Width = 64, Height = 16, Stride = Width * 4;
        var white = FlatWhite(Width, Height, Stride);

        var solid = (byte[])white.Clone();
        var solidParams = Scanlines(8);
        DitherPixels.Apply(solid, Width, Height, Stride, in solidParams);

        var beads = (byte[])white.Clone();
        var beadParams = Scanlines(8, dotsPercent: 100f);
        DitherPixels.Apply(beads, Width, Height, Stride, in beadParams);

        Assert.All(Row(solid, 4, Stride, 64), v => Assert.Equal(255, v));

        int[] lit = Row(beads, 4, Stride, 64);
        // 译：每 8 像素一颗珠子：<lit>
        Assert.True(
            lit[3] == 255 && lit[4] == 255 && lit[0] < 60 && lit[8] < 60,
            $"beads every 8 pixels: {string.Join(",", lit)}");
    }

    /// <summary>
    /// 原 Swift：<c>DitherTests.swift:68 wobbleMovesLinesSideways()</c> —— 2 条断言。
    /// </summary>
    [Fact]
    public void DitherTests_WobbleMovesLinesSideways()
    {
        byte[] edge = HalfWhite(64, 64, 64 * 4);

        Assert.Equal(new[] { 32 }, Edges(edge, 0));

        // Swift 无注释：wobble = 12 时，8 条扫描线的竖边至少落到 3 个不同的 x。
        Assert.True(Edges(edge, 12).Length >= 3, "edges(12).count >= 3");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MagicWandTests.swift
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly byte[] Red = { 255, 0, 0, 255 };
    private static readonly byte[] Blue = { 0, 0, 255, 255 };

    // Swift private func image(width:height:_:)：预乘 RGBA，**首行在上**（行序与 C# 缓冲一致）。
    private static byte[] SolidImage(int width, int height, Func<int, int, byte[]> color)
    {
        var rgba = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte[] pixel = color(x, y);
                int p = y * width * 4 + x * 4;
                for (int c = 0; c < 4; c++) rgba[p + c] = pixel[c];
            }

        return rgba;
    }

    // Swift private func pixels(path:width:height:) 的 C 函数层等价：
    //   wand_mask 直接写出的掩码（原断言里再经 wand_trace → CGPath → DocumentSelection.coverage）。
    // 阈值 >= 128 沿用 Swift 的 coverage 判定式。
    private static int[] Selected(
        byte[] rgba, int width, int height, double pointX, double pointY,
        int tolerance, int sampleSizeRadius, int contiguous)
    {
        int seedX = (int)Math.Floor(pointX), seedY = (int)Math.Floor(pointY);
        var mask = new byte[width * height];
        WandPixels.Mask(rgba, width, height, width * 4, seedX, seedY, sampleSizeRadius, tolerance, contiguous, mask);

        return Enumerable.Range(0, width * height).Where(i => mask[i] >= 128).ToArray();
    }

    // Swift private func block(columns:rows:width:)
    private static int[] Block(int columnStart, int columnEnd, int rowStart, int rowEnd, int width)
        => Enumerable.Range(rowStart, rowEnd - rowStart)
            .SelectMany(y => Enumerable.Range(columnStart, columnEnd - columnStart).Select(x => y * width + x))
            .ToArray();

    /// <summary>
    /// 原 Swift：<c>MagicWandTests.swift:43 contiguousStopsAtOtherColorsWhileNonContiguousFindsEveryMatch()</c>
    /// —— 4 条断言。
    /// </summary>
    /// <remarks>
    /// 默认 <c>WandSettings</c>：tolerance 32、sampleSize .point（radius 0）、contiguous true
    /// （照抄 MagicWand.swift:11-19 的默认值）。点击点 (1.5, 2.5) 的取整是 <c>floor</c>，即种子 (1, 2)。
    /// </remarks>
    [Fact]
    public void MagicWandTests_ContiguousStopsAtOtherColorsWhileNonContiguousFindsEveryMatch()
    {
        var stripes = SolidImage(10, 4, (x, _) => x < 3 || x >= 6 ? Red : Blue);
        int[] left = Block(0, 3, 0, 4, 10);
        int[] right = Block(6, 10, 0, 4, 10);

        int[] connected = Selected(stripes, 10, 4, 1.5, 2.5, 32, 0, 1);
        Assert.Equal(left, connected);

        int[] everywhere = Selected(stripes, 10, 4, 1.5, 2.5, 32, 0, 0);
        Assert.Equal(left.Concat(right).OrderBy(v => v).ToArray(), everywhere);

        // 译：行序不变——点最上面一行就选中最上面一行。
        var banded = SolidImage(4, 3, (_, y) => y == 0 ? Red : Blue);
        int[] top = Selected(banded, 4, 3, 1, 0, 32, 0, 1);
        Assert.Equal(new[] { 0, 1, 2, 3 }, top);

        // Swift：#expect(... == nil)。nil ⇔ wand_mask 选中 0 个像素（MagicWand.swift:50 guard count > 0）。
        int[] miss = Selected(banded, 4, 3, 9, 0, 32, 0, 1);
        Assert.True(miss.Length == 0, "select(at: (9, 0)) == nil");
    }

    /// <summary>
    /// 原 Swift：<c>MagicWandTests.swift:58 toleranceAppliesToEveryChannelIncludingAlpha()</c> —— 3 条断言。
    /// </summary>
    [Fact]
    public void MagicWandTests_ToleranceAppliesToEveryChannelIncludingAlpha()
    {
        byte[][] columns =
        {
            new byte[] { 100, 100, 100, 255 },
            new byte[] { 132, 100, 100, 255 },
            new byte[] { 133, 100, 100, 255 },
            new byte[] { 100, 100, 100, 222 },
        };
        var row = SolidImage(4, 1, (x, _) => columns[x]);

        Assert.Equal(new[] { 0 }, Selected(row, 4, 1, 0.5, 0.5, 0, 0, 0));
        Assert.Equal(new[] { 0, 1 }, Selected(row, 4, 1, 0.5, 0.5, 32, 0, 0));
        Assert.Equal(new[] { 0, 1, 2, 3 }, Selected(row, 4, 1, 0.5, 0.5, 33, 0, 0));
    }

    /// <summary>
    /// 原 Swift：<c>MagicWandTests.swift:70 sampleSizeAveragesThePixelsAroundTheClick()</c> —— 2 条断言。
    /// </summary>
    [Fact]
    public void MagicWandTests_SampleSizeAveragesThePixelsAroundTheClick()
    {
        byte[] white = { 255, 255, 255, 255 };
        byte[] black = { 0, 0, 0, 255 };
        var dot = SolidImage(5, 5, (x, y) => x == 2 && y == 2 ? white : black);

        int[] point = Selected(dot, 5, 5, 2.5, 2.5, 10, 0, 0);
        Assert.Equal(new[] { 12 }, point);

        // 译：3 × 3 均值是灰 28：黑在它的 30 以内，中央的白不在。
        int[] averaged = Selected(dot, 5, 5, 2.5, 2.5, 30, 1, 0);
        Assert.Equal(Enumerable.Range(0, 25).Where(i => i != 12).ToArray(), averaged);
    }

    /// <summary>
    /// 原 Swift：<c>MagicWandTests.swift:80 outlinesReproduceTheirPixelsWithHolesAndCornerTouches()</c>
    /// —— 2 条断言中的第 2 条已翻译（空掩码 ⇒ nil）。
    /// </summary>
    /// <remarks>
    /// 被描的 8 × 6 掩码（3 × 3 环 + 空洞 + 两个只斜角相接的像素）那条
    /// <c>#expect(pixels(outline(of: mask)) == expected)</c> 属"往返"断言：轮廓是
    /// <c>CGPath</c>，必须经 <c>DocumentSelection.coverage</c> 光栅化才能与像素集比较。
    /// C# 侧没有多边形光栅化器 → 见 <see cref="MagicWandTests_OutlineRoundTripNeedsRasterizer"/>（已跳过）。
    /// </remarks>
    [Fact]
    public void MagicWandTests_OutlineOfEmptyMaskIsNil()
    {
        // 原 Swift：outline(of: [UInt8](repeating: 0, count: 4), width: 2, height: 2) == nil
        // nil ⇔ wand_trace 一个闭环也没描出来（MagicWand.swift:65 guard loopCount > 0）。
        // status == 0 对应同函数里的 guard status == 0；points 为空是 loops 为空的必然推论
        // （每描一个闭环至少写一个角点），不是新增期望值。
        var empty = new byte[4];
        int status = WandPixels.Trace(empty, 2, 2, out int[] points, out int[] loops);
        Assert.Equal(0, status);
        Assert.True(loops.Length == 0 && points.Length == 0, "outline of an empty mask == nil");
    }

    /// <summary>
    /// 原 Swift：<c>MagicWandTests.swift:88</c> —— 第 1 条断言，<b>未翻译</b>。
    /// </summary>
    /// <remarks>
    /// <c>#expect(try pixels(MagicWand.outline(of: mask, 8, 6), 8, 6) == expected)</c>：
    /// 它要的是"把描出来的 <c>CGPath</c> 光栅化回 8 × 6 的灰度图、>= 128 的像素恰好等于原掩码的非零集"，
    /// 其中包含<b>空洞</b>（环绕方向相反的闭环）与<b>只斜角相接</b>的两点。
    /// <c>WandPixels.Trace</c> 只给角点环，没有对应的多边形填充实现，C# 侧无 CGPath 光栅化器可用，
    /// 期望值无法在 C 函数层面复现 → 不翻译、绝不改成"只看闭环数"之类更弱的断言。
    /// </remarks>
    [Fact(Skip = "未翻译：需要把 wand_trace 的角点环光栅化回像素（CGPath / DocumentSelection.coverage），C# 侧无多边形光栅化器（见方法注释）")]
    public void MagicWandTests_OutlineRoundTripNeedsRasterizer()
    {
    }

    /// <summary>
    /// 原 Swift：<c>MagicWandTests.swift:92 theWandReadsTheActiveLayerOrEveryVisibleLayerAndCombinesModes()</c>
    /// —— 6 条断言，<b>全部未翻译</b>。
    /// </summary>
    /// <remarks>
    /// 逐条原因（全部依赖未移植的 <c>EditorSession</c> / 图层 / 撤销栈）：
    /// <list type="bullet">
    /// <item><c>#expect(try selected().count == 200)</c>：当前活动图层是空白图层，整幅画布"全透明"因而全中；
    /// 判定输入来自 <c>session.selectionSample</c> 的"活动图层透明 ⇒ 什么都不画"分支。</item>
    /// <item><c>#expect(try selected() == left)</c>：依赖 <c>wandSettings.sampleAllLayers = true</c> 时
    /// <c>drawLiveComposite</c> 合成可见图层。</item>
    /// <item><c>#expect(try selected().count == 200)</c>（add 模式）：依赖 <c>applySelection(path, mode: .add)</c>。</item>
    /// <item><c>#expect(session.history.undoCount == count + 1)</c>：撤销栈。</item>
    /// <item><c>#expect(try selected() == Set(0..&lt;200).subtracting(left))</c>（subtract 模式）：选区模式合并。</item>
    /// <item><c>#expect(try selected().count == 200)</c>（<c>session.undo()</c> 之后）：撤销栈回滚。</item>
    /// </list>
    /// C# 侧只有 <c>WandPixels</c>（wand_mask / wand_trace / color_range_mask），没有会话、图层、
    /// 选区模式与撤销栈，以上 6 条一律无法构造。
    /// </remarks>
    [Fact(Skip = "未翻译：6 条断言全部依赖 EditorSession / 图层合成 / 选区模式 / 撤销栈，C# 侧无对应层（见方法注释）")]
    public void MagicWandTests_ReadsActiveLayerOrEveryVisibleLayerAndCombinesModes()
    {
    }

    /// <summary>
    /// 原 Swift：<c>MagicWandTests.swift:117 clickingInsideASelectionMakesANewWandSelectionRatherThanDeselecting()</c>
    /// —— 2 条断言，<b>全部未翻译</b>。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>#expect(session.selection != nil)</c>：会话选区状态。</item>
    /// <item><c>#expect(try pixels(session.selection?.path, 20, 10) == left)</c>：<c>CanvasView</c> +
    /// <c>NSWindow</c> + <c>NSEvent</c> + <c>session.viewport</c> 的视图坐标换算与点击分发，
    /// 再加 <c>Task.sleep</c> 轮询等待选区落地。C# 侧既无视图层也无选区状态。</item>
    /// </list>
    /// </remarks>
    [Fact(Skip = "未翻译：2 条断言依赖 CanvasView / NSWindow / NSEvent / viewport 与会话选区状态，C# 侧无对应层（见方法注释）")]
    public void MagicWandTests_ClickingInsideASelectionMakesANewWandSelection()
    {
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SpotHealingTests.swift
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Swift <c>blemished()</c>：竖直灰条纹（2 px 宽）＋中央 10 × 10 红斑（SpotHealingTests.swift:8-23）。</summary>
    private static byte[] Blemished(int width, int height, int stride)
    {
        var rgba = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool red = x >= 55 && x < 65 && y >= 35 && y < 45;
                byte gray = (byte)(x % 4 < 2 ? 100 : 112);
                int p = y * stride + x * 4;
                rgba[p] = red ? (byte)230 : gray;
                rgba[p + 1] = red ? (byte)20 : gray;
                rgba[p + 2] = red ? (byte)20 : gray;
                rgba[p + 3] = 255;
            }

        return rgba;
    }

    /// <summary>
    /// 笔刷覆盖：<c>BrushSettings(diameter: 24, hardness: 1)</c>，落笔于 (60, 40)。
    /// Swift 侧这层是 <c>BrushStroke</c> 盖出的 macOS 笔刷印，C# 的 <c>BrushPixels</c> 没有 coverage 光栅函数，
    /// 故按半径 12 的实心圆盘构造（<c>spot_heal</c> 只判 <c>coverage != 0</c>，见 HealPixels.cs:288）。
    /// </summary>
    private static byte[] BrushCoverage(int width, int height)
    {
        var coverage = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                long dx = x - 60, dy = y - 40;
                if (dx * dx + dy * dy <= 12 * 12) coverage[y * width + x] = 255;
            }

        return coverage;
    }

    private static int[] PixelAt(byte[] rgba, int stride, int x, int y)
    {
        int p = y * stride + x * 4;
        return new[] { rgba[p], rgba[p + 1], rgba[p + 2], rgba[p + 3] };
    }

    /// <summary>
    /// 原 Swift：<c>SpotHealingTests.swift:36 healsTheBlemishUnderTheBrushAndNothingElse(mode:)</c>
    /// —— 3 条断言里的第 2、3 条已翻译（第 1 条见下方跳过的测试）。
    /// </summary>
    /// <remarks>
    /// <c>SpotHealingMode.allCases</c> 的顺序即 C 的 mode 取值（BrushStroke.swift:899）：
    /// 0 = Content-Aware、1 = Create Texture、2 = Proximity Match。
    /// <c>opacity</c> 沿用 <c>BrushSettings.opacity</c> 默认值 1。
    /// Swift 传的是 <c>UInt32.random(in: .min ... .max)</c>；C# 无随机源，取固定种子，
    /// 随机量本身来自 <c>heal_hash(seed, 坐标)</c>，同 seed 必同结果。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SpotHealingTests_HealsTheBlemishUnderTheBrushAndNothingElse(int mode)
    {
        const int Width = 120, Height = 80, Stride = Width * 4;
        var rgba = Blemished(Width, Height, Stride);
        var before = (byte[])rgba.Clone();
        byte[] coverage = BrushCoverage(Width, Height);

        // Swift BrushStroke.swift:900 `guard spot_heal(...) == 0 else { throw ProjectError.tooLarge }`
        // 是原 #expect(brushError == nil && undoCount == count + 1) 中前半句的 C 层对应物。
        int status = HealPixels.SpotHeal(rgba, coverage, Width, Height, Stride, 1f, mode, 12345u);
        Assert.Equal(0, status);

        foreach ((int x, int y) in new[] { (60, 40), (56, 36), (64, 44) })
        {
            int[] healed = PixelAt(rgba, Stride, x, y);

            // 译：<mode>: (x, y) 是 [r, g, b, a]，没有被修成灰色底纹
            Assert.True(
                healed[0] - healed[1] < 30 && healed[1] >= 80 && healed[1] <= 130 && healed[3] == 255,
                $"{mode}: ({x}, {y}) is [{string.Join(",", healed)}], not healed into the gray surface");
        }

        // 远离笔刷处，一切不动。（Swift 原文注释）
        foreach ((int x, int y) in new[] { (10, 10), (90, 40), (30, 40), (60, 10), (60, 70) })
        {
            int[] after = PixelAt(rgba, Stride, x, y);
            int[] original = PixelAt(before, Stride, x, y);

            // 译：<mode>: (x, y) 在笔刷之外发生了变化
            Assert.True(
                after.SequenceEqual(original),
                $"{mode}: ({x}, {y}) changed outside the brush");
        }
    }

    /// <summary>
    /// 原 Swift：<c>SpotHealingTests.swift:48</c> —— 1 条断言，<b>未翻译</b>。
    /// </summary>
    /// <remarks>
    /// <c>#expect(session.brushError == nil &amp;&amp; session.history.undoCount == count + 1)</c>：
    /// 前半句的 C 层对应物已并入 <see cref="SpotHealingTests_HealsTheBlemishUnderTheBrushAndNothingElse"/>
    /// （<c>spot_heal</c> 返回 0）；后半句 <c>undoCount == count + 1</c> 依赖
    /// <c>EditorSession.history</c> 撤销栈，C# 侧无会话层，无法构造 → 不翻译。
    /// </remarks>
    [Fact(Skip = "未翻译：undoCount == count + 1 依赖 EditorSession.history 撤销栈，C# 侧无会话层（见方法注释）")]
    public void SpotHealingTests_BrushIsOneUndoStepWithoutError()
    {
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DistortTests.swift —— 19 条断言全部未翻译（详见各方法注释）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 原 Swift：<c>DistortTests.swift:13 perspectiveMappingHitsTheCornersAndDegenerateShapesAreRefused()</c>
    /// —— 7 条断言，<b>全部未翻译</b>。
    /// </summary>
    /// <remarks>
    /// 断言对象是 <c>DistortWarp.homography / isUsable / isConvex</c>（Swift 侧
    /// <c>Compositor/Document/Distort.swift:8</c> 的透视 / 折叠 warp 几何层）。
    /// <b>C# 侧不存在 <c>DistortWarp</c> 的任何对应实现</b>——<c>LensPixels.Distort</c> 是 C 的
    /// <c>lens_distort</c>（桶形畸变重采样，调用方是 Filters.swift:263 的镜头校正与
    /// AdjustPixels.c:1098 的相机原始光学），与透视 warp 不是同一个函数。
    /// 把 <c>homography/isUsable/isConvex</c> 在测试里重写一份只会测到测试自己，不构成对产品的验收，
    /// 故 7 条一律不翻译。
    /// </remarks>
    [Fact(Skip = "未翻译：7 条断言的对象 DistortWarp（homography/isUsable/isConvex）在 C# 侧无对应实现；LensPixels.Distort 是另一个函数（见方法注释）")]
    public void DistortTests_PerspectiveMappingHitsTheCornersAndDegenerateShapesAreRefused()
    {
    }

    /// <summary>
    /// 原 Swift：<c>DistortTests.swift:29 distortingWarpsTheLayerIntoTheShapeAsOneUndoStep()</c>
    /// —— 9 条断言，<b>全部未翻译</b>。
    /// </summary>
    /// <remarks>
    /// 逐条原因：<c>transformEdit?.corners?.count == 4 &amp;&amp; persistent == true</c>、
    /// <c>corners?[1] == shape[2]</c>、<c>transformEdit == nil &amp;&amp; undoCount == count + 1</c>、
    /// <c>transform.origin/size/rotation</c> 四条依赖 <c>EditorSession</c> 的变换编辑态与撤销栈；
    /// <c>alpha(50,12) == 255</c> / <c>alpha(15,25) == 255</c> / <c>alpha(50,28) == 0</c> /
    /// <c>alpha(80,12) == 0</c> 四条的像素来自 <c>ImageExporter</c> 渲染整个工程快照（透视 warp 走
    /// CoreImage / Metal，C# 侧无对应路径）；末条 <c>activeLayer?.transform.size == 20 × 20</c> 依赖撤销。
    /// </remarks>
    [Fact(Skip = "未翻译：9 条断言依赖 EditorSession 变换编辑态 / 撤销栈 / ImageExporter + CoreImage|Metal 透视 warp，C# 侧均无对应（见方法注释）")]
    public void DistortTests_DistortingWarpsTheLayerIntoTheShapeAsOneUndoStep()
    {
    }

    /// <summary>
    /// 原 Swift：<c>DistortTests.swift:70 distortedLayerIsTrimmedToItsVisiblePixels()</c>
    /// —— 3 条断言，<b>全部未翻译</b>。
    /// </summary>
    /// <remarks>
    /// <c>transform.size.width &lt; 20 &amp;&amp; height &lt;= 12</c>、<c>origin.x &gt;= 20 &amp;&amp; origin.y &gt;= 14</c>
    /// 两条断言的是"Apply 后按可见像素裁边"的图层变换结果（<c>DistortWarp.warpFolded</c> + 裁边，C# 侧无）；
    /// <c>bytes[(20 * width + 30) * 4 + 3] == 255</c> 来自 <c>ImageExporter</c> 渲染工程快照。
    /// </remarks>
    [Fact(Skip = "未翻译：3 条断言依赖 DistortWarp 裁边与 ImageExporter 渲染，C# 侧无对应（见方法注释）")]
    public void DistortTests_DistortedLayerIsTrimmedToItsVisiblePixels()
    {
    }
}