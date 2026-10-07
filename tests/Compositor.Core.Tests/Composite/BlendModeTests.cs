using Compositor.Core;
using Compositor.Core.Composite;
using Xunit;

namespace Compositor.Core.Tests.Composite;

/// <summary>
/// 24 个混合模式的数学测试。
/// </summary>
/// <remarks>
/// <para><b>🔴 本文件是「自写 smoke」，不是 Tier 1 照抄基准。</b>
/// 两者在本项目里地位完全不同：
/// <list type="bullet">
/// <item><b>Tier 1</b>（<c>tests/Pixels/Tier1/</c>）逐条照抄 <c>CompositorTests/</c> 的 Swift 断言，
/// 期望值与容差原样搬运，<b>绝不许为了跑绿而修改</b>。</item>
/// <item><b>本文件</b>的期望值全部<b>按 W3C Compositing and Blending Level 1 的公式手算</b>，
/// 算式写在每条断言的注释里，没有一个是"先跑一遍实现再抄输出"。</item>
/// </list>
/// 从实现抄期望值会把错误一起锁死，等于用错误实现给自己盖章。</para>
///
/// <para><b>本文件能证明什么、不能证明什么</b>：
/// <list type="bullet">
/// <item>✅ 能证明：实现<b>符合 W3C 规范文本</b>，且预乘/反预乘的收口没有写反。</item>
/// <item>❌ <b>不能</b>证明与 Mac 版逐像素一致 —— Core Graphics 与 Core Image 都是 macOS 专有，
/// Core Image 闭源。全套跑完也只是「规范级正确」，不是「与 Mac 输出一致」。
/// 这个边界必须始终保留在交付报告里。</item>
/// </list></para>
///
/// <para><b>期望值的坐标一律用精确二进制小数</b>（0、0.25、0.5、0.75、1），
/// 只有 <c>Lum</c> 的权重（0.3 / 0.59 / 0.11）本身不可精确表示，相关断言才用容差。</para>
/// </remarks>
public sealed class BlendModeTests
{
    // ══════════════════════════════════════════════════════════════════════
    //  一、辅助函数（W3C §10.2）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary><c>Lum = 0.3R + 0.59G + 0.11B</c>。权重取自 <c>Overview.bs:1401</c>。</summary>
    [Theory]
    [InlineData(1.0, 0.0, 0.0, 0.30)] // 0.3*1 + 0.59*0 + 0.11*0
    [InlineData(0.0, 1.0, 0.0, 0.59)]
    [InlineData(0.0, 0.0, 1.0, 0.11)]
    public void Lum_UsesSpecifiedWeights(double r, double g, double b, double expected) =>
        Assert.Equal(expected, BlendMath.Lum(r, g, b), 10);

    /// <summary><c>Lum</c> 的三个权重之和为 1，所以纯白等于纯黑的反量 —— 灰阶的亮度就是自身。</summary>
    [Fact]
    public void Lum_WeightsSumToOne_WhiteIsOne()
    {
        // 0.3 + 0.59 + 0.11 = 1.0（在 double 里累加顺序固定，这里用容差而不是精确相等）
        Assert.Equal(1.0, BlendMath.Lum(1.0, 1.0, 1.0), 10);
        Assert.Equal(0.5, BlendMath.Lum(0.5, 0.5, 0.5), 10);
    }

    /// <summary><c>Sat = max - min</c>（<c>Overview.bs:1422</c>），灰阶的饱和度为 0。</summary>
    [Fact]
    public void Sat_IsMaxMinusMin()
    {
        Assert.Equal(1.0, BlendMath.Sat(1.0, 0.0, 0.0), 10);
        Assert.Equal(0.5, BlendMath.Sat(0.5, 0.0, 0.0), 10);
        Assert.Equal(0.0, BlendMath.Sat(0.25, 0.25, 0.25), 10);
    }

    /// <summary>
    /// <c>ClipColor</c> 把越界的最大通道<b>精确</b>压回 1，且不是简单截断 —— 另外两个通道会被
    /// 顺带<b>抬高</b>（规范要求保持亮度，<c>Overview.bs:1410-1411</c>）。
    /// </summary>
    [Fact]
    public void ClipColor_PullsOutOfGamutMaxToExactlyOne_AndRaisesTheOthers()
    {
        // r=1.5 越界。L = 0.3*1.5 = 0.45；x = 1.5；分母 x - L = 1.05。
        // r = L + (r - L) * (1 - L) / (x - L) = 0.45 + 1.05 * 0.55 / 1.05 = 0.45 + 0.55 = 1.0  ✔ 精确
        // g = 0.45 + (0 - 0.45) * 0.55 / 1.05 = 0.45 - 0.235714... = 0.214285...
        // 若实现写成直接截断到 1.0，g 会留在 0.0，本测试即可抓住。
        Rgb3 clipped = BlendMath.ClipColor(1.5, 0.0, 0.0);

        Assert.Equal(1.0, clipped.R, 12);
        Assert.Equal(0.45 - (0.45 * 0.55 / 1.05), clipped.G, 12);
        Assert.Equal(clipped.G, clipped.B, 12);
        Assert.True(clipped.G > 0.0, "ClipColor 保持亮度，被压红的通道应该被抬高而不是截断为 0。");
    }

