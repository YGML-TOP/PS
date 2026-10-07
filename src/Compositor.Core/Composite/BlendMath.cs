using System.Runtime.CompilerServices;

namespace Compositor.Core.Composite;

/// <summary>
/// RGB 三元组，各通道为<b>直通（未预乘）</b>、归一化到 0–1 的实数。
/// </summary>
/// <param name="R">红通道，0–1。</param>
/// <param name="G">绿通道，0–1。</param>
/// <param name="B">蓝通道，0–1。</param>
/// <remarks>
/// <para><b>为什么混合要反预乘。</b>W3C Compositing and Blending Level 1 §10 明写
/// <i>"The blending calculations must not use pre-multiplied color values."</i>
/// （编辑器源码 <c>Overview.bs:1128</c>）。这与项目铁律 2（缓冲全程预乘 RGBA8）<b>不冲突</b>：
/// 铁律约束的是<b>缓冲的存储形式</b>，混合前反预乘一次、混合后预乘回去即可，
/// 边界转换在 <see cref="BlendOps"/> 一处收口。</para>
/// <para>通道范围是 0–1 的实数，<b>不是</b> 0–255 的整数。W3C 的全部公式都写在这个量纲上
/// （<c>min(1, Cb / (1 - Cs))</c> 里的 1 就是 1.0），照抄量纲可以避免每一处都手算 255 的倍数时算错。</para>
/// </remarks>
internal readonly record struct Rgb3(double R, double G, double B);

/// <summary>
/// 24 个混合模式的<b>逐通道标量数学</b>。全部为纯函数，无状态、无分配。
/// </summary>
/// <remarks>
/// <para><b>记号与出处</b>。全文沿用 W3C Compositing and Blending Level 1 的记号：
/// <c>Cb</c> = backdrop（下方已有的内容）、<c>Cs</c> = source（上方待混合的内容）。
/// 每条公式的注释都标了<b>精确行号</b>，行号指向 W3C 编辑器源码
/// <c>https://raw.githubusercontent.com/w3c/csswg-drafts/main/compositing-1/Overview.bs</c>
/// （Bikeshed 版本，与 CRD 2024-03-21 渲染稿同源）。W3C <b>没有</b>定义的 8 个模式
/// 在各自的 &lt;remarks&gt; 里注明了出处与把握度。</para>
///
/// <para><b>🔴 24 个模式分两类来源，把握度不同，务必区别对待</b>：
/// <list type="bullet">
/// <item><b>16 个（W3C 有规范公式）</b>：normal, darken, multiply, lighten, screen, overlay,
/// hardLight, colorBurn, colorDodge, softLight, difference, exclusion, hue, saturation, color, luminosity。
/// 公式逐字来自 W3C，<b>把握度高</b>。</item>
/// <item><b>8 个（W3C 没有定义）</b>：linearBurn, linearDodge, vividLight, linearLight,
/// pinLight, hardMix, subtract, divide。
/// 只能取自 Adobe 官方文档的散文描述 + 公开实现，<b>Core Image 是闭源的，
/// 原理上无法证明与 Mac 版逐像素一致</b>。其中 <see cref="PinLight"/> 把握度最低，
/// 已列入「已知差异」，等 Tier 2 实测裁决。</item>
/// </list></para>
///
/// <para><b>本类是 internal</b>：对外只暴露 <see cref="BlendOps"/> 的缓冲级入口。
/// 这些标量函数是实现细节，外部若直接调用就绕开了「反预乘 → 混合 → 预乘」的收口，
/// 容易造出铁律 2 说的那种「一个模块预乘一个直通、边缘发黑且测试抓不住」的返工。</para>
/// </remarks>
internal static class BlendMath
{
    // ══════════════════════════════════════════════════════════════════════
    //  非可分离模式的辅助函数（W3C §10.2，Overview.bs:1398-1435）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary><c>Lum(C) = 0.3 x Cred + 0.59 x Cgreen + 0.11 x Cblue</c>。</summary>
    /// <param name="r">红通道。</param>
    /// <param name="g">绿通道。</param>
    /// <param name="b">蓝通道。</param>
    /// <returns>亮度，权重和为 1.0（0.3 + 0.59 + 0.11），故落在三通道的极值区间内。</returns>
    /// <remarks>逐字对应 <c>Overview.bs:1401</c>。权重是 Rec.601 的，不是 Rec.709——
    /// 这组 0.3/0.59/0.11 是 W3C、Core Graphics 与 Core Image 在非可分离模式上的一致选择，
    /// 不要"顺手改成" 0.2126/0.7152/0.0722，那是 Rec.709，色相/饱和度/明度四个模式会整体偏移。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Lum(double r, double g, double b) => (0.3 * r) + (0.59 * g) + (0.11 * b);

