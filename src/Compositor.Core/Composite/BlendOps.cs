namespace Compositor.Core.Composite;

/// <summary>
/// 预乘 RGBA 四元组，各通道归一化到 0–1。<b>色通道是预乘值</b>，即已含 alpha 权重。
/// </summary>
/// <param name="R">预乘红通道。</param>
/// <param name="G">预乘绿通道。</param>
/// <param name="B">预乘蓝通道。</param>
/// <param name="A">alpha。</param>
/// <remarks>
/// 这是 <see cref="BlendOps"/> 内部的中间表示，刻意用实数而非 <see cref="byte"/>：
/// 只有在最后写回缓冲的那一步才做一次 8-bit 量化，中间全程不丢精度。
/// </remarks>
internal readonly record struct PremulRgba(double R, double G, double B, double A);

/// <summary>
/// 混合模式的<b>缓冲级入口</b>：把一个预乘 RGBA8 图层按指定模式混合到另一个预乘 RGBA8 缓冲上。
/// </summary>
/// <remarks>
/// <para><b>本类是铁律 2 与 W3C §10 的收口。</b>W3C 要求混合在<b>直通</b>色上进行
/// （<c>Overview.bs:1128</c>），项目铁律 2 要求缓冲<b>全程预乘</b>。两者不矛盾，
/// 但转换点必须<b>唯一且显式</b>，否则最容易出的错就是「有的路径反预乘了、有的没反」——
/// 那种 bug 在图层不透明时完全看不出来，只在半透明边缘显形，且单元测试通常抓不住。
/// 所以规矩是：<b>只有本类反预乘，也只有本类预乘回去</b>。</para>
///
/// <para><b>合成公式</b>（W3C <c>Overview.bs:1180-1187</c> 的推导）：
/// <code>
/// αo x Co = αs x (1 - αb) x Cs  +  αs x αb x B(Cb, Cs)  +  (1 - αs) x αb x Cb
/// αo       = αs + (1 - αs) x αb
/// </code>
/// 三个系数加起来恒等于 1（可自行展开验证），这保证了<b>αb = 1</b> 时结果退化为
/// 普通 source-over 被 αs 加权，符合直觉。</para>
///
/// <para><b>两处退化捷径是恒等变形，不是近似</b>：
/// <list type="bullet">
/// <item><c>αs == 0</c> → 输出底色。代入公式：第三项系数 <c>(1-αs)xαb = αb</c>，
/// 前两项为 0，结果正是 <c>αb x Cb</c>，即底色本身。</item>
/// <item><c>αb == 0</c> → 输出 <c>αs x Cs</c>。代入公式：第二项系数 <c>αs x αb = 0</c>，
/// 第三项为 0，结果正是 <c>αs x Cs</c>。</item>
/// </list>
/// 走捷径除了省掉每像素的分支，还能<b>彻底避开 Cb 为 0 时 B(Cb, Cs) 的未定义输入</b>
/// （比如 ColorDodge 在 Cb = 0 时走特例、Dodge 的除零方向）。</para>
///
/// <para><b>8-bit 量化</b>：W3C 的公式定义在实数域上，本项目缓冲是 8-bit，
/// 因此写回时用 <see cref="MidpointRounding.AwayFromZero"/> 四舍五入（与 C 的 <c>lround</c> 一致，
/// 见 <c>CSemantics.LRound</c> 的说明）并钳到 [0,255]。这一步的误差是本项目量化引入的，
/// <b>不是规范的一部分</b>，比对着规范手算时不要把它算进去。</para>
/// </remarks>
public static class BlendOps
{
    /// <summary>混合一个像素。实数域，不做 8-bit 量化。测试专用路径。</summary>
    /// <param name="bpr">底色的<b>预乘</b>红通道，0–1。</param>
    /// <param name="bpg">底色的预乘绿通道，0–1。</param>
    /// <param name="bpb">底色的预乘蓝通道，0–1。</param>
    /// <param name="ba">底色 alpha，0–1。</param>
    /// <param name="spr">源的<b>预乘</b>红通道，0–1。</param>
    /// <param name="spg">源的预乘绿通道，0–1。</param>
    /// <param name="spb">源的预乘蓝通道，0–1。</param>
    /// <param name="sa">源 alpha，0–1。</param>
    /// <param name="mode">混合模式。</param>
    /// <returns>结果像素（预乘 RGBA，实数域）。</returns>
    /// <remarks>
    /// 入参的色通道<b>是预乘值</b>，与 <see cref="PixelBuffer"/> 的存储一致；
    /// 反预乘由本方法内部完成，不要在调用前先除一次 alpha。
    /// </remarks>
    internal static PremulRgba BlendPixel(
        double bpr, double bpg, double bpb, double ba,
        double spr, double spg, double spb, double sa,
        BlendMode mode)
    {
        // 恒等捷径，见类注释的推导。
        if (sa == 0.0)
        {
            return new PremulRgba(bpr, bpg, bpb, ba);
        }

        if (ba == 0.0)
        {
            return new PremulRgba(spr, spg, spb, sa);
        }

        // 反预乘：W3C 的混合公式要求直通色（Overview.bs:1128）。
        double csR = spr / sa;
        double csG = spg / sa;
        double csB = spb / sa;
        double cbR = bpr / ba;
        double cbG = bpg / ba;
        double cbB = bpb / ba;

        var cb = new Rgb3(cbR, cbG, cbB);
        var cs = new Rgb3(csR, csG, csB);
        Rgb3 mixed = IsNonSeparable(mode)
            ? BlendNonSeparable(mode, cb, cs)
            : BlendSeparable(mode, cb, cs);

        // αs x (1 - αb) x Cs + αs x αb x B(Cb, Cs) + (1 - αs) x αb x Cb
        double wSrc = sa * (1.0 - ba);
        double wMix = sa * ba;
        double wBkd = (1.0 - sa) * ba;

        return new PremulRgba(
            (wSrc * csR) + (wMix * mixed.R) + (wBkd * cbR),
            (wSrc * csG) + (wMix * mixed.G) + (wBkd * cbG),
            (wSrc * csB) + (wMix * mixed.B) + (wBkd * cbB),
            sa + ((1.0 - sa) * ba));
    }