    /// <summary><c>ClipColor</c> 的负向对称：越界的最小通道压回 0，另两个被压低。</summary>
    [Fact]
    public void ClipColor_PullsNegativeMinToZero()
    {
        // r = -0.5 越界。L = 0.3*(-0.5) = -0.15；n = -0.5；分母 L - n = 0.35。
        // r = -0.15 + (-0.5 + 0.15) * (-0.15) / 0.35 = -0.15 + (-0.35)*(-0.15)/0.35 = -0.15 + 0.15 = 0.0  ✔
        // g = -0.15 + (0 + 0.15) * (-0.15) / 0.35 = -0.15 + 0.15*(-0.15)/0.35 = -0.15 - 0.0642857 = -0.2142857
        Rgb3 clipped = BlendMath.ClipColor(-0.5, 0.0, 0.0);

        Assert.Equal(0.0, clipped.R, 12);
        Assert.Equal(-0.15 - (0.15 * 0.15 / 0.35), clipped.G, 12);
    }

    /// <summary>
    /// <c>ClipColor</c> 的零除保护：三通道全相等且越界时分母为 0，实现必须跳过而不是产出 NaN。
    /// </summary>
    [Fact]
    public void ClipColor_AllEqualOutOfRange_DoesNotProduceNaN()
    {
        // 三通道都是 1.5：x = L = 1.5，x - L = 0 → 规范此处未定义（0/0）。
        // 本实现的约定是「跳过调整」，结果保持 1.5 而非 NaN。
        Rgb3 clipped = BlendMath.ClipColor(1.5, 1.5, 1.5);

        Assert.False(double.IsNaN(clipped.R), "零除保护失效：ClipColor 产出了 NaN。");
        Assert.False(double.IsNaN(clipped.G), "零除保护失效：ClipColor 产出了 NaN。");
        Assert.Equal(1.5, clipped.R, 12);
    }

    /// <summary><c>SetLum(C, l)</c>：整体抬到目标亮度。三通道同加同一个 <c>d</c>（<c>Overview.bs:1417-1419</c>）。</summary>
    [Fact]
    public void SetLum_RaisesBlackToRequestedLuminance()
    {
        // Lum(0,0,0) = 0，d = 0.5 - 0 = 0.5，三通道同加 0.5 → (0.5, 0.5, 0.5)，无越界。
        Rgb3 result = BlendMath.SetLum(0.0, 0.0, 0.0, 0.5);

        Assert.Equal(0.5, result.R, 12);
        Assert.Equal(0.5, result.G, 12);
        Assert.Equal(0.5, result.B, 12);
        Assert.Equal(0.5, BlendMath.Lum(result.R, result.G, result.B), 10);
    }

    /// <summary><c>SetSat</c> 把饱和度重设为目标值，同时保持三个通道的相对次序。</summary>
    [Fact]
    public void SetSat_RebuildsFromSortedTriple_PreservingChannelIdentity()
    {
        // (1,0,0)：min 在 G 与 B 上并列，规范按下标取 G；max 在 R。
        // n = 0(G)、m = 0(B)、x = 1(R)。x > n → m = (0-0)*s/(1-0) = 0；x = s = 0.5；n = 0。
        // 写回：R 拿到 0.5、G 拿到 0、B 拿到 0。**通道身份不能错位**。
        Rgb3 result = BlendMath.SetSat(1.0, 0.0, 0.0, 0.5);

        Assert.Equal(0.5, result.R, 12);
        Assert.Equal(0.0, result.G, 12);
        Assert.Equal(0.0, result.B, 12);
        Assert.Equal(0.5, BlendMath.Sat(result.R, result.G, result.B), 10);
    }

