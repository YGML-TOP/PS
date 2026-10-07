namespace Compositor.Selection.Tests;

using System.Collections.Generic;
using Compositor.Selection;
using Xunit;

/// <summary>
/// <see cref="ColorRangePanel"/> 的行为规格：取色、取色模式归约、fuzziness 滑杆语义。
/// </summary>
/// <remarks>
/// 【照抄组】全部对应 Mac 版 <c>Document/ColorRangeSelection.swift</c> 的具体行号。
/// 【推导组】由公式推导，不引用行号。
/// </remarks>
public class ColorRangePanelTests
{
    private static byte[] Pixel(byte r, byte g, byte b, byte a) => new byte[] { r, g, b, a };

    /// <summary>把若干行铺成 RGBA 图像缓冲。</summary>
    private static byte[] Image(int width, int height, params byte[][] pixels)
    {
        var buf = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            Array.Copy(pixels[i], 0, buf, i * 4, 4);
        }

        return buf;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:8  fuzzinessRange = 0...200
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :8 —— 滑杆范围是 0–200，<b>不是</b> 0–255。</summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(40.0, 40.0)]
    [InlineData(200.0, 200.0)]
    [InlineData(201.0, 200.0)]
    [InlineData(1000.0, 200.0)]
    [InlineData(-1.0, 0.0)]
    public void ClampFuzziness_UsesTheSliderRangeNotByteRange(double input, double expected) =>
        Assert.Equal(expected, ColorRangePanel.ClampFuzziness(input));

    /// <summary>照抄 :11 —— 缺省 fuzziness 是 40，且落在合法范围内。</summary>
    [Fact]
    public void FuzzinessDefault_IsForty()
    {
        Assert.Equal(40.0, ColorRangePanel.FuzzinessDefault);
        Assert.Equal(ColorRangePanel.FuzzinessDefault, ColorRangePanel.ClampFuzziness(ColorRangePanel.FuzzinessDefault));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:71  Int32(edit.fuzziness.rounded())
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 照抄 :71 —— 取整用 <b>AwayFromZero</b>。
    /// </summary>
    /// <remarks>
    /// 0.5 与 2.5 这两个中点是<b>决定性</b>的：C# 的 <c>Math.Round</c> 默认
    /// <c>ToEven</c>（银行家舍入）会给出 0 和 2，而 Swift 的 <c>.rounded()</c> 给 1 和 3。
    /// 其余的非中点用例两者一致，所以必须挑中点才有鉴别力。
    /// </remarks>
    [Theory]
    [InlineData(0.5, 1)]     // ToEven 会给 0 —— 错
    [InlineData(2.5, 3)]     // ToEven 会给 2 —— 错
    [InlineData(1.4, 1)]
    [InlineData(1.6, 2)]
    [InlineData(200.0, 200)]
    public void FuzzinessToInt_RoundsAwayFromZero(double input, int expected) =>
        Assert.Equal(expected, ColorRangePanel.FuzzinessToInt(input));

    /// <summary>照抄 :71 + :8 —— 先夹到 0–200，再取整。越界值不能绕过上界。</summary>
    [Theory]
    [InlineData(250.0, 200)]
    [InlineData(999.0, 200)]
    [InlineData(-40.0, 0)]
    public void FuzzinessToInt_ClampsBeforeRounding(double input, int expected) =>
        Assert.Equal(expected, ColorRangePanel.FuzzinessToInt(input));

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:17  held ?? sampleMode
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :17 —— 按住修饰键时用它，否则用面板选定的取色器。</summary>
    [Theory]
    [InlineData(null, HueSampleMode.Replace, HueSampleMode.Replace)]
    [InlineData(null, HueSampleMode.Add, HueSampleMode.Add)]
    [InlineData(HueSampleMode.Remove, HueSampleMode.Replace, HueSampleMode.Remove)]
    [InlineData(HueSampleMode.Add, HueSampleMode.Remove, HueSampleMode.Add)]
    public void EffectiveMode_PrefersHeldKey(HueSampleMode? held, HueSampleMode sample, HueSampleMode expected) =>
        Assert.Equal(expected, ColorRangePanel.EffectiveMode(held, sample));

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:55-59  取色归约
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :56 —— Replace 是「重新开始」：包含色整个替换，<b>排除色被清空</b>。</summary>
    [Fact]
    public void Apply_Replace_ResetsBothLists()
    {
        var include = new List<byte> { 1, 2, 3, 4, 5, 6 };   // 之前已取过两色
        var exclude = new List<byte> { 7, 8, 9 };

        ColorRangePanel.Apply(HueSampleMode.Replace, include, exclude, new byte[] { 10, 20, 30 });

        Assert.Equal(new byte[] { 10, 20, 30 }, include);
        Assert.Empty(exclude);
    }

    /// <summary>照抄 :57 —— Add 只往包含色尾部追加，<b>不动排除色</b>。</summary>
    [Fact]
    public void Apply_Add_AppendsToIncludeOnly()
    {
        var include = new List<byte> { 1, 2, 3 };
        var exclude = new List<byte> { 9, 9, 9 };

        ColorRangePanel.Apply(HueSampleMode.Add, include, exclude, new byte[] { 4, 5, 6 });

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, include);
        Assert.Equal(new byte[] { 9, 9, 9 }, exclude);
    }

    /// <summary>照抄 :58 —— Remove 只往排除色尾部追加，<b>不动包含色</b>。</summary>
    [Fact]
    public void Apply_Remove_AppendsToExcludeOnly()
    {
        var include = new List<byte> { 1, 2, 3 };
        var exclude = new List<byte> { 7, 8, 9 };

        ColorRangePanel.Apply(HueSampleMode.Remove, include, exclude, new byte[] { 4, 5, 6 });

        Assert.Equal(new byte[] { 1, 2, 3 }, include);
        Assert.Equal(new byte[] { 7, 8, 9, 4, 5, 6 }, exclude);
    }

    /// <summary>照抄 :55 —— Option 优先于 Shift：两者同按时应是 remove。</summary>
    [Fact]
    public void Apply_OptionBeatsShiftInTheModifierPrecedence()
    {
        // 照抄 :55 的 option ? .remove : shift ? .add : edit.sampleMode
        var include = new List<byte>();
        var exclude = new List<byte>();

        HueSampleMode mode = ColorRangePanel.EffectiveMode(
            held: HueSampleMode.Remove,          // Option 按住
            sampleMode: HueSampleMode.Add);      // 即使面板选的是 add

        ColorRangePanel.Apply(mode, include, exclude, new byte[] { 1, 1, 1 });

        Assert.Empty(include);
        Assert.Equal(new byte[] { 1, 1, 1 }, exclude);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:28  hasColors = !include.isEmpty
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>照抄 :28 —— <c>hasColors</c> 只看<b>包含色</b>。</summary>
    /// <remarks>
    /// Mac 版 <c>ColorRangeSelection.swift:28</c> 是 <c>var hasColors: Bool { !include.isEmpty }</c>，
    /// 签名里<b>根本没有 exclude</b> —— 这正是「只取排除色不算取过色」的实现方式。
    /// 所以本方法的签名也只收 include：语义上已经无法把排除色算进去。
    /// </remarks>
    [Fact]
    public void HasColors_OnlyLooksAtInclude()
    {
        Assert.False(ColorRangePanel.HasColors(new List<byte>()));
        Assert.True(ColorRangePanel.HasColors(new List<byte> { 1, 2, 3 }));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:99  Int(point.x.rounded(.down)) —— floor
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 照抄 :99 —— 小数坐标向<b>下</b>取整（floor），不是 AwayFromZero。
    /// </summary>
    /// <remarks>
    /// <b>手算。</b>用 3×1 的横条（高为 1，3×3 邻域在纵向被裁成只有第 0 行），
    /// 三个像素分别是 R=(255,0,0,255)、B=(0,0,255,255)、G=(0,255,0,255)。
    /// 在 <c>pointX = 0.9</c> 取色：
    /// <list type="bullet">
    /// <item><description>
    /// <b>floor(0.9) = 0</b> → 邻域是 x∈{0,1}，即 {R,B}。
    /// sums = (255, 0, 255, 510)。
    /// <c>(255*255 + 255)/510 = 65280/510 = 128</c>，
    /// <c>(0 + 255)/510 = 0</c>，<c>(255*255 + 255)/510 = 128</c> → <b>(128, 0, 128)</b>。
    /// </description></item>
    /// <item><description>
    /// 若误用 <c>AwayFromZero</c>：<c>round(0.9) = 1</c> → 邻域是 x∈{0,1,2}，即 {R,B,G}。
    /// sums = (255, 255, 255, 765)，
    /// <c>(255*255 + 382)/765 = 65407/765 = 85.49… → 85</c> → <b>(85, 85, 85)</b>。
    /// </description></item>
    /// </list>
    /// (128,0,128) 与 (85,85,85) 毫无重叠，取舍一眼可见。
    /// </remarks>
    [Fact]
    public void Sample3x3_RoundsPointDownNotAwayFromZero()
    {
        byte[] img = Image(
            3, 1,
            Pixel(255, 0, 0, 255), Pixel(0, 0, 255, 255), Pixel(0, 255, 0, 255));

        byte[]? color = ColorRangePanel.Sample3x3(img, 3, 1, 12, 0.9, 0.0);

        Assert.NotNull(color);
        Assert.Equal(new byte[] { 128, 0, 128 }, color!);
    }

    /// <summary>
    /// 照抄 :99 + :100 —— 负小数坐标 floor 后越界，返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 手算：<c>floor(-0.2) = -1</c>，而 <c>(0..&lt;width).contains(-1)</c> 为假 → 取不到颜色。
    /// 若误用 <c>AwayFromZero</c>，<c>round(-0.2) = 0</c>，会落在合法的 (0,1) 上并返回颜色 ——
    /// 两种实现给出 null 与非 null，无重叠。
    /// </remarks>
    [Fact]
    public void Sample3x3_NegativeFractionFloorsOutOfBounds()
    {
        byte[] img = Image(
            4, 3,
            Pixel(10, 20, 30, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255),
            Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255),
            Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255));

        Assert.Null(ColorRangePanel.Sample3x3(img, 4, 3, 16, -0.2, 1.0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:111  min(255, …) —— 结果要夹到 255
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 照抄 :111 —— 反预乘结果<b>夹到 255</b>。
    /// </summary>
    /// <remarks>
    /// 手算：1×1 图，RGBA = (255, 0, 0, 128)。3×3 邻域被裁剪成只剩这一个像素，
    /// 所以 sums = (255, 0, 0, 128)。
    /// <c>color[0] = min(255, (255*255 + 128/2) / 128) = min(255, (65025 + 64) / 128)
    /// = min(255, 65089/128) = min(255, 508) = 255</c>。
    /// <para>
    /// 漏掉 <c>min(255, …)</c> 会得到 <b>508</b>，而 <c>byte</c> 转换在 unchecked 下会静默取模
    /// 成 <c>508 &amp; 0xFF = 252</c> —— 正是本项目记录的「窄化溢出静默取模」坑。
    /// </para>
    /// </remarks>
    [Fact]
    public void Sample3x3_ClampsUnpremultipliedResultTo255()
    {
        byte[] img = Image(1, 1, Pixel(255, 0, 0, 128));

        byte[]? color = ColorRangePanel.Sample3x3(img, 1, 1, 4, 0, 0);

        Assert.NotNull(color);
        Assert.Equal(255, color![0]);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 【照抄组】ColorRangeSelection.swift:98-112  3×3 邻域与反预乘
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>推导组：全同色区域取色结果就是该颜色本身（反预乘后还原）。</summary>
    /// <remarks>
    /// 手算：9 个像素全为 (200,100,50,255)，sums = (1800, 900, 450, 2295)。
    /// <c>(1800*255 + 1147)/2295 = 460147/2295 = 200.49… → 200</c>；
    /// <c>(900*255 + 1147)/2295 = 230647/2295 = 100.49… → 100</c>；
    /// <c>(450*255 + 1147)/2295 = 115897/2295 = 50.49… → 50</c>。
    /// </remarks>
    [Fact]
    public void Sample3x3_UniformRegionReturnsThatColor()
    {
        byte[] img = Image(
            3, 3,
            Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255),
            Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255),
            Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255));

        byte[]? color = ColorRangePanel.Sample3x3(img, 3, 3, 12, 1, 1);

        Assert.NotNull(color);
        Assert.Equal(new byte[] { 200, 100, 50 }, color!);
    }

    /// <summary>推导组：邻域确实是 3×3，会把周围像素平均进来。</summary>
    /// <remarks>
    /// 手算：3×3 全为 (200,100,50,255)，仅中心 (1,1) 是 (0,0,0,255) 纯黑不透明。
    /// sums = (8*200, 8*100, 8*50, 9*255) = (1600, 800, 400, 2295)。
    /// <c>(1600*255 + 1147)/2295 = 409147/2295 = 178.27… → 178</c>；
    /// <c>(800*255 + 1147)/2295 = 205147/2295 = 89.38… → 89</c>；
    /// <c>(400*255 + 1147)/2295 = 103147/2295 = 44.94… → 44</c>。
    /// </remarks>
    [Fact]
    public void Sample3x3_AveragesTheWholeNeighbourhood()
    {
        byte[] img = Image(
            3, 3,
            Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255),
            Pixel(200, 100, 50, 255), Pixel(0, 0, 0, 255), Pixel(200, 100, 50, 255),
            Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255), Pixel(200, 100, 50, 255));

        byte[]? color = ColorRangePanel.Sample3x3(img, 3, 3, 12, 1, 1);

        Assert.NotNull(color);
        Assert.Equal(new byte[] { 178, 89, 44 }, color!);
    }

    /// <summary>
    /// 推导组：3×3 邻域在图像边缘被<b>裁剪</b>，分母是<b>实际 alpha 和</b>而非固定 9。
    /// </summary>
    /// <remarks>
    /// 手算：1×1 图，像素 (100,60,30,200)。邻域被裁成只剩它自己，
    /// sums = (100, 60, 30, 200)。
    /// <c>(100*255 + 100)/200 = 25600/200 = 128</c>；
    /// <c>(60*255 + 100)/200 = 15400/200 = 77</c>；
    /// <c>(30*255 + 100)/200 = 7750/200 = 38</c>。
    /// <para>
    /// 若误把分母当成「9 个样本 × 255」或「固定 9 个样本」，
    /// 会得到 255 / 1700 之类完全不同的值。所以这条能钉死「分母是实际 alpha 和」。
    /// </para>
    /// </remarks>
    [Fact]
    public void Sample3x3_ClipsNeighbourhoodButDividesByActualAlphaSum()
    {
        byte[] img = Image(1, 1, Pixel(100, 60, 30, 200));

        byte[]? color = ColorRangePanel.Sample3x3(img, 1, 1, 4, 0, 0);

        Assert.NotNull(color);
        Assert.Equal(new byte[] { 128, 77, 38 }, color!);
    }

    /// <summary>推导组：邻域 alpha 全为 0 时取不到颜色（照抄 :110）。</summary>
    [Fact]
    public void Sample3x3_TransparentNeighbourhoodReturnsNull()
    {
        byte[] img = Image(
            3, 3,
            Pixel(255, 255, 255, 0), Pixel(0, 0, 0, 0), Pixel(0, 0, 0, 0),
            Pixel(0, 0, 0, 0), Pixel(0, 0, 0, 0), Pixel(0, 0, 0, 0),
            Pixel(0, 0, 0, 0), Pixel(0, 0, 0, 0), Pixel(0, 0, 0, 0));

        Assert.Null(ColorRangePanel.Sample3x3(img, 3, 3, 12, 1, 1));
    }

    /// <summary>推导组：坐标越界返回 null（照抄 :100）。</summary>
    [Theory]
    [InlineData(4.0, 1.0)]   // x == width，越界
    [InlineData(1.0, 3.0)]   // y == height，越界
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, -1.0)]
    public void Sample3x3_OutOfBoundsReturnsNull(double x, double y)
    {
        byte[] img = Image(
            4, 3,
            Pixel(9, 9, 9, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255),
            Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255),
            Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255), Pixel(0, 0, 0, 255));

        Assert.Null(ColorRangePanel.Sample3x3(img, 4, 3, 16, x, y));
    }

    /// <summary>推导组：stride 大于一行时按行距寻址，不是按 width*4。</summary>
    /// <remarks>
    /// <b>手算。</b>用 1×1 的图、stride = 8：有效像素是 <c>buf[0..3]</c> = (11,22,33,255)，
    /// 而 <c>buf[4..7]</c> 是行内填充的垃圾字节。
    /// 3×3 邻域在 1×1 上被裁成只剩这一个像素，sums = (11, 22, 33, 255)。
    /// <c>(11*255 + 127)/255 = 2932/255 = 11.49… → 11</c>，
    /// <c>(22*255 + 127)/255 = 5737/255 = 22.49… → 22</c>，
    /// <c>(33*255 + 127)/255 = 8542/255 = 33.49… → 33</c>。
    /// <para>
    /// 若误用 <c>width * 4 = 4</c> 当步长，就会读到 <c>buf[4..7]</c> 的填充垃圾，颜色完全不对。
    /// </para>
    /// </remarks>
    [Fact]
    public void Sample3x3_HonoursStridePadding()
    {
        var buf = new byte[8];
        Pixel(11, 22, 33, 255).CopyTo(buf, 0);   // 有效像素 (0,0)
        Pixel(200, 200, 200, 255).CopyTo(buf, 4); // 行内填充垃圾，不得被当成像素

        byte[]? color = ColorRangePanel.Sample3x3(buf, 1, 1, 8, 0, 0);

        Assert.NotNull(color);
        Assert.Equal(new byte[] { 11, 22, 33 }, color!);
    }
}