    /// <summary>
    /// <c>ClipColor(C)</c>：把 <paramref name="r"/>/<paramref name="g"/>/<paramref name="b"/> 中
    /// 超出 [0,1] 的部分，沿「保持亮度」的方向拉回色域。<c>Overview.bs:1403-1413</c>
    /// </summary>
    /// <param name="r">红通道。</param>
    /// <param name="g">绿通道。</param>
    /// <param name="b">蓝通道。</param>
    /// <returns>已钳回色域的三元组。</returns>
    /// <remarks>
    /// <para>规范定义：<c>L = Lum(C)</c>、<c>n = min</c>、<c>x = max</c>，
    /// <c>if (n &lt; 0) C = L + ((C - L) × L) / (L - n)</c>，
    /// <c>if (x &gt; 1) C = L + ((C - L) × (1 - L)) / (x - L)</c>。
    /// 这里把 <c>((C-L)×常数)/(常数')</c> 提成了「先算比例系数、再逐通道乘」，
    /// 与规范逐字等价，且少一次除法。</para>
    /// <para><b>规范没写、但必须处理的两处零除</b>：当三通道<b>全相等</b>且该值 &lt; 0 时
    /// <c>L == n</c>，当三通道全相等且该值 &gt; 1 时 <c>L == x</c>，两个分母都是 0，
    /// 此时分子 <c>C - L</c> 也全是 0，IEEE 下会算出 NaN。两个分支的除法都是<b>被 0/0 保护</b>的：
    /// 跳过即保持原值，而原值本身就是均匀的 L，调整它没有任何意义。
    /// 这是规范留给实现者的空洞，本实现选择「跳过」而不是「抛异常」。</para>
    /// <para><c>n</c> 与 <c>x</c> 按规范在<b>进入时</b>各算一次即可：
    /// 第一个分支只把 &lt; 0 的通道朝 L 上拉，而 <c>L ≤ x</c>，所以拉完最大值不会变。</para>
    /// </remarks>
    public static Rgb3 ClipColor(double r, double g, double b)
    {
        double n = Math.Min(r, Math.Min(g, b));
        double x = Math.Max(r, Math.Max(g, b));
        double l = Lum(r, g, b);

        if (n < 0.0)
        {
            double denom = l - n;
            if (denom != 0.0)
            {
                double k = l / denom;
                r = l + ((r - l) * k);
                g = l + ((g - l) * k);
                b = l + ((b - l) * k);
            }
        }

        if (x > 1.0)
        {
            double denom = x - l;
            if (denom != 0.0)
            {
                double k = (1.0 - l) / denom;
                r = l + ((r - l) * k);
                g = l + ((g - l) * k);
                b = l + ((b - l) * k);
            }
        }

        return new Rgb3(r, g, b);
    }

    /// <summary><c>SetLum(C, l)</c>：整体平移三通道使亮度变成 <paramref name="l"/>，再钳回色域。<c>Overview.bs:1415-1420</c></summary>
    /// <param name="r">红通道。</param>
    /// <param name="g">绿通道。</param>
    /// <param name="b">蓝通道。</param>
    /// <param name="l">目标亮度。</param>
    /// <returns>亮度被改成 <paramref name="l"/>（钳位后）的三元组。</returns>
    /// <remarks>
    /// 规范定义 <c>d = l - Lum(C)</c>，三通道<b>同加</b> <c>d</c>，再 <c>ClipColor</c>。
    /// 之所以要 ClipColor：同加会让亮通道越界（加白时），
    /// 规范的做法是沿亮度不变的方向把越界部分压回，而不是直接截断到 1——直接截断会顺带改掉亮度。
    /// </remarks>
    public static Rgb3 SetLum(double r, double g, double b, double l)
    {
        double d = l - Lum(r, g, b);
        return ClipColor(r + d, g + d, b + d);
    }

