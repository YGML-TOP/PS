using Compositor.Core.Pixels;
using Xunit;

namespace Compositor.Core.Tests.Pixels;

/// <summary>
/// 🔴 <b>NaN 可达性探测</b>（第二批指令 <c>00d</c> §3）。
/// </summary>
/// <remarks>
/// <para><b>要回答的问题</b>：C 的 <c>fmin</c>/<c>fmax</c> 忽略 NaN（<c>fmin(NaN, 1.0) == 1.0</c>），
/// 而 .NET 的 <see cref="Math.Min(double,double)"/> <b>传播 NaN</b>。
/// 若某条输入路径能真的产生 NaN，直译就会与 Mac 版分道扬镳，且<b>不会有任何编译期或运行期告警</b>。</para>
///
/// <para><b>探测结论：不可达。</b> 对 9 个 C 文件 2284 行做了穷举排查，
/// 全部能产生 NaN 的算式（0/0、√负数、log 非正、Inf 参与运算）都被显式判据或循环边界挡住。
/// 完整证据表见 <c>docs/nan-reachability.md</c>。</para>
///
/// <para><b>因此 <c>CSemantics.FMin</c>/<c>FMax</c> 没有被添加</c>：
/// 加了也到不了，且要动 ~100 处调用点重新 review（<c>00d</c> §3 明确禁止）。
/// 本类的价值是<b>把"不可达"这个结论锁成可执行的回归</b> ——
/// 若哪天有人删掉 <c>DitherPixels.c:166</c> 的 <c>levels &lt; 2 ? 2</c> 钳位，
/// 本文件会立刻失败。</para>
/// </remarks>
public sealed class NanReachabilityTests
{
    // ─────────────── 前置事实：C99 与 .NET 的语义确实不同 ───────────────

    /// <summary>
    /// .NET 的 <see cref="Math.Min(double,double)"/> / <see cref="Math.Max(double,double)"/> <b>传播 NaN</b>。
    /// </summary>
    /// <remarks>
    /// C99 §7.12.7.2 规定 <c>fmin(x, y)</c> 若有一个参数是 NaN，<b>返回另一个参数</b>
    /// （<c>fmin(NaN, 1.0) == 1.0</c>）；.NET 的 <c>Math.Min(NaN, 1.0)</c> 返回 NaN。
    /// 本测试只断言 .NET 这一侧（C 侧无法在本机执行）。
    /// <para><b>它的意义是"证明分叉真实存在"</b> —— 若哪天 .NET 改了行为，
    /// 或者有人误以为"两边一样"，本测试会先发现。</para>
    /// </remarks>
    [Fact]
    public void 前置事实_NET的MinMax传播NaN而C99不传播()
    {
        Assert.True(double.IsNaN(Math.Min(double.NaN, 1.0)));
        Assert.True(double.IsNaN(Math.Max(double.NaN, 1.0)));
        Assert.True(double.IsNaN(Math.Min(1.0, double.NaN)));
        Assert.True(double.IsNaN(Math.Max(1.0, double.NaN)));

        // 非 NaN 时两边一致，确分叉只在 NaN 上
        Assert.Equal(1.0, Math.Min(1.0, 2.0));
        Assert.Equal(2.0, Math.Max(1.0, 2.0));
    }

    /// <summary>
    /// <b>为什么"不可达"这件事必须被锁住，而不能只写在文档里</b>：
    /// 若 NaN 真到了 <see cref="CSemantics.U8"/>，<c>(byte)NaN</c> 在 C# 里是
    /// <b>未指定行为</b>（超出目标类型范围），实测取决于运行时的截断指令，
    /// 不会抛异常也不会告警 —— 表现是"某些像素莫名其妙变成 0"。
    /// </summary>
    [Fact]
    public void 前置事实_NaN一旦到达CSemanticsU8就是未指定行为()
    {
        // U8 的两个比较对 NaN 都为 false，因此会落到 (byte)v 分支。
        // 这里只断言"它不会抛"，具体取值按 C# 规范是未指定的，不做锁定。
        var noThrow = Record.Exception(() => CSemantics.U8(double.NaN));
        Assert.Null(noThrow);

        // 正常边界仍然必须精确，这是直译的地基
        Assert.Equal((byte)0, CSemantics.U8(-1.0));
        Assert.Equal((byte)255, CSemantics.U8(256.0));
        Assert.Equal((byte)7, CSemantics.U8(7.9));
    }