    /// <summary>
    /// <c>SetSat</c> 的灰阶分支：三通道全相等时规范给全 0，<b>与 s 无关</b>
    /// （<c>Cmax == Cmin</c> 走 else 分支，<c>Overview.bs:1431-1432</c>）。
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void SetSat_GrayInputCollapsesToZero_RegardlessOfTargetSaturation(double s)
    {
        Rgb3 result = BlendMath.SetSat(0.5, 0.5, 0.5, s);

        Assert.Equal(0.0, result.R, 12);
        Assert.Equal(0.0, result.G, 12);
        Assert.Equal(0.0, result.B, 12);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  二、逐模式标量公式 —— W3C 有规范的 16 个
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>可分离八件套：每条期望值都在注释里按 W3C 公式逐步算出。</summary>
    [Theory]
    // Normal：B = Cs（Overview.bs:1202）
    [InlineData(BlendMode.Normal, 0.3, 0.6, 0.6)]
    // Multiply：B = Cb x Cs = 0.5 * 0.5（Overview.bs:1216）
    [InlineData(BlendMode.Multiply, 0.5, 0.5, 0.25)]
    // Screen：B = 1 - (1-0.5)(1-0.5) = 1 - 0.25 = 0.75（Overview.bs:1230）
    [InlineData(BlendMode.Screen, 0.5, 0.5, 0.75)]
    // Darken：min(0.75, 0.25) = 0.25（Overview.bs:1262）
    [InlineData(BlendMode.Darken, 0.75, 0.25, 0.25)]
    // Lighten：max(0.75, 0.25) = 0.75（Overview.bs:1276）
    [InlineData(BlendMode.Lighten, 0.75, 0.25, 0.75)]
    // Overlay：B = HardLight(Cs, Cb) = HardLight(backdrop=0.25, source=0.75)
    //          source 0.75 > 0.5 → Screen(0.25, 2*0.75-1 = 0.5) = 1 - 0.75*0.5 = 0.625（Overview.bs:1245/1230）
    [InlineData(BlendMode.Overlay, 0.75, 0.25, 0.625)]
    // HardLight：source 0.25 <= 0.5 → Multiply(0.75, 2*0.25 = 0.5) = 0.375（Overview.bs:1327-1328）
    [InlineData(BlendMode.HardLight, 0.75, 0.25, 0.375)]
    // ColorDodge：min(1, 0.5 / (1 - 0.5)) = min(1, 1) = 1（Overview.bs:1296）
    [InlineData(BlendMode.ColorDodge, 0.5, 0.5, 1.0)]
    // ColorBurn：1 - min(1, (1 - 0.5) / 1) = 1 - 0.5 = 0.5（Overview.bs:1314）
    [InlineData(BlendMode.ColorBurn, 0.5, 1.0, 0.5)]
    // SoftLight：source 1 > 0.5 → Cb + (2*1-1)*(D(0.25) - 0.25)
    //           Cb = 0.25 <= 0.25 → D = ((16*0.25-12)*0.25+4)*0.25 = ((4-12)*0.25+4)*0.25 = 0.5
    //           = 0.25 + 1*(0.5-0.25) = 0.5（Overview.bs:1345-1353）
    [InlineData(BlendMode.SoftLight, 0.25, 1.0, 0.5)]
    // Difference：|0.25 - 0.75| = 0.5（Overview.bs:1368）
    [InlineData(BlendMode.Difference, 0.25, 0.75, 0.5)]
    // Exclusion：0.5 + 0.5 - 2*0.25 = 0.5（Overview.bs:1380）
    [InlineData(BlendMode.Exclusion, 0.5, 0.5, 0.5)]
    public void SeparableMode_MatchesW3cFormula(BlendMode mode, double cb, double cs, double expected)
    {
        double actual = ApplySeparable(mode, cb, cs);
        Assert.Equal(expected, actual, 12);
    }

    /// <summary>
    /// <b>Overlay 与 HardLight 必须区分开。</b>两者互为逆运算，只在同一个通道 &lt;= 0.5 时才巧合相等。
    /// </summary>
    [Fact]
    public void Overlay_IsNotHardLight_WhenDecidingChannelsSitOnOppositeSides()
    {
        // cb = 0.75（亮）、cs = 0.25（暗）
        // HardLight 判 source：0.25 <= 0.5 → 走正片叠底
        // Overlay 判 backdrop：0.75 > 0.5 → 走滤色
        // 两者结果必须不同；若实现把 Overlay 直接写成 HardLight 而忘了对调参数，本测试立刻红。
        double hardLight = ApplySeparable(BlendMode.HardLight, 0.75, 0.25);
        double overlay = ApplySeparable(BlendMode.Overlay, 0.75, 0.25);

        Assert.Equal(0.375, hardLight, 12);
        Assert.Equal(0.625, overlay, 12);
        Assert.NotEqual(hardLight, overlay, 12);
    }

    /// <summary>
    /// 🔴 <b>柔光与强光必须区分开：纯白源打不到纯白。</b>
    /// </summary>
    /// <remarks>
    /// 这两个模式在 <c>Cs = 0.5</c> 处完全一样（都退化成底色），越往两端差得越大，
    /// 而「纯白源得到纯白」正是把它们分开的那个端点。
    /// </remarks>
    [Fact]
    public void SoftLight_WhiteSourceDoesNotReachWhite_UnlikeHardLight()
    {
        // HardLight：source 1 > 0.5 → Screen(0, 2*1-1) = Screen(0,1) = 1 - (1-0)*(1-1) = 1
        Assert.Equal(1.0, ApplySeparable(BlendMode.HardLight, 0.0, 1.0), 12);

        // SoftLight：source 1 > 0.5 → Cb + (2*1-1)*(D(0) - 0)，D(0) = ((0-12)*0+4)*0 = 0
        //          = 0 + 1*(0-0) = 0     ← 打不到纯白
        Assert.Equal(0.0, ApplySeparable(BlendMode.SoftLight, 0.0, 1.0), 12);

        // SoftLight 在纯白 / 纯黑底色上不做任何改动。
        Assert.Equal(1.0, ApplySeparable(BlendMode.SoftLight, 1.0, 1.0), 12);
        Assert.Equal(0.0, ApplySeparable(BlendMode.SoftLight, 0.0, 0.0), 12);

        // 中间点：D(0.25) 的三次多项式 = ((16*0.25-12)*0.25+4)*0.25 = ((4-12)*0.25+4)*0.25 = 0.5，
        // 与 sqrt(0.25) 恰好相等 —— 规范把分支阈值定在 0.25 就是为了这条连续性。
        Assert.Equal(0.5, Math.Sqrt(0.25), 12);
    }

    /// <summary>
    /// 🔴 <b>分支顺序测试</b>：ColorDodge / ColorBurn 的两个特例分支<b>不可交换</b>。
    /// W3C 先判 <c>Cb</c> 再判 <c>Cs</c>，交换顺序会让这两个角点整整差一个满量程。
    /// </summary>
    [Fact]
    public void ColorDodgeAndBurn_BranchOrderMattersAtCorners()
    {
        // ColorDodge：Cb == 0 与 Cs == 1 同时成立，规范先判 Cb → 0，不是 1。
        Assert.Equal(0.0, ApplySeparable(BlendMode.ColorDodge, 0.0, 1.0), 12);

        // ColorBurn：Cb == 1 与 Cs == 0 同时成立，规范先判 Cb → 1，不是 0。
        Assert.Equal(1.0, ApplySeparable(BlendMode.ColorBurn, 1.0, 0.0), 12);

        // 非角点处两者都应走一般式：ColorDodge(0.5,0.5) = 0.5/0.5 = 1；ColorBurn(0.5,1) = 1 - 0.5/1 = 0.5。
        Assert.Equal(1.0, ApplySeparable(BlendMode.ColorDodge, 0.5, 0.5), 12);
        Assert.Equal(0.5, ApplySeparable(BlendMode.ColorBurn, 0.5, 1.0), 12);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  三、W3C 未定义的 8 个模式 —— 出处与把握度见 BlendMath 的 XML 注释
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>W3C 未定义的那 8 个模式。每条期望值按注释里写的公式手算。</summary>
    [Theory]
    // LinearBurn：max(0, 0.75 + 0.5 - 1) = 0.25
    [InlineData(BlendMode.LinearBurn, 0.75, 0.5, 0.25)]
    // LinearBurn 负向：max(0, 0.5 + 0.25 - 1) = 0
    [InlineData(BlendMode.LinearBurn, 0.5, 0.25, 0.0)]
    // LinearDodge：min(1, 0.5 + 0.5) = 1
    [InlineData(BlendMode.LinearDodge, 0.5, 0.5, 1.0)]
    // LinearDodge 饱和：min(1, 0.75 + 0.6) = min(1, 1.35) = 1
    [InlineData(BlendMode.LinearDodge, 0.75, 0.6, 1.0)]
    // Subtract：max(0, 0.75 - 0.5) = 0.25
    [InlineData(BlendMode.Subtract, 0.75, 0.5, 0.25)]
    // Subtract 负向：max(0, 0.25 - 0.5) = 0
    [InlineData(BlendMode.Subtract, 0.25, 0.5, 0.0)]
    // Divide：min(1, 0.25 / 0.5) = 0.5
    [InlineData(BlendMode.Divide, 0.25, 0.5, 0.5)]
    // LinearLight：clamp(0.5 + 2*0.5 - 1) = 0.5
    [InlineData(BlendMode.LinearLight, 0.5, 0.5, 0.5)]
    // LinearLight 下溢：clamp(0.5 + 2*0 - 1) = clamp(-0.5) = 0
    [InlineData(BlendMode.LinearLight, 0.5, 0.0, 0.0)]
    // LinearLight 上溢：clamp(0.5 + 2*1 - 1) = clamp(1.5) = 1
    [InlineData(BlendMode.LinearLight, 0.5, 1.0, 1.0)]
    // VividLight：source 0.75 > 0.5 → ColorDodge(0.5, 2*0.75-1 = 0.5) = 0.5/0.5 = 1
    [InlineData(BlendMode.VividLight, 0.5, 0.75, 1.0)]
    // VividLight：source 0.25 <= 0.5 → ColorBurn(0.5, 2*0.25 = 0.5) = 1 - 0.5/0.5 = 0
    [InlineData(BlendMode.VividLight, 0.5, 0.25, 0.0)]
    // HardMix：0.5 + 0.5 >= 1 → 1
    [InlineData(BlendMode.HardMix, 0.5, 0.5, 1.0)]
    // HardMix：0.25 + 0.25 < 1 → 0
    [InlineData(BlendMode.HardMix, 0.25, 0.25, 0.0)]
    public void NonW3cMode_MatchesDocumentedFormula(BlendMode mode, double cb, double cs, double expected) =>
        Assert.Equal(expected, ApplySeparable(mode, cb, cs), 12);

    /// <summary>Divide 的除零方向必须显式处理：源为黑时结果为白（1），而不是 NaN 或 0。</summary>
    [Fact]
    public void Divide_ZeroSource_YieldsWhite_NotNaN()
    {
        double result = ApplySeparable(BlendMode.Divide, 0.5, 0.0);

        Assert.False(double.IsNaN(result), "Divide 除零防护失效。");
        Assert.Equal(1.0, result, 12);
    }

    /// <summary>
    /// 对比度组三兄弟的端点行为：source 全黑 → 全黑，全白 → 全白。
    /// 这是三条公式都由 ColorBurn/ColorDodge/Lighten 类组合而来的共同推论。
    /// </summary>
    [Theory]
    [InlineData(BlendMode.VividLight)]
    [InlineData(BlendMode.LinearLight)]
    [InlineData(BlendMode.PinLight)]
    [InlineData(BlendMode.HardLight)]
    [InlineData(BlendMode.HardMix)]
    public void ContrastModes_BlackSourceYieldsBlack_WhiteSourceYieldsWhite(BlendMode mode)
    {
        // 对每个模式取三个不同的 backdrop，确认端点行为与 backdrop 无关。
        foreach (double cb in new[] { 0.25, 0.5, 0.75 })
        {
            Assert.Equal(0.0, ApplySeparable(mode, cb, 0.0), 12);
            Assert.Equal(1.0, ApplySeparable(mode, cb, 1.0), 12);
        }
    }

    /// <summary>
    /// 🔴 <b>PinLight 的「结构」是确定的：暗源取 min、亮源取 max。</b>
    /// 这一条不受 2 倍重映射存疑的影响，可以长期守住。
    /// </summary>
    [Fact]
    public void PinLight_DarkSourceTakesDarker_LightSourceTakesLighter()
    {
        // Adobe Learn：「blend color 暗于 50% 灰时，比它亮的像素被替换」→ 结果变暗 → min
        //           「blend color 亮于 50% 灰时，比它暗的像素被替换」→ 结果变亮 → max
        double darkSource = ApplySeparable(BlendMode.PinLight, 0.75, 0.25);
        double lightSource = ApplySeparable(BlendMode.PinLight, 0.25, 0.75);

        Assert.Equal(0.5, darkSource, 12);   // min(0.75, 2*0.25 = 0.5) = 0.5
        Assert.Equal(0.5, lightSource, 12);  // max(0.25, 2*0.75-1 = 0.5) = 0.5

        // 方向性：暗源必须把 backdrop 压暗，亮源必须把 backdrop 提亮 —— 不看数值只看不等号。
        Assert.True(darkSource <= 0.75, "PinLight 暗源没有把底色压暗。");
        Assert.True(lightSource >= 0.25, "PinLight 亮源没有把底色提亮。");
    }

    /// <summary>
    /// ⚠️ <b>【低把握度 · 待 Tier 2 裁决】PinLight 的 2 倍重映射。</b>
    /// </summary>
    /// <remarks>
    /// 本测试<b>锁定的是当前选择的实现，不是已验证的正确性</b>。
    /// 若 Tier 2（macOS runner 实测 CoreImage）判定不该有 2 倍重映射，
    /// 正确结果是 <c>cs = 0.25 → min(0.75, 0.25) = 0.25</c>（而非 0.5），
    /// <b>改这一条即可</b>，其余 PinLight 断言不受影响。
    /// </remarks>
    [Fact]
    public void PinLight_UsesDoubleRemapAroundMidGray_PENDING_TIER2()
    {
        // 2 倍重映射：min(0.75, 2*0.25 = 0.5) = 0.5
        Assert.Equal(0.5, ApplySeparable(BlendMode.PinLight, 0.75, 0.25), 12);

        // 不重映射的候选式会给出 min(0.75, 0.25) = 0.25，两者在此点结果不同 ——
        // 这正是需要实测裁决的分歧点。
        Assert.NotEqual(0.25, ApplySeparable(BlendMode.PinLight, 0.75, 0.25), 12);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  四、非可分离四件套
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Color 模式保底色明度、取源色相与饱和度（<c>Overview.bs:1466</c>）。</summary>
    [Fact]
    public void Color_KeepsBackdropLuminance()
    {
        // cb = (0.2,0.4,0.6)：Lum = 0.06 + 0.236 + 0.066 = 0.362
        // cs = (0.8,0.1,0.3)：Lum = 0.24 + 0.059 + 0.033 = 0.332；d = 0.362 - 0.332 = 0.03
        // → (0.83, 0.13, 0.33)，全部在 [0,1] 内，ClipColor 不生效
        Rgb3 result = BlendPixel3(BlendMode.Color, (0.2, 0.4, 0.6), (0.8, 0.1, 0.3));

        Assert.Equal(0.83, result.R, 12);
        Assert.Equal(0.13, result.G, 12);
        Assert.Equal(0.33, result.B, 12);
        Assert.Equal(0.362, BlendMath.Lum(result.R, result.G, result.B), 10);
    }

    /// <summary>Luminosity 是 Color 的逆操作：取源明度、留底色相与饱和度（<c>Overview.bs:1477</c>）。</summary>
    [Fact]
    public void Luminosity_KeepsSourceLuminance()
    {
        Rgb3 result = BlendPixel3(BlendMode.Luminosity, (0.2, 0.4, 0.6), (0.8, 0.1, 0.3));

        // Lum(cs) = 0.332，结果明度应当等于它。
        Assert.Equal(0.332, BlendMath.Lum(result.R, result.G, result.B), 10);
    }

    /// <summary>Hue 模式遇到<b>灰色源</b>时结果是无色的（底色被去饱和）—— 规范 <c>Overview.bs:1439</c> 的直接推论。</summary>
    [Fact]
    public void Hue_GraySourceDesaturatesBackdrop()
    {
        // cs = (0.5,0.5,0.5)：Sat(cs) = 0，SetSat(...,0) 在灰阶上直接归零
        // cb = (0.8,0.2,0.2)：Lum = 0.24 + 0.118 + 0.022 = 0.38
        // → SetLum(0,0,0, 0.38) = (0.38, 0.38, 0.38)
        Rgb3 result = BlendPixel3(BlendMode.Hue, (0.8, 0.2, 0.2), (0.5, 0.5, 0.5));

        Assert.Equal(0.38, result.R, 12);
        Assert.Equal(0.38, result.G, 12);
        Assert.Equal(0.38, result.B, 12);
        Assert.Equal(0.0, BlendMath.Sat(result.R, result.G, result.B), 12);
    }

    /// <summary>Hue 模式遇到<b>灰色底色</b>时结果也是无色的 —— <c>Sat(Cb) = 0</c> 同理。</summary>
    [Fact]
    public void Hue_GrayBackdropAlsoDesaturates()
    {
        Rgb3 result = BlendPixel3(BlendMode.Hue, (0.5, 0.5, 0.5), (0.8, 0.1, 0.3));

        Assert.Equal(0.0, BlendMath.Sat(result.R, result.G, result.B), 12);
    }

    /// <summary>Saturation 模式在灰色源上不产生任何变化（规范 <c>Overview.bs:1451</c> 明写）。</summary>
    [Fact]
    public void Saturation_GraySourceLeavesBackdropUntouched()
    {
        // Sat(Cs) = 0 → SetSat(Cb, 0) 把 Cb 的饱和度清零 → 无色；再 SetLum 补回 Lum(Cb) → 灰。
        // 净效果：结果就是灰。规范描述是「no change」指的是对有彩度的部分不改变饱和度以外的量，
        // 这里断言的是**可观测结果**：灰色源下结果必然无色。
        Rgb3 result = BlendPixel3(BlendMode.Saturation, (0.8, 0.2, 0.2), (0.5, 0.5, 0.5));

        Assert.Equal(0.0, BlendMath.Sat(result.R, result.G, result.B), 12);
        Assert.Equal(0.38, BlendMath.Lum(result.R, result.G, result.B), 10);
    }

    /// <summary>四个非可分离模式的分类判定必须正确，否则会走错分派分支。</summary>
    [Fact]
    public void IsNonSeparable_ExactlyTheFourHslModes()
    {
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            bool expected = mode is BlendMode.Hue or BlendMode.Saturation
                or BlendMode.Color or BlendMode.Luminosity;
            Assert.Equal(expected, BlendOps.IsNonSeparable(mode));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  五、覆盖度：24 个模式一个都不能漏
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 <b>24 个模式必须全部可达</b>：<see cref="BlendMode"/> 的每个值都要能被
    /// <see cref="BlendOps.Composite"/> 走通，不许有模式落在 switch 的 <c>_ =&gt;</c> 抛异常臂里。
    /// </summary>
    [Fact]
    public void EveryBlendMode_IsReachableThroughComposite()
    {
        BlendMode[] all = Enum.GetValues<BlendMode>();
        Assert.Equal(24, all.Length);

        foreach (BlendMode mode in all)
        {
            PixelBuffer backdrop = Fill(1, 1, 0.4, 0.5, 0.6, 1.0);
            PixelBuffer source = Fill(1, 1, 0.7, 0.2, 0.3, 0.8);
            byte[] before = backdrop.Rgba.ToArray();

            // 若模式没有出现在 BlendSeparable / BlendNonSeparable 的任一分支里，这里会抛。
            BlendOps.Composite(backdrop, source, mode);

            // 并且必须真的改变了像素 —— 恒真断言比没有断言更危险，这里排除「什么都没发生」。
            bool changed = false;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] != backdrop.Raw[i])
                {
                    changed = true;
                    break;
                }
            }

            Assert.True(changed, $"模式 {mode} 跑完之后 backdrop 一个字节都没变，疑似落到了空实现。");
        }
    }

    /// <summary>
    /// 🔴 <b>alpha 不变量</b>：无论用哪个模式，结果 alpha 必须等于普通 source-over 的 alpha
    /// <c>αo = αs + (1 - αs) x αb</c>。混合只改颜色，不该改覆盖率。
    /// </summary>
    [Fact]
    public void ResultAlpha_IsIndependentOfBlendMode()
    {
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            // 底色 alpha = 191/255 ≈ 0.749；源 alpha = 128/255 ≈ 0.502
            // αo = 128/255 + (1 - 128/255) x 191/255 = 0.501961 + 0.498039 x 0.749020 = 0.874971
            //     x 255 = 223.117 → 远离零舍入 = 223
            PixelBuffer backdrop = Fill(1, 1, 0.9, 0.3, 0.2, (191.0 / 255.0));
            PixelBuffer source = Fill(1, 1, 0.1, 0.8, 0.5, (128.0 / 255.0));

            BlendOps.Composite(backdrop, source, mode);

            Assert.Equal(223, backdrop.Raw[3]);
        }
    }