    /// <summary><see cref="SetLum(double, double, double, double)"/> 的 <see cref="Rgb3"/> 重载。</summary>
    /// <param name="c">待调整的三元组。</param>
    /// <param name="l">目标亮度。</param>
    /// <returns>亮度被改成 <paramref name="l"/>（钳位后）的三元组。</returns>
    /// <remarks>四个非可分离模式的公式最终都落到「先 <c>SetSat</c> 得三元组、再 <c>SetLum</c> 定亮度」，
    /// 所以这个形状才是调用点真正需要的那个；逐通道版本保留给将来需要单通道调用的场合。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgb3 SetLum(Rgb3 c, double l) => SetLum(c.R, c.G, c.B, l);

    /// <summary><c>Sat(C) = max(Cred, Cgreen, Cblue) - min(Cred, Cgreen, Cblue)</c>。<c>Overview.bs:1422</c></summary>
    /// <param name="r">红通道。</param>
    /// <param name="g">绿通道。</param>
    /// <param name="b">蓝通道。</param>
    /// <returns>饱和度，实数。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Sat(double r, double g, double b) =>
        Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    /// <summary>
    /// <c>SetSat(C, s)</c>：保持各通道的相对次序不变，把饱和度重设为 <paramref name="s"/>。<c>Overview.bs:1427-1434</c>
    /// </summary>
    /// <param name="r">红通道。</param>
    /// <param name="g">绿通道。</param>
    /// <param name="b">蓝通道。</param>
    /// <param name="s">目标饱和度。</param>
    /// <returns>重设后的三元组。</returns>
    /// <remarks>
    /// <para>规范原文（<c>Overview.bs:1424-1425</c> 特别注明下标指的是
    /// <i>"the color components having the minimum, middle, and maximum values
    /// <b>upon entry to the function</b>"</i>）：
    /// <code>
    /// if (Cmax &gt; Cmin) { Cmid = ((Cmid - Cmin) x s) / (Cmax - Cmin); Cmax = s; }
    /// else               { Cmid = Cmax = 0; }
    /// Cmin = 0;
    /// </code></para>
    /// <para><b>因此必须先按下标记住谁是 min/mid/max，再写回</b>。
    /// 常见写法 <c>mid = r + g + b - min - max</c> 在数学上等价，
    /// 但本方法的入参是已经除过 255 的 double，<c>(r + g + b) - min - max</c> 存在抵消误差，
    /// 中间通道可能偏离真值若干 ULP。这里用「按下标排序」保证逐字精确。</para>
    /// <para><b>灰阶早退</b>：三通道全相等时 <c>Cmax == Cmin</c>，走 else 分支得到全 0，
    /// 此时三通道没有可区分的「谁是谁」，任何下标推导都会越界，直接返回 <c>(0,0,0)</c>。
    /// 这也正是色相模式「灰色源 → 底色去饱和」的正确行为
    /// （后续 <see cref="SetLum(Rgb3, double)"/> 会把亮度补回来）。</para>
    /// </remarks>
    public static Rgb3 SetSat(double r, double g, double b, double s)
    {
        // 下标 0/1/2 = R/G/B。三元组的下标之和恒为 3，中位下标可由另两个直接求出。
        int iMax = r >= g ? (r >= b ? 0 : 2) : (g >= b ? 1 : 2);
        int iMin = r <= g ? (r <= b ? 0 : 2) : (g <= b ? 1 : 2);

        if (iMin == iMax)
        {
            return new Rgb3(0.0, 0.0, 0.0);
        }

        int iMid = 3 - iMin - iMax;
        double n = Channel(iMin, r, g, b);
        double m = Channel(iMid, r, g, b);
        double x = Channel(iMax, r, g, b);

        double mNew;
        double xNew;
        if (x > n)
        {
            mNew = ((m - n) * s) / (x - n);
            xNew = s;
        }
        else
        {
            // x == n 而 iMin != iMax：两通道相等、第三通道不同（Cmax == Cmin 不成立的情况）。
            // 规范的 else 分支给 Cmid = Cmax = 0。
            mNew = 0.0;
            xNew = 0.0;
        }

        return new Rgb3(
            WriteBack(0, iMin, iMid, mNew, xNew),
            WriteBack(1, iMin, iMid, mNew, xNew),
            WriteBack(2, iMin, iMid, mNew, xNew));
    }