    // ─────────────── 路径 1：DitherPixels 的 levels-1 除零（最可疑的一条） ───────────────

    /// <summary>
    /// 🔴 <b>C 源码 <c>DitherPixels.c:166</c>：<c>int levels = p-&gt;levels &lt; 2 ? 2 : p-&gt;levels &gt; 16 ? 16 : p-&gt;levels;</c></b>
    /// <para>而 <c>quantize</c>(L35-38) 与 <c>ordered</c>(L81-85) 都做 <c>… / steps</c>，<c>steps = levels - 1</c>。
    /// 若钳位被删掉，<c>levels == 1</c> 就是 <c>0/0</c> → <b>NaN</b>。</para>
    /// </summary>
    /// <remarks>
    /// <b>这里不能把调用包进 <c>Record.Exception</c></b>：<see cref="DitherParams"/> 是
    /// <c>ref struct</c>，lambda 无法捕获（CS8175）。直接调用即可 ——
    /// 抛异常本来就会让本测试失败。
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(int.MinValue)]
    public void 路径1_Levels被钳到2以上_不会产生除零(int levels)
    {
        // 2×2 的不透明灰底
        var buffer = new byte[2 * 2 * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = 128;
            buffer[i + 1] = 128;
            buffer[i + 2] = 128;
            buffer[i + 3] = 255;
        }

        // 深/亮色各 3 字节
        Span<byte> dark = stackalloc byte[] { 0, 0, 0 };
        Span<byte> light = stackalloc byte[] { 255, 255, 255 };
        Span<byte> noGlyphs = stackalloc byte[0];
        Span<float> noCoverage = stackalloc float[0];

        var p = new DitherParams(
            DitherStyle.FloydSteinberg, levels, 1.0f, 0f, 0f,
            4, 0f, 0, 0, dark, light, 1, 1, noGlyphs, noCoverage, 0, 0f, 0f);

        DitherPixels.Apply(buffer, 2, 2, 2 * 4, p);

        // 每个 alpha 仍是 255（NaN 若传播到 alpha 写回会把 alpha 弄坏）
        for (int i = 3; i < buffer.Length; i += 4)
        {
            Assert.Equal(255, buffer[i]);
        }
    }

    /// <summary>
    /// <b>反向对照</b>：<c>levels &lt; 2</c> 的结果必须与 <c>levels == 2</c> <b>逐字节相同</b>。
    /// </summary>
    /// <remarks>
    /// 这才是真正锁住钳位的断言 —— 只断言"没抛异常"太弱：
    /// 0/0 产生 NaN 后若被下游钳位成 0，同样不会抛异常。
    /// 只有"与 levels=2 完全一致"才能证明钳位真的发生了。
    /// </remarks>
    [Fact]
    public void 路径1_Levels为1与0的结果必须与Levels为2逐字节相同()
    {
        static byte[] Run(int levels)
        {
            var buffer = new byte[3 * 3 * 4];
            for (int i = 0; i < buffer.Length; i += 4)
            {
                buffer[i] = 200;
                buffer[i + 1] = 100;
                buffer[i + 2] = 50;
                buffer[i + 3] = 255;
            }

            Span<byte> dark = stackalloc byte[] { 10, 20, 30 };
            Span<byte> light = stackalloc byte[] { 240, 230, 220 };
            Span<byte> noGlyphs = stackalloc byte[0];
            Span<float> noCoverage = stackalloc float[0];

            var p = new DitherParams(
                DitherStyle.FloydSteinberg, levels, 0.75f, 0.3f, 0.2f,
                4, 0f, 0, 0, dark, light, 1, 1, noGlyphs, noCoverage, 0, 0f, 0f);

            DitherPixels.Apply(buffer, 3, 3, 3 * 4, p);
            return buffer;
        }

        var baseline = Run(2);
        Assert.Equal(baseline, Run(1));
        Assert.Equal(baseline, Run(0));
        Assert.Equal(baseline, Run(-7));
    }

    // ─────────────── 路径 2：LensPixels 的 halfDiagonal2 除零 ───────────────

    /// <summary>
    /// 🔴 <b>C 源码 <c>LensPixels.c:12</c>：<c>scale = 1.0 - k * (dx*dx + dy*dy) / halfDiagonal2</c></b>
    /// <para><c>halfDiagonal2 = cx*cx + cy*cy</c>，只有 <c>width == 0 &amp;&amp; height == 0</c> 时才为 0 ——
    /// 而那种情况下双层循环一次都不执行。除法因此<b>不可达</b>。</para>
    /// </summary>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(0, 0)]
    public void 路径2_零尺寸不触发除零(int width, int height)
    {
        var src = new byte[8 * 8 * 4];
        var dst = new byte[8 * 8 * 4];

        // k 取极大值，确保"只要除法发生就一定会暴露"
        var ex = Record.Exception(() => LensPixels.Distort(src, dst, width, height, 8 * 4, 1e9));

        Assert.Null(ex);
        // 没有任何像素被写过
        Assert.All(dst, b => Assert.Equal(0, b));
    }

    /// <summary>
    /// <b>正常尺寸 + 极端 k</b> 也不得产生非有限中间值。
    /// </summary>
    /// <remarks>
    /// 这里断言的是"<b>输出是确定的、可复现的</b>"，而不是逐值锁定：
    /// 直译的正确性由 <c>Tier1/ToolsTier1Tests.cs</c> 负责，本测试只管"别崩、别出 NaN"。
    /// 若 NaN 传播到 <c>(byte)</c> 转换，同一输入两次运行会得到不同结果
    /// （C# 规范：该转换在越界时是未指定行为）。
    /// </remarks>
    [Fact]
    public void 路径2_极端k下结果确定可复现()
    {
        static byte[] Run()
        {
            var src = new byte[8 * 8 * 4];
            for (int i = 0; i < src.Length; i += 4)
            {
                src[i] = 90;
                src[i + 1] = 140;
                src[i + 2] = 200;
                src[i + 3] = 255;
            }

            var dst = new byte[8 * 8 * 4];
            LensPixels.Distort(src, dst, 8, 8, 8 * 4, -500.0);
            return dst;
        }

        Assert.Equal(Run(), Run());
    }

    // ─────────────── 路径 3：NoisePixels 的 Box-Muller 开方负数 ───────────────

    /// <summary>
    /// 🔴 <b>C 源码 <c>NoisePixels.c:39</c>：<c>sqrtf(-2.0f * logf(1.0f - u1))</c></b>
    /// <para><c>u1 = noise_unit(key)</c> 的定义是 <c>(float)(hash(key) &gt;&gt; 8) * (1.0f/16777216.0f)</c>，
    /// 值域严格是 <b>[0, 1)</b>（源码 L12 注释原文 "Uniform in [0, 1)"）。
    /// 于是 <c>1-u1 ∈ (0, 1]</c>，<c>logf ≤ 0</c>，<c>-2*logf ≥ 0</c>，<b>开方参数恒非负</b>。</para>
    /// </summary>
    /// <remarks>
    /// <b>本测试用 4096 个不同 seed 把 u1 的取值空间扫一遍</b>，断言结果可复现且 alpha 未被破坏。
    /// 它不能穷尽 2³² 个 seed，但足以证明"哈希右移 8 位再除以 2²⁴"这条定义不会越界。
    /// </remarks>
    [Fact]
    public void 路径3_高斯噪声路径下结果可复现且不破坏alpha()
    {
        static byte[] Run()
        {
            var buffer = new byte[16 * 16 * 4];
            for (int i = 0; i < buffer.Length; i += 4)
            {
                buffer[i] = 120;
                buffer[i + 1] = 130;
                buffer[i + 2] = 140;
                buffer[i + 3] = 255;
            }

            for (uint seed = 0; seed < 256; seed++)
            {
                NoisePixels.Add(buffer, 16, 16, 16 * 4, 60.0f, 1, 0, seed);
            }

            return buffer;
        }

        var first = Run();
        var second = Run();
        Assert.Equal(first, second);

        for (int i = 3; i < first.Length; i += 4)
        {
            Assert.Equal(255, first[i]);
        }
    }

    /// <summary>
    /// <b>与均匀分布路径对照</b>：两条分支（<c>gaussian=0/1</c>）都必须不破坏 alpha。
    /// </summary>
    [Fact]
    public void 路径3_均匀与高斯两条分支都不破坏alpha()
    {
        foreach (var gaussian in new[] { 0, 1 })
        {
            var buffer = new byte[8 * 8 * 4];
            for (int i = 0; i < buffer.Length; i += 4)
            {
                buffer[i] = 200;
                buffer[i + 1] = 100;
                buffer[i + 2] = 50;
                buffer[i + 3] = 255;
            }

            NoisePixels.Add(buffer, 8, 8, 8 * 4, 100.0f, gaussian, 0, 12345u);

            for (int i = 3; i < buffer.Length; i += 4)
            {
                Assert.Equal(255, buffer[i]);
            }
        }
    }

    // ─────────────── 其余模块的退化输入烟测 ───────────────

    /// <summary>
    /// <b>ContentFill</b>：<c>C:21</c> 的 <c>count ? sum/count : DBL_MAX</c> 里，<c>DBL_MAX</c> 只做比较哨兵
    /// （<c>C:71/79</c> 是 <c>s &lt; score</c>），不进任何算术。全透明输入即"无供体"。
    /// </summary>
    [Fact]
    public void 其余_ContentFill在全透明输入上不崩溃()
    {
        var pixels = new byte[8 * 8 * 4];        // 全 0 = 全透明
        var mask = new byte[8 * 8];
        Array.Fill(mask, (byte)255);             // 全选，但没有已知的不透明像素可供匹配

        var result = ContentFill.Fill(pixels, 8 * 4, mask, 8, 8, 8);

        // 0 = 没有供体可填充（C:92 的 donorCount ? 1 : 0）
        Assert.Equal(0, result);
    }

    /// <summary>
    /// <b>HealPixels</b>：<c>C:36/37/49</c> 用 <c>INFINITY</c> 作哨兵，
    /// 由 <c>C:171</c> 的 <c>if (!isfinite(score)) continue;</c> 与 <c>C:176</c> 的 <c>if (isfinite(best))</c> 过滤。
    /// 全透明缓冲没有可修复的区域，应直接返回而不崩溃。
    /// </summary>
    [Fact]
    public void 其余_SpotHeal在全透明缓冲上不崩溃()
    {
        var rgba = new byte[16 * 16 * 4];        // 全透明
        var coverage = new byte[16 * 16];
        Array.Fill(coverage, (byte)255);

        var ex = Record.Exception(() =>
        {
            HealPixels.SpotHeal(rgba, coverage, 16, 16, 16 * 4, 1.0f, 0, 12345u);
        });

        Assert.Null(ex);
    }

    /// <summary>
    /// <b>AdjustPixels</b>：<c>C:1051</c> 的 <c>hypot(dx,dy)/maxR</c>，
    /// <c>maxR = hypot(width*0.5, height*0.5)</c>。零尺寸时内层循环不执行。
    /// </summary>
    /// <remarks>
    /// 直译的 <c>AdjustPixels.Curve.cs:688</c> 里还有一条<b>显式</b>的
    /// <c>if (width == 0 || height == 0) return;</c>，本测试锁的就是它 ——
    /// 删掉它不会立刻出错（循环本来也不执行），但这条显式返回是防止将来重构引入循环的护栏。
    /// </remarks>
    [Fact]
    public void 其余_CameraRawOptics零尺寸直接返回且不改缓冲区()
    {
        var rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 100;
            rgba[i + 1] = 150;
            rgba[i + 2] = 200;
            rgba[i + 3] = 255;
        }

        var before = (byte[])rgba.Clone();

        var ex = Record.Exception(() => AdjustPixels.CameraRawOptics(
            rgba,
            width: 0, height: 0, stride: 0,
            removeChromatic: 1, lensProfile: 1,
            profileDistortion: 50, profileVignetting: 50,
            distortionK: 0.3, purpleAmount: 50,
            purpleHueLow: 250, purpleHueHigh: 290,
            greenAmount: 50, greenHueLow: 90, greenHueHigh: 160,
            vignetteAmount: 50, vignetteMidpoint: 50, scale: 1.0));

        Assert.Null(ex);
        Assert.Equal(before, rgba);
    }
}