    /// <summary>
    /// 把 <paramref name="source"/> 图层按 <paramref name="mode"/> 混合到
    /// <paramref name="backdrop"/> 上，<b>原地修改 backdrop</b>。
    /// </summary>
    /// <param name="backdrop">被混合到的下方内容（就地修改）。</param>
    /// <param name="source">上方待混合的图层。要求与 <paramref name="backdrop"/> 同尺寸。</param>
    /// <param name="mode">混合模式。</param>
    /// <param name="maskCoverage">
    /// <b>蒙版覆盖率</b>，0–1。来源：有效蒙版 = 自身栅格蒙版 × 剪贴 coverage × 所有祖先分组蒙版，
    /// 三者在<b>进入本次合成之前</b>已逐层相乘完毕。默认 1.0 表示无蒙版。
    /// </param>
    /// <param name="opacity">
    /// <b>图层不透明度</b>，0–1。作用于<b>本源自身</b>。
    /// 若 <paramref name="source"/> 是分组的扁平结果，则这里施加的是<b>该组</b>的不透明度，
    /// 必须在组内合成完成之后才施加。默认 1.0 表示完全不透明。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="backdrop"/> 或 <paramref name="source"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">两块缓冲尺寸不一致。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> 未定义，或两个系数不在 0–1 内。</exception>
    /// <remarks>
    /// <para><b>🔴 为什么「蒙版覆盖率」与「图层不透明度」必须是两个参数，不能合成一个。</b>
    /// 二者在<b>树上的作用时机不同</b>：
    /// <list type="number">
    /// <item><b>蒙版是逐层乘的</b>。一张图层的有效覆盖率要把它自己那张栅格蒙版、
    /// 剪贴 coverage、以及<b>所有祖先分组</b>的蒙版乘起来。乘完才进入混合。</item>
    /// <item><b>不透明度是在组内合成完成之后统一施加的</b>。一个分组里的多层先各自合成，
    /// 得到该组的扁平结果，再由这个结果整体参与不透明度。</item>
    /// </list>
    /// 合成一个标量只在「单一来源」时碰巧对；一旦出现分组蒙版，
    /// 调用点就分不清手上那个系数到底是逐层乘出来的还是组级施加的 ——
    /// 而这种错<b>不会让任何测试变红</b>，只会让画面淡一点。
    /// 保持两个具名参数，等于把这件事在类型层面钉死。</para>
    ///
    /// <para><b>诚实交代：在这一个调用点上，两者的算术效果是可交换的</b> ——
    /// 它们都是把预乘源整体乘一个系数，顺序与合并都不改变结果
    /// （见 <c>BlendModeTests.MaskCoverageAndOpacity_AreArithmeticallyCommutativeAtThisCallSite</c>）。
    /// 拆开的理由<b>不是算术，是调用点的可读性与树级时机</b>；
    /// 真正会算出不同像素的是「不透明度施加在单层还是施加在已合成的组上」，
    /// 那是合成器遍历的职责，不是本方法的职责。
    /// 但即便如此也不应该合并参数 —— 一旦合并，下一个调用点就会按自己的直觉填错。</para>
    ///
    /// <para><b>缩放作用在预乘值上</b>：两个系数都同时乘到源的三个色通道和 alpha 上。
    /// 这与「先反预乘、再只缩放 alpha」是<b>同一个结果</b>——预乘的定义就是 <c>color x alpha</c>。
    /// 写在预乘侧的好处是永远不需要在缩放前反预乘，少一条能让 alpha 铁律走漏的路径。</para>
    /// <para>本方法<b>不</b>处理图层顺序、可见性、剪贴栈的成栈判定 —— 那些是合成器遍历的职责，
    /// 本类只管「两个像素怎么混」。</para>
    /// </remarks>
    public static void Composite(
        PixelBuffer backdrop,
        PixelBuffer source,
        BlendMode mode,
        double maskCoverage = 1.0,
        double opacity = 1.0)
    {
        ArgumentNullException.ThrowIfNull(backdrop);
        ArgumentNullException.ThrowIfNull(source);

        if (backdrop.Width != source.Width || backdrop.Height != source.Height)
        {
            throw new ArgumentException(
                $"尺寸不一致：backdrop {backdrop.Width}×{backdrop.Height}，source {source.Width}×{source.Height}。",
                nameof(source));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "未定义的混合模式。");
        }