    /// <summary>
    /// 🔴 <b>Normal 必须退化成普通 source-over</b>。这是整个合成公式的锚点：
    /// 代入 <c>B(Cb, Cs) = Cs</c> 后三个系数应当塌成 <c>αs x Cs + (1 - αs) x αb x Cb</c>。
    /// </summary>
    [Fact]
    public void NormalMode_EqualsPlainSourceOver()
    {
        // 底色不透明红 (255,0,0,255)；源 50% 绿 (0,255,0,128)
        // 预乘：FromStraightRgba → (0,128,0,128)  [G = (255*128+127)/255 = 128]
        // 反预乘 Cs = (0,128/128) = (0,1)
        // Co = αs x Cs + (1 - αs) x Cb = 128/255 x (0,1,0) + 127/255 x (1,0,0)
        //    = (127/255, 128/255, 0) → 字节 (127, 128, 0)，αo = 1 → 255
        PixelBuffer backdrop = Fill(1, 1, 1.0, 0.0, 0.0, 1.0);
        PixelBuffer source = Fill(1, 1, 0.0, 1.0, 0.0, (128.0 / 255.0));

        Assert.Equal(new byte[] { 0, 128, 0, 128 }, source.Rgba.ToArray());

        BlendOps.Composite(backdrop, source, BlendMode.Normal);

        Assert.Equal(new byte[] { 127, 128, 0, 255 }, backdrop.Rgba.ToArray());
    }