    /// <summary>按下标取通道值。<paramref name="index"/> 为 0/1/2。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Channel(int index, double r, double g, double b) =>
        index == 0 ? r : index == 1 ? g : b;

    /// <summary>
    /// <see cref="SetSat"/> 的写回：min 下标写 0，mid 下标写 <paramref name="mNew"/>，max 下标写 <paramref name="xNew"/>。
    /// </summary>
    /// <param name="index">正在写入的那个通道自己的下标（0=R / 1=G / 2=B）。</param>
    /// <param name="iMin">最小值所在通道下标。</param>
    /// <param name="iMid">中位值所在通道下标。</param>
    /// <param name="mNew">中位值的新值。</param>
    /// <param name="xNew">最大值的新值。</param>
    /// <returns>本通道的新值。</returns>
    /// <remarks>
    /// 三个互斥判断的顺序无所谓 —— <paramref name="iMin"/>、<paramref name="iMid"/> 与剩下的那个
    /// 三者互不相同。<b>关键是把「正在写哪个通道」传进来</b>：
    /// 不传的话三个返回值会算成同一个通道的，这是本函数第一版的真实 bug
    /// （<c>SetSat(1,0,0,0.5)</c> 三个通道全写成 0）。
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double WriteBack(int index, int iMin, int iMid, double mNew, double xNew)
    {
        if (index == iMin)
        {
            return 0.0;
        }

        return index == iMid ? mNew : xNew;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  可分离模式 —— 全部有 W3C 公式（§10.1，Overview.bs:1190-1386）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary><c>Multiply</c>：<c>B = Cb x Cs</c>。<c>Overview.bs:1216</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Multiply(double cb, double cs) => cb * cs;

    /// <summary><c>Screen</c>：<c>B = 1 - [(1 - Cb) x (1 - Cs)]</c>。<c>Overview.bs:1230</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>采用补数相乘的写法而非等价的 <c>Cb + Cs - Cb*Cs</c>（<c>Overview.bs:1231</c>）：
    /// 前者在 <c>Cb、Cs → 0</c> 时数值稳定，后者会发生严重抵消（结果恒为 0，还丢精度）。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Screen(double cb, double cs) => 1.0 - ((1.0 - cb) * (1.0 - cs));

    /// <summary><c>Darken</c>：<c>B = min(Cb, Cs)</c>。<c>Overview.bs:1262</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Darken(double cb, double cs) => Math.Min(cb, cs);

    /// <summary><c>Lighten</c>：<c>B = max(Cb, Cs)</c>。<c>Overview.bs:1276</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>规范随后写「超出范围时向下取整」（<c>Overview.bs:1279</c>），但 <c>max</c> 的两个操作数本就在
    /// [0,1] 内，结果不可能越界。该句是照搬更早版本对加法模式的约束，本实现不额外处理。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Lighten(double cb, double cs) => Math.Max(cb, cs);

    /// <summary><c>ColorDodge</c>。<c>Overview.bs:1291-1296</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <para>规范原文：
    /// <code>
    /// if (Cb == 0)     B = 0
    /// else if (Cs == 1) B = 1
    /// else              B = min(1, Cb / (1 - Cs))
    /// </code></para>
    /// <para><b>分支顺序不可交换</b>：<c>Cb == 0</c> 与 <c>Cs == 1</c> 同时成立时规范取前者，结果是 0；
    /// 若先判 <c>Cs == 1</c> 会得到 1，差一个满量程。</para>
    /// <para>最后一个分支的分母 <c>1 - Cs</c> 此时恒 &gt; 0，不需要额外防零除。</para>
    /// </remarks>
    public static double ColorDodge(double cb, double cs)
    {
        if (cb == 0.0)
        {
            return 0.0;
        }

        if (cs == 1.0)
        {
            return 1.0;
        }

        return Math.Min(1.0, cb / (1.0 - cs));
    }

    /// <summary><c>ColorBurn</c>。<c>Overview.bs:1309-1314</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// 规范原文：
    /// <code>
    /// if (Cb == 1)     B = 1
    /// else if (Cs == 0) B = 0
    /// else              B = 1 - min(1, (1 - Cb) / Cs)
    /// </code>
    /// <b>分支顺序同样不可交换</b>：<c>Cb == 1</c> 与 <c>Cs == 0</c> 同时成立时规范取前者，结果是 1。</remarks>
    public static double ColorBurn(double cb, double cs)
    {
        if (cb == 1.0)
        {
            return 1.0;
        }

        if (cs == 0.0)
        {
            return 0.0;
        }

        return 1.0 - Math.Min(1.0, (1.0 - cb) / cs);
    }

    /// <summary><c>HardLight</c>：按 <paramref name="cs"/> 与 0.5 的大小关系选乘或筛。<c>Overview.bs:1327-1330</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>规范原文 <c>if (Cs &lt;= 0.5) B = Multiply(Cb, 2 x Cs) else B = Screen(Cb, 2 x Cs - 1)</c>。
    /// 注意判的是 <b>source</b>（照在 backdrop 上的那束光），这正是它与 <see cref="Overlay"/> 的区别。</remarks>
    public static double HardLight(double cb, double cs) =>
        cs <= 0.5 ? Multiply(cb, 2.0 * cs) : Screen(cb, (2.0 * cs) - 1.0);

    /// <summary><c>Overlay</c>：<c>B = HardLight(Cs, Cb)</c>，即两个参数对调。<c>Overview.bs:1245</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>规范自己说 Overlay 是 HardLight 的逆运算并直接给出对调写法（<c>Overview.bs:1248</c>）。
    /// 对调后分支条件变成「backdrop 与 0.5 比较」，即<b>底色</b>决定乘还是筛 —— 与 Overlay 的语义一致。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Overlay(double cb, double cs) => HardLight(cs, cb);

    /// <summary><c>SoftLight</c>。<c>Overview.bs:1345-1353</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// 规范原文：
    /// <code>
    /// if (Cs &lt;= 0.5) B = Cb - (1 - 2 x Cs) x Cb x (1 - Cb)
    /// else           B = Cb + (2 x Cs - 1) x (D(Cb) - Cb)
    /// where  if (Cb &lt;= 0.25) D(Cb) = ((16 x Cb - 12) x Cb + 4) x Cb
    ///        else            D(Cb) = sqrt(Cb)
    /// </code>
    /// 上分支的三次多项式在 <c>0.5 → 0.5</c>、<c>0.25 → 0.25</c> 处与 sqrt 分支连续，是刻意设计的。</remarks>
    public static double SoftLight(double cb, double cs)
    {
        if (cs <= 0.5)
        {
            return cb - ((1.0 - (2.0 * cs)) * cb * (1.0 - cb));
        }

        double d = cb <= 0.25
            ? ((((16.0 * cb) - 12.0) * cb) + 4.0) * cb
            : Math.Sqrt(cb);
        return cb + (((2.0 * cs) - 1.0) * (d - cb));
    }

    /// <summary><c>Difference</c>：<c>B = |Cb - Cs|</c>。<c>Overview.bs:1368</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Difference(double cb, double cs) => Math.Abs(cb - cs);

    /// <summary><c>Exclusion</c>：<c>B = Cb + Cs - 2 x Cb x Cs</c>。<c>Overview.bs:1380</c></summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Exclusion(double cb, double cs) => cb + cs - (2.0 * cb * cs);

    // ══════════════════════════════════════════════════════════════════════
    //  W3C 未定义的 8 个模式
    //  以下公式无规范可依，出处与把握度逐条标注。Core Image 为闭源实现，
    //  原理上无法证明与 Mac 版逐像素一致 —— 这是 Tier 3，诚实声明而不是假装对齐。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary><c>Linear Burn</c>：<c>B = max(0, Cb + Cs - 1)</c>。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <b>出处</b>：W3C <b>没有</b>定义 linear burn。255 量纲下的 <c>C = A + B - 255</c>
    /// 在 Adobe Learn「Blending mode descriptions」与多个独立实现中一致，换算到 0–1 即本式。
    /// <b>把握度：高</b>（含钳位在内所有来源都一致，无分支、无奇点）。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double LinearBurn(double cb, double cs) => Math.Max(0.0, cb + cs - 1.0);

    /// <summary><c>Linear Dodge (Add)</c>：<c>B = min(1, Cb + Cs)</c>。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <b>出处</b>：W3C <b>没有</b>定义。255 量纲下 <c>C = A + B</c> 上限 255，即 0–1 量纲下钳到 1。
    /// Adobe Learn 原文对 Divide 的描述中把 linear dodge 称为「plus lighter」，
    /// CSS <c>plus-lighter</c> 同样是加法后钳位，三者一致。
    /// <b>把握度：高</b>。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double LinearDodge(double cb, double cs) => Math.Min(1.0, cb + cs);

    /// <summary><c>Subtract</c>：<c>B = max(0, Cb - Cs)</c>。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <b>出处</b>：W3C <b>没有</b>定义。Adobe Learn 原文
    /// <i>"Subtracts the source file from the underlying color. ... In 8- and 16-bit images,
    /// any resulting negative values are clipped to zero."</i>
    /// —— 源减底、负值钳零。注意 32-bit 工程里 Adobe 允许负值，本项目是 8-bit 缓冲，钳零是唯一选择。
    /// <b>把握度：高</b>。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Subtract(double cb, double cs) => Math.Max(0.0, cb - cs);

    /// <summary><c>Divide</c>：<c>Cs == 0 ? 1 : min(1, Cb / Cs)</c>。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <b>出处</b>：W3C <b>没有</b>定义。Adobe Learn：<i>"Divides underlying color by source color."</i>
    /// <b>除零方向</b>：IEEE 下 <c>Cb / 0</c> 得 ±∞ 或 NaN，钳到 1 后 NaN 仍是 NaN，会污染整条像素。
    /// 因此显式判 <c>Cs == 0 → 1.0</c>（白），这也与「与黑色相除得白」的实际效果一致。
    /// <b>把握度：高</b>。</remarks>
    public static double Divide(double cb, double cs) =>
        cs == 0.0 ? 1.0 : Math.Min(1.0, cb / cs);

    /// <summary><c>Linear Light</c>：<c>B = clamp(Cb + 2 x Cs - 1, 0, 1)</c>。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <b>出处</b>：W3C <b>没有</b>定义。255 量纲下 <c>C = A + 2 x B - 255</c>，三个独立来源一致。
    /// <para>可写成两个分支：<c>Cs &lt; 0.5</c> 时等价于 <c>LinearBurn(Cb, 2 x Cs)</c>（只会低于下界），
    /// <c>Cs &gt; 0.5</c> 时等价于 <c>LinearDodge(Cb, 2 x Cs - 1)</c>（只会高于上界）。
    /// 两支合并后就是单次钳位，本实现取合并形式——它同时解释了为什么这个模式不存在分支奇点。</para>
    /// <b>把握度：高</b>（三个来源 + 上述等价推导互相印证）。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double LinearLight(double cb, double cs) => Math.Clamp(cb + (2.0 * cs) - 1.0, 0.0, 1.0);

    /// <summary><c>Vivid Light</c>：暗源走 ColorBurn、亮源走 ColorDodge，源按中灰做 2 倍重映射。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <code>
    /// Cs &lt;= 0.5 → B = ColorBurn (Cb, 2 x Cs)
    /// Cs &gt;  0.5 → B = ColorDodge(Cb, 2 x Cs - 1)
    /// </code>
    /// <b>出处</b>：W3C <b>没有</b>定义。三处互相印证：
    /// <list type="number">
    /// <item>Wikipedia「Blend modes」：<i>"this blend mode combines Color Dodge and Color Burn
    /// (rescaled so that neutral colors become middle gray). Dodge applies when values in the
    /// top layer are lighter than middle gray, and burn applies to darker values."</i>
    /// —— 给出了结构（暗 burn 亮 dodge）<b>和</b>重映射的<b>原因</b>。</item>
    /// <item>Adobe Learn：<i>"Burns or dodges the colors by increasing or decreasing the contrast,
    /// depending on the blend color. If the blend color is lighter than 50% gray, the image is lightened."</i>
    /// —— 独立确认了「亮源变亮」即走 dodge。</item>
    /// <item>compass-blend-modes（可执行实现）：<c>if (fg &lt; 128) colorburn(2*fg, bg) else colordodge(2*(fg-128), bg)</c>
    /// —— 给出了 2 倍重映射的<b>具体参数</b>。</item>
    /// </list>
    /// <b>把握度：高</b>（三来源的分支方向一致，重映射有独立佐证）。
    /// 仍属 Core Image 闭源实现，Tier 2 实测前不宣称与 Mac 版逐像素一致。</remarks>
    public static double VividLight(double cb, double cs) =>
        cs <= 0.5 ? ColorBurn(cb, 2.0 * cs) : ColorDodge(cb, (2.0 * cs) - 1.0);

    /// <summary><c>HardMix</c>：<c>B = Cb + Cs &gt;= 1 ? 1 : 0</c>。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>0 或 1。</returns>
    /// <remarks>
    /// <b>出处</b>：W3C <b>没有</b>定义。255 量纲下 <c>C = A + B &gt;= 255 ? 255 : 0</c>，
    /// Adobe Learn、多个独立实现一致。compass 的等价写法是「先算 VividLight，再按是否 ≥ 128 取 0/255」，
    /// 两者只在 <c>Cb + Cs</c> 恰好为 1 且 VividLight 恰为 0 的孤立点上不一致（例如 <c>Cb = 0, Cs = 1</c>：
    /// 本式给 1，compass 给 0）。本实现采用 Adobe 直接描述的加法式。
    /// <b>把握度：高</b>。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double HardMix(double cb, double cs) => (cb + cs) >= 1.0 ? 1.0 : 0.0;

    /// <summary><c>PinLight</c>：暗源取暗（min）、亮源取亮（max），源按中灰做 2 倍重映射。</summary>
    /// <param name="cb">backdrop 通道值。</param>
    /// <param name="cs">source 通道值。</param>
    /// <returns>混合后的通道值。</returns>
    /// <remarks>
    /// <code>
    /// Cs &lt;= 0.5 → B = min(Cb, 2 x Cs)
    /// Cs &gt;  0.5 → B = max(Cb, 2 x Cs - 1)
    /// </code>
    /// <b>🔴 把握度：低 —— 本项目 24 个模式里唯一低于「高」的一个，已列入「已知差异」。</b>
    /// <para><b>已确定的部分</b>：分支方向是「暗源取 min、亮源取 max」。Adobe Learn 原文
    /// <i>"If the blend color is lighter than 50% gray, pixels darker than the blend color are replaced"</i>
    /// （被替换的像素因此变亮 → max），<i>"If the blend color is darker than 50% gray,
    /// pixels lighter than the blend color are replaced"</i>（被替换的像素因此变暗 → min）——
    /// 与 compass 实现 <c>darken / lighten</c> 的分支完全一致。</para>
    /// <para><b>存疑的部分</b>：那个「2 倍重映射」到底该不该有。compass 有（<c>min(Cb, 2*Cs)</c>），
    /// 若按 Adobe 散文直译则没有（<c>min(Cb, Cs)</c>）。本实现选<b>有重映射</b>，理由是
    /// vivid / linear / pin 三个模式同属「对比度组」，共享同一套中灰重映射约定；
    /// 且 Adobe 对 Pin Light 的描述是「extreme、all mid-tones removed」，与重映射版在
    /// <c>Cs = 0.5</c> 处直接归零的极端行为相符。<b>但这是推断，不是规范。</b>
    /// 未重映射的候选式是 <c>Cs &lt;= 0.5 ? min(Cb, Cs) : max(Cb, Cs)</c>，
    /// 两者在 <c>Cs = 0.5</c> 与 <c>Cs ∈ (0.25, 0.5)</c> 上结果不同。
    /// 待 Tier 2（macOS runner 实测 CoreImage 输出）裁决后修正。</para>
    /// </remarks>
    public static double PinLight(double cb, double cs) =>
        cs <= 0.5 ? Math.Min(cb, 2.0 * cs) : Math.Max(cb, (2.0 * cs) - 1.0);
}