        RequireUnitRange(maskCoverage, nameof(maskCoverage));
        RequireUnitRange(opacity, nameof(opacity));

        Span<byte> dst = backdrop.Raw;
        ReadOnlySpan<byte> src = source.Rgba;

        // 两个系数都作用在预乘源上，因此合并成一个缩放因子。
        // 合并只是省一次乘法，不改变语义 —— 语义上的区别在上面的参数文档里。
        double k = maskCoverage * opacity * (1.0 / 255.0);

        for (int i = 0; i < dst.Length; i += 4)
        {
            double sa = src[i + 3] * k;
            PremulRgba result = BlendPixel(
                dst[i] * (1.0 / 255.0), dst[i + 1] * (1.0 / 255.0), dst[i + 2] * (1.0 / 255.0), dst[i + 3] * (1.0 / 255.0),
                src[i] * k, src[i + 1] * k, src[i + 2] * k, sa,
                mode);

            dst[i] = ToByte(result.R);
            dst[i + 1] = ToByte(result.G);
            dst[i + 2] = ToByte(result.B);
            dst[i + 3] = ToByte(result.A);
        }
    }

    /// <summary>校验系数是 0–1 之间的实数。</summary>
    /// <param name="value">待校验的系数。</param>
    /// <param name="paramName">出参名，用于异常。</param>
    /// <exception cref="ArgumentOutOfRangeException">不是有限数，或不在 [0,1] 内。</exception>
    private static void RequireUnitRange(double value, string paramName)
    {
        if (double.IsNaN(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, "必须是 0–1 之间的实数。");
        }
    }

    /// <summary>该模式是否属于 W3C 的「非可分离」四件套。</summary>
    /// <param name="mode">混合模式。</param>
    /// <returns>色相/饱和度/颜色/明度返回 <see langword="true"/>。</returns>
    /// <remarks>这四个模式必须看<b>三个通道一起</b>算，其余 20 个逐通道独立即可（<c>Overview.bs:1192</c>）。</remarks>
    internal static bool IsNonSeparable(BlendMode mode) => mode switch
    {
        BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity => true,
        _ => false,
    };

    /// <summary>逐通道混合。20 个可分离模式。</summary>
    /// <param name="mode">混合模式。</param>
    /// <param name="cb">底色（直通）。</param>
    /// <param name="cs">源色（直通）。</param>
    /// <returns>混合结果（直通）。</returns>
    private static Rgb3 BlendSeparable(BlendMode mode, Rgb3 cb, Rgb3 cs) => mode switch
    {
        BlendMode.Normal => cs,
        BlendMode.Darken => new Rgb3(BlendMath.Darken(cb.R, cs.R), BlendMath.Darken(cb.G, cs.G), BlendMath.Darken(cb.B, cs.B)),
        BlendMode.Multiply => new Rgb3(BlendMath.Multiply(cb.R, cs.R), BlendMath.Multiply(cb.G, cs.G), BlendMath.Multiply(cb.B, cs.B)),
        BlendMode.Lighten => new Rgb3(BlendMath.Lighten(cb.R, cs.R), BlendMath.Lighten(cb.G, cs.G), BlendMath.Lighten(cb.B, cs.B)),
        BlendMode.Screen => new Rgb3(BlendMath.Screen(cb.R, cs.R), BlendMath.Screen(cb.G, cs.G), BlendMath.Screen(cb.B, cs.B)),
        BlendMode.Overlay => new Rgb3(BlendMath.Overlay(cb.R, cs.R), BlendMath.Overlay(cb.G, cs.G), BlendMath.Overlay(cb.B, cs.B)),
        BlendMode.SoftLight => new Rgb3(BlendMath.SoftLight(cb.R, cs.R), BlendMath.SoftLight(cb.G, cs.G), BlendMath.SoftLight(cb.B, cs.B)),
        BlendMode.HardLight => new Rgb3(BlendMath.HardLight(cb.R, cs.R), BlendMath.HardLight(cb.G, cs.G), BlendMath.HardLight(cb.B, cs.B)),
        BlendMode.VividLight => new Rgb3(BlendMath.VividLight(cb.R, cs.R), BlendMath.VividLight(cb.G, cs.G), BlendMath.VividLight(cb.B, cs.B)),
        BlendMode.LinearLight => new Rgb3(BlendMath.LinearLight(cb.R, cs.R), BlendMath.LinearLight(cb.G, cs.G), BlendMath.LinearLight(cb.B, cs.B)),
        BlendMode.PinLight => new Rgb3(BlendMath.PinLight(cb.R, cs.R), BlendMath.PinLight(cb.G, cs.G), BlendMath.PinLight(cb.B, cs.B)),
        BlendMode.HardMix => new Rgb3(BlendMath.HardMix(cb.R, cs.R), BlendMath.HardMix(cb.G, cs.G), BlendMath.HardMix(cb.B, cs.B)),
        BlendMode.Difference => new Rgb3(BlendMath.Difference(cb.R, cs.R), BlendMath.Difference(cb.G, cs.G), BlendMath.Difference(cb.B, cs.B)),
        BlendMode.Exclusion => new Rgb3(BlendMath.Exclusion(cb.R, cs.R), BlendMath.Exclusion(cb.G, cs.G), BlendMath.Exclusion(cb.B, cs.B)),
        BlendMode.Subtract => new Rgb3(BlendMath.Subtract(cb.R, cs.R), BlendMath.Subtract(cb.G, cs.G), BlendMath.Subtract(cb.B, cs.B)),
        BlendMode.Divide => new Rgb3(BlendMath.Divide(cb.R, cs.R), BlendMath.Divide(cb.G, cs.G), BlendMath.Divide(cb.B, cs.B)),
        BlendMode.ColorBurn => new Rgb3(BlendMath.ColorBurn(cb.R, cs.R), BlendMath.ColorBurn(cb.G, cs.G), BlendMath.ColorBurn(cb.B, cs.B)),
        BlendMode.LinearBurn => new Rgb3(BlendMath.LinearBurn(cb.R, cs.R), BlendMath.LinearBurn(cb.G, cs.G), BlendMath.LinearBurn(cb.B, cs.B)),
        BlendMode.ColorDodge => new Rgb3(BlendMath.ColorDodge(cb.R, cs.R), BlendMath.ColorDodge(cb.G, cs.G), BlendMath.ColorDodge(cb.B, cs.B)),
        BlendMode.LinearDodge => new Rgb3(BlendMath.LinearDodge(cb.R, cs.R), BlendMath.LinearDodge(cb.G, cs.G), BlendMath.LinearDodge(cb.B, cs.B)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "该模式是可分离的，不应走到非可分离分支。"),
    };

    /// <summary>整体混合。4 个非可分离模式。</summary>
    /// <param name="mode">混合模式。</param>
    /// <param name="cb">底色（直通）。</param>
    /// <param name="cs">源色（直通）。</param>
    /// <returns>混合结果（直通）。</returns>
    /// <remarks>四条公式逐字来自 <c>Overview.bs:1442 / 1454 / 1466 / 1477</c>，
    /// 辅助函数 <see cref="BlendMath.Lum"/>、<see cref="BlendMath.SetSat"/>、<see cref="BlendMath.SetLum(Rgb3, double)"/> 同出该节。</remarks>
    private static Rgb3 BlendNonSeparable(BlendMode mode, Rgb3 cb, Rgb3 cs) => mode switch
    {
        // B = SetLum(SetSat(Cs, Sat(Cb)), Lum(Cb)) —— 取源的色相，底色的饱和度与明度
        BlendMode.Hue => BlendMath.SetLum(
            BlendMath.SetSat(cs.R, cs.G, cs.B, BlendMath.Sat(cb.R, cb.G, cb.B)),
            BlendMath.Lum(cb.R, cb.G, cb.B)),
        // B = SetLum(SetSat(Cb, Sat(Cs)), Lum(Cb)) —— 取源的饱和度，底色的色相与明度
        BlendMode.Saturation => BlendMath.SetLum(
            BlendMath.SetSat(cb.R, cb.G, cb.B, BlendMath.Sat(cs.R, cs.G, cs.B)),
            BlendMath.Lum(cb.R, cb.G, cb.B)),
        // B = SetLum(Cs, Lum(Cb)) —— 取源的色相与饱和度，底色的明度
        BlendMode.Color => BlendMath.SetLum(cs.R, cs.G, cs.B, BlendMath.Lum(cb.R, cb.G, cb.B)),
        // B = SetLum(Cb, Lum(Cs)) —— 取源的明度，底色的色相与饱和度
        BlendMode.Luminosity => BlendMath.SetLum(cb.R, cb.G, cb.B, BlendMath.Lum(cs.R, cs.G, cs.B)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "该模式是非可分离的，不应走到可分离分支。"),
    };

    /// <summary>实数域结果 → 8-bit。四舍五入（远离零）后钳到 [0,255]。</summary>
    /// <param name="v">0–1 量纲的通道值，允许越界。</param>
    /// <returns>量化后的字节。</returns>
    /// <remarks>
    /// 舍入方向与 C 的 <c>lround</c> 一致（<c>MidpointRounding.AwayFromZero</c>），
    /// 不使用 .NET 默认的银行家舍入 —— 理由见 <c>CSemantics.LRound</c> 的类注释。
    /// <para><b>NaN 的处理</b>：<c>double</c> 转 <see cref="int"/> 遇 NaN 的行为是未指定的，
    /// 不能依赖它恰好得到 0。混合公式里所有除法都已显式防零除，
    /// 这里再挡一道 <c>IsFinite</c>，是为了让「万一将来新增模式引入了 NaN」时
    /// 表现为确定的 0 而不是随机的字节。</para>
    /// </remarks>
    private static byte ToByte(double v)
    {
        double scaled = v * 255.0;
        if (!double.IsFinite(scaled) || scaled <= 0.0)
        {
            return 0;
        }

        if (scaled >= 255.0)
        {
            return 255;
        }

        return (byte)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }
}