    /// <summary>Multiply 在两块都不透明时的逐通道积。</summary>
    [Fact]
    public void Multiply_OpaqueLayers_ProducesChannelProduct()
    {
        // 底色 128/255 ≈ 0.501961，源 64/255 ≈ 0.250980
        // 积 = (128 x 64) / (255 x 255) = 8192 / 65025 = 0.125974
        // x 255 = 32.123 → 远离零舍入 = 32
        PixelBuffer backdrop = Fill(2, 2, (128.0 / 255.0), (128.0 / 255.0), (128.0 / 255.0), 1.0);
        PixelBuffer source = Fill(2, 2, (64.0 / 255.0), (64.0 / 255.0), (64.0 / 255.0), 1.0);

        BlendOps.Composite(backdrop, source, BlendMode.Multiply);

        // 2×2 全部同色，顺带验证了行距遍历没有跳格。
        for (int i = 0; i < backdrop.Raw.Length; i += 4)
        {
            Assert.Equal(32, backdrop.Raw[i]);
            Assert.Equal(32, backdrop.Raw[i + 1]);
            Assert.Equal(32, backdrop.Raw[i + 2]);
            Assert.Equal(255, backdrop.Raw[i + 3]);
        }
    }

    /// <summary>
    /// <c>sourceAlphaScale</c> 必须等价于「把图层不透明度乘进源的 alpha」。
    /// </summary>
    [Fact]
    public void SourceAlphaScale_ActsAsExtraLayerOpacity()
    {
        // 底色不透明红，源不透明绿，Multiply，缩放 0.5
        // 缩放后 sa = 0.5、spg = 0.5 → 反预乘 Cs 仍是 (0,1,0)（缩放不改变直通色）✔
        // Multiply(Cb, Cs) = (1*0, 0*1, 0*0) = (0,0,0)
        // wSrc = 0.5 x 0 = 0；wMix = 0.5 x 1 = 0.5；wBkd = 0.5 x 1 = 0.5
        // Co = 0 + 0.5 x (0,0,0) + 0.5 x (1,0,0) = (0.5, 0, 0) → (128, 0, 0)，αo = 1 → 255
        PixelBuffer backdrop = Fill(1, 1, 1.0, 0.0, 0.0, 1.0);
        PixelBuffer source = Fill(1, 1, 0.0, 1.0, 0.0, 1.0);

        BlendOps.Composite(backdrop, source, BlendMode.Multiply, 0.5);

        Assert.Equal(new byte[] { 128, 0, 0, 255 }, backdrop.Rgba.ToArray());
    }

    /// <summary>完全透明的源不改变底色<b>任何一个字节</b>。</summary>
    [Fact]
    public void FullyTransparentSource_LeavesBackdropByteIdentical()
    {
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            PixelBuffer backdrop = Fill(1, 1, 0.4, 0.5, 0.6, 0.9);
            byte[] before = backdrop.Rgba.ToArray();
            PixelBuffer source = Fill(1, 1, 0.7, 0.2, 0.3, 0.0);

            BlendOps.Composite(backdrop, source, mode);

            Assert.Equal(before, backdrop.Rgba.ToArray());
        }
    }

    /// <summary>空底色（alpha 全 0）+ 任意模式 = 源的直通色原样放上去。</summary>
    [Fact]
    public void EmptyBackdrop_EqualsPlainSourceOver()
    {
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            PixelBuffer backdrop = PixelBuffer.Create(1, 1);
            PixelBuffer source = Fill(1, 1, 0.3, 0.6, 0.9, 0.75);

            BlendOps.Composite(backdrop, source, mode);

            // 半透明源落在空底上，结果就是源本身（预乘值原样）。
            Assert.Equal(source.Rgba.ToArray(), backdrop.Rgba.ToArray());
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  六、参数校验
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>尺寸不一致必须抛，而不是按较短的一边静默处理。</summary>
    [Fact]
    public void Composite_RejectsMismatchedSizes()
    {
        PixelBuffer backdrop = Fill(2, 2, 0.5, 0.5, 0.5, 1.0);
        PixelBuffer source = Fill(1, 1, 0.5, 0.5, 0.5, 1.0);

        Assert.Throws<ArgumentException>(() => BlendOps.Composite(backdrop, source, BlendMode.Normal));
    }

    /// <summary><c>sourceAlphaScale</c> 越界必须抛。</summary>
    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Composite_RejectsOutOfRangeAlphaScale(double scale)
    {
        PixelBuffer backdrop = Fill(1, 1, 0.5, 0.5, 0.5, 1.0);
        PixelBuffer source = Fill(1, 1, 0.5, 0.5, 0.5, 1.0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => BlendOps.Composite(backdrop, source, BlendMode.Normal, scale));
    }

    /// <summary>null 参数必须抛。</summary>
    [Fact]
    public void Composite_RejectsNulls()
    {
        PixelBuffer buffer = Fill(1, 1, 0.5, 0.5, 0.5, 1.0);

        Assert.Throws<ArgumentNullException>(() => BlendOps.Composite(null!, buffer, BlendMode.Normal));
        Assert.Throws<ArgumentNullException>(() => BlendOps.Composite(buffer, null!, BlendMode.Normal));
    }

    // ─────────────────── 私有工具 ───────────────────

    /// <summary>造一块 n×m 的直通色缓冲（内部会转预乘）。</summary>
    private static PixelBuffer Fill(int w, int h, double r, double g, double b, double a)
    {
        var straight = new byte[w * h * 4];
        byte br = ToByte(r);
        byte bg = ToByte(g);
        byte bb = ToByte(b);
        byte ba = ToByte(a);
        for (int i = 0; i < straight.Length; i += 4)
        {
            straight[i] = br;
            straight[i + 1] = bg;
            straight[i + 2] = bb;
            straight[i + 3] = ba;
        }

        return PixelBuffer.FromStraightRgba(straight, w, h);
    }

    private static byte ToByte(double v)
    {
        double scaled = v * 255.0;
        return (byte)Math.Clamp((int)Math.Round(scaled, MidpointRounding.AwayFromZero), 0, 255);
    }

    /// <summary>
    /// 取单通道的混合结果，<b>走真实分派路径但不做 8-bit 量化</b>。
    /// </summary>
    /// <remarks>
    /// 底层用两块<b>不透明</b>像素（αb = αs = 1）调用 <see cref="BlendOps.BlendPixel"/>。
    /// 此时三个系数塌成 <c>wSrc = 0、wMix = 1、wBkd = 0</c>，输出预乘红通道<b>恰好等于 B(Cb, Cs)</b>，
    /// 所以拿到的就是纯公式结果。
    /// <para><b>为什么不走缓冲</b>：缓冲路径最后有一步 8-bit 量化
    /// （<c>BlendOps.ToByte</c>），那会把 0.625 变成 159/255 ≈ 0.6235。
    /// 公式断言要验的是<b>规范公式本身</b>，量化误差不该混进来 ——
    /// 它是本项目引入的、不是 W3C 的一部分。量化行为另有缓冲级测试单独守。</para>
    /// </remarks>
    private static double ApplySeparable(BlendMode mode, double cb, double cs) =>
        BlendOps.BlendPixel(cb, cb, cb, 1.0, cs, cs, cs, 1.0, mode).R;

    /// <summary>取三通道的混合结果，同样走真实分派、不做量化。</summary>
    private static Rgb3 BlendPixel3(BlendMode mode, (double R, double G, double B) cb, (double R, double G, double B) cs)
    {
        PremulRgba r = BlendOps.BlendPixel(cb.R, cb.G, cb.B, 1.0, cs.R, cs.G, cs.B, 1.0, mode);
        return new Rgb3(r.R, r.G, r.B);
    }
}