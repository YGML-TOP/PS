using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests.Contract;

/// <summary>
/// 🔴 几何契约层（<c>Geometry.cs</c>）与变换契约层（<c>Transform/LayerTransform.cs</c>）的回归锁。
/// </summary>
/// <remarks>
/// <para><b>期望值全部手算</b>：本文件里的每一条期望值都是从源码公式与原项目 Swift 语义推导出来的，
/// <b>不是跑一遍实现抄回来的</b>。用被测实现给自己盖章会把直译错误一起锁死，届时两边一起错也照样全绿。</para>
///
/// <para><b>铁律 1 是本文件的第一优先级</b>：文档坐标 Y 向下，且 <see cref="DocCoord"/> 的两个方法
/// <b>不做任何 Y 翻转</b>。全项目只允许在这两个方法里做坐标换算，因此"换算是纯线性、不取反"这件事
/// 必须被单独钉死 —— 它一旦被破坏，编译照过、测试照绿，只有整幅图上下颠倒。</para>
///
/// <para><b>浮点约定</b>：涉及期望值的用例一律取二进制可精确表示的数（0.25 / 0.5 / 1.25 / 1.5 这类），
/// 并在注释里写出逐步计算，便于逐位复核；涉及 π 的断言统一用小数位精度比较（<c>precision</c>），
/// 而不是"容差随便给一点"。</para>
///
/// <para><b>分工</b>：采样档位的三个字面量往返、未知值抛 <see cref="FormatException"/>、
/// 枚举连续性都已由 <c>ContractStringTests</c> 覆盖，本文件不重复，只补了一处它没写的
/// <see cref="ArgumentNullException"/> 分支。</para>
/// </remarks>
public sealed class GeometryTests
{
    // ═══════════════════ 铁律 1：DocCoord.FromView 不做 Y 翻转 ═══════════════════

    /// <summary>
    /// 🔴 <see cref="DocCoord.FromView"/> 在 Y 上<b>什么都不做</b>：视图 Y 原样减去 OriginY，
    /// 再除以 <c>PointsPerPixel</c>，符号与量纲都不变。
    /// </summary>
    /// <remarks>
    /// 手算（刻意取非 1 的缩放与非 0 的原点，否则"翻转了但看不出来"）：
    /// <list type="bullet">
    /// <item>视口 <c>Viewport(OriginX=10, OriginY=20, PointsPerPixel=2, Scale=2)</c></item>
    /// <item>输入 <c>ViewPoint(100, 300)</c></item>
    /// <item>X = (100 - 10) / 2 = 45</item>
    /// <item>Y = (300 - 20) / 2 = 140 <b>（没有取负）</b></item>
    /// </list>
    /// 依据原项目 <c>Rendering/CanvasViewport.swift:25-33</c> 的 <c>(point.y - origin.y) / pointsPerPixel</c>：
    /// 纯线性变换，无 Y 取反。视图层若需要 Y 向上，由 UI 层在自己那一侧翻转。
    /// </remarks>
    [Fact]
    public void FromView_不做Y翻转_只做线性平移与缩放()
    {
        var viewport = new Viewport(10.0, 20.0, 2.0, 2.0);

        var doc = DocCoord.FromView(new ViewPoint(100.0, 300.0), viewport);

        Assert.Equal(45.0, doc.X);
        Assert.Equal(140.0, doc.Y);
    }

    /// <summary>
    /// 🔴 <b>同一视图 Y 的所有点，必须得到<b>完全相同</b>的文档 Y</b>。
    /// </summary>
    /// <remarks>
    /// 这条比"某几个点算对"更强：只要实现里掺进一点点耦合（Y 乘上 X、或者拿 X 一起算行列式、
    /// 或者对 Y 做了 <c>sqrt</c>/取绝对值之类的非线性），同一个 Y 就会散成好几个值。
    /// 换算必须是 X、Y 各自独立的线性变换。
    /// <para>手算：视图 Y 固定为 300，则文档 Y = (300 - 20) / 2 = 140，与传入的 X 完全无关。</para>
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(37.5)]
    [InlineData(-12.25)]
    [InlineData(1000000.0)]
    public void FromView_同一视图Y的所有点得到完全相同的文档Y(double viewX)
    {
        var viewport = new Viewport(10.0, 20.0, 2.0, 2.0);

        var doc = DocCoord.FromView(new ViewPoint(viewX, 300.0), viewport);

        // (300 - 20) / 2 = 140，与 X 无关
        Assert.Equal(140.0, doc.Y);
    }

    /// <summary>
    /// 🔴 视图 Y 增大，文档 Y 必须<b>同向</b>增大（斜率 +1/ppp），不能反向。
    /// </summary>
    /// <remarks>
    /// 逐个手算，Y = (viewY - 20) / 2：
    /// <list type="bullet">
    /// <item>(-100 - 20) / 2 = -60</item>
    /// <item>(0 - 20) / 2 = -10</item>
    /// <item>(20 - 20) / 2 = 0</item>
    /// <item>(21 - 20) / 2 = 0.5</item>
    /// <item>(300 - 20) / 2 = 140</item>
    /// </list>
    /// 期望序列严格递增。若实现取负，这串值会变成 +60、+10、0、-0.5、-140（严格递减）。
    /// </remarks>
    [Fact]
    public void FromView_视图Y增大则文档Y同向增大_不会反向()
    {
        var viewport = new Viewport(10.0, 20.0, 2.0, 2.0);
        double[] viewYs = { -100.0, 0.0, 20.0, 21.0, 300.0 };
        double[] expectedDocYs = { -60.0, -10.0, 0.0, 0.5, 140.0 };

        for (int i = 0; i < viewYs.Length; i++)
        {
            var doc = DocCoord.FromView(new ViewPoint(0.0, viewYs[i]), viewport);
            Assert.Equal(expectedDocYs[i], doc.Y);
        }
    }

    /// <summary>
    /// 🔴 <b>反向对照</b>：把"常见的四种 Y 翻转写法"算一遍，证明它们的结果都和实际值不同 ——
    /// 于是"实际值等于纯线性结果"这件事就足以排除翻转。
    /// </summary>
    /// <remarks>
    /// 同一组输入（Origin=(10,20)、ppp=2、p=(100,300)），真值 Y = (300 - 20) / 2 = 140。
    /// 假如实现里混进下面任何一种翻转，Y 都会变成：
    /// <list type="number">
    /// <item>取负翻转 -((y - oy) / ppp) = -140</item>
    /// <item>绕原点镜像 oy + (oy - y) / ppp = 20 + (20 - 300) / 2 = -120</item>
    /// <item>AppKit 常见的高度镜像 (canvasH - y) / ppp，canvasH = 500 时 = (500 - 300) / 2 = 100</item>
    /// <item>减去原点的高度镜像 (canvasH - y - oy) / ppp，canvasH = 500 时 = (500 - 300 - 20) / 2 = 90</item>
    /// </list>
    /// 四个候选值两两不同且都 ≠ 140，因此这条测试能排除以上全部翻转形态。
    /// </remarks>
    [Fact]
    public void FromView_反向对照_任何一种Y翻转写法都会得到不同结果()
    {
        var viewport = new Viewport(10.0, 20.0, 2.0, 2.0);
        const double canvasHeight = 500.0;

        var doc = DocCoord.FromView(new ViewPoint(100.0, 300.0), viewport);

        Assert.Equal(140.0, doc.Y);

        // 四种翻转假设的值，逐一排除
        Assert.NotEqual(-140.0, doc.Y);                                              // -(y - oy) / ppp
        Assert.NotEqual(-120.0, doc.Y);                                              // oy + (oy - y) / ppp
        Assert.NotEqual((canvasHeight - 300.0) / 2.0, doc.Y);                        // (canvasH - y) / ppp
        Assert.NotEqual((canvasHeight - 300.0 - 20.0) / 2.0, doc.Y);                 // (canvasH - y - oy) / ppp
    }

    /// <summary>
    /// <see cref="DocCoord.FromView"/> 的逐点期望值，锁死公式 <c>(p - origin) / ppp</c> 本身
    /// （而不只是它的可逆性）。
    /// </summary>
    /// <remarks>
    /// 每一行都可在注释给出的算式里复核；全部选二进制可精确表示、且除法能整除的输入，
    /// 所以断言用精确相等成立（没有舍入误差）。
    /// </remarks>
    [Theory]
    [InlineData(2.0, 10.0, 20.0, 100.0, 300.0, 45.0, 140.0)]      // (100-10)/2, (300-20)/2
    [InlineData(2.0, 10.0, 20.0, -0.5, 21.0, -5.25, 0.5)]        // (-0.5-10)/2, (21-20)/2
    [InlineData(1.5, 10.0, 20.0, 100.0, 320.0, 60.0, 200.0)]      // (100-10)/1.5, (320-20)/1.5
    [InlineData(3.0, -5.0, 7.5, 100.0, 82.5, 35.0, 25.0)]        // (100+5)/3, (82.5-7.5)/3
    [InlineData(0.25, 13.0, -11.0, 100.0, 12.0, 348.0, 92.0)]     // (100-13)*4, (12+11)*4
    [InlineData(4.0, 0.0, 0.0, 1.5, -7.25, 0.375, -1.8125)]       // 1.5/4, -7.25/4
    [InlineData(1.25, 2.5, -3.75, 27.5, -13.75, 20.0, -8.0)]       // (27.5-2.5)/1.25, (-13.75+3.75)/1.25
    [InlineData(1.0, 0.0, 0.0, 7.0, 9.0, 7.0, 9.0)]               // 1:1 且无原点时为恒等
    public void FromView_按公式换算到预期的文档坐标(
        double pointsPerPixel, double originX, double originY,
        double viewX, double viewY,
        double expectedX, double expectedY)
    {
        var viewport = new Viewport(originX, originY, pointsPerPixel, 1.0);

        var doc = DocCoord.FromView(new ViewPoint(viewX, viewY), viewport);

        Assert.Equal(expectedX, doc.X);
        Assert.Equal(expectedY, doc.Y);
    }

    /// <summary>
    /// 🔴 坐标换算的分母只能是 <c>PointsPerPixel</c>，<c>Scale</c> 一律不参与。
    /// </summary>
    /// <remarks>
    /// <c>Viewport.Scale</c> 是留给 UI 层渲染用的重复表示。若实现误把它当除数，
    /// 同一份 <c>Viewport</c> 在不同调用点会算出不同坐标，且因为"两处都能跑"很难被发现。
    /// 这里把 Scale 设成一个绝不可能与 PointsPerPixel 约分的值（99 对 2），一旦被误用立刻错得很明显。
    /// </remarks>
    [Fact]
    public void FromView_只按PointsPerPixel换算_Scale不参与()
    {
        var viewport = new Viewport(10.0, 20.0, 2.0, 99.0);

        var doc = DocCoord.FromView(new ViewPoint(100.0, 300.0), viewport);

        Assert.Equal(45.0, doc.X);
        Assert.Equal(140.0, doc.Y);
    }

    // ═══════════════════ DocCoord 往返 ═══════════════════

    /// <summary>
    /// 🔴 往返恒等：<c>ToView(FromView(p)) == p</c>，必须是<b>逐位精确相等</b>。
    /// </summary>
    /// <remarks>
    /// 为什么必须精确相等而不是"误差小于 1e-9"：一旦实现里混进一次 Y 取反、或者把 Origin 加在了
    /// 错误的一侧，往返误差会是一个 Origin 或一个符号量 —— 用宽松容差断言会把这种结构性错误放过，
    /// 只把浮点噪声当成了安全网。
    /// <para>为了让精确相等真正成立（而不是靠运气），下面每行的输入都满足
    /// <c>(p - o) / ppp</c> 与 <c>q * ppp + o</c> 两步都<b>无舍入误差</b>：
    /// 所有输入值均为二进制可精确表示的数，且差值除以缩放后仍是整数或二进制分数。</para>
    /// </remarks>
    [Theory]
    [InlineData(2.0, 10.0, 20.0, 100.0, 300.0)]     // (100-10)/2=45 → 45*2+10=100；(300-20)/2=140 → 300
    [InlineData(1.5, 10.0, 20.0, 100.0, 320.0)]     // (100-10)/1.5=60 → 90+10；(320-20)/1.5=200 → 300+20
    [InlineData(3.0, -5.0, 7.5, 100.0, 82.5)]       // (100+5)/3=35 → 105-5；(82.5-7.5)/3=25 → 75+7.5
    [InlineData(0.25, 13.0, -11.0, 100.0, 12.0)]    // (100-13)/0.25=348 → 87+13；(12+11)/0.25=92 → 23-11
    [InlineData(4.0, 0.0, 0.0, 1.5, -7.25)]         // 1.5/4=0.375 → 1.5；-7.25/4=-1.8125 → -7.25
    [InlineData(1.25, 2.5, -3.75, 27.5, -13.75)]    // 25/1.25=20 → 25+2.5；-10/1.25=-8 → -10-3.75
    public void ToView_往返于FromView精确还原(
        double pointsPerPixel, double originX, double originY, double viewX, double viewY)
    {
        var viewport = new Viewport(originX, originY, pointsPerPixel, 1.0);
        var original = new ViewPoint(viewX, viewY);

        var roundTripped = DocCoord.ToView(DocCoord.FromView(original, viewport), viewport);

        Assert.Equal(original, roundTripped);
    }

    /// <summary>
    /// 🔴 铁律 1 在<b>反方向</b>同样成立：<see cref="DocCoord.ToView"/> 也不能翻转 Y，
    /// 否则"翻转只发生在边界里"这条规则会从另一头破掉。
    /// </summary>
    /// <remarks>
    /// 手算：Origin=(10,20)、ppp=2、文档点 (45,140)
    /// <list type="bullet">
    /// <item>X = 45 * 2 + 10 = 100</item>
    /// <item>Y = 140 * 2 + 20 = 300 <b>（没有取负）</b></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void ToView_同样不做Y翻转()
    {
        var viewport = new Viewport(10.0, 20.0, 2.0, 2.0);

        var view = DocCoord.ToView(new DocPoint(45.0, 140.0), viewport);

        Assert.Equal(100.0, view.X);
        Assert.Equal(300.0, view.Y);
    }

    // ═══════════════════ DocCoord 参数校验 ═══════════════════

    /// <summary>
    /// 🔴 <c>PointsPerPixel</c> 不是有限正数时，<see cref="DocCoord.FromView"/> 必须抛
    /// <see cref="ArgumentOutOfRangeException"/>，且参数名固定为 <c>viewport</c>。
    /// </summary>
    /// <remarks>
    /// 为什么必须抛而不是让结果变成 ±Infinity / NaN：这类脏值会一路流到图层树里，
    /// 全程<b>没有异常、没有日志</b>，最终表现只是图层"飞到天边"，排查成本极高。
    /// 在唯一的换算边界一次拦下，错误定位成本最低。
    /// <para>覆盖 0、负数、NaN、±Infinity 以及 double.MinValue（有限但为负）五种非法形态。</para>
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MinValue)]
    public void FromView_缩放非有限正数时抛ArgumentOutOfRangeException(double pointsPerPixel)
    {
        var viewport = new Viewport(10.0, 20.0, pointsPerPixel, 1.0);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => DocCoord.FromView(new ViewPoint(100.0, 300.0), viewport));

        Assert.Equal("viewport", ex.ParamName!);
    }

    /// <summary>
    /// 🔴 反方向同样必须抛：<see cref="DocCoord.ToView"/> 与 FromView 共用同一条校验，
    /// 任何一边漏掉都会让脏缩放从那条路重新进入图层树。
    /// </summary>
    /// <remarks>
    /// <c>ToView</c> 虽然不做除法（只有乘法），但 §校验 依然拦同样的输入 ——
    /// 因为 <c>Viewport</c> 本身是无效的，放行等于允许同一个非法状态在两个方向上行为不一致
    /// （FromView 抛、ToView 静默），调用方没法写出统一的重试或降级逻辑。
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ToView_缩放非有限正数时抛ArgumentOutOfRangeException(double pointsPerPixel)
    {
        var viewport = new Viewport(10.0, 20.0, pointsPerPixel, 1.0);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => DocCoord.ToView(new DocPoint(45.0, 140.0), viewport));

        Assert.Equal("viewport", ex.ParamName!);
    }

    /// <summary>
    /// 🔴 极小的正数缩放<b>必须被拦下</b>，不能静默产出 ±Infinity。
    /// </summary>
    /// <remarks>
    /// <para>只校验 <c>PointsPerPixel &gt; 0</c> 是不够的：<c>double.Epsilon</c>（≈4.94e-324）
    /// 是<b>有限正数</b>，而 <c>1.0 / double.Epsilon ≈ 2.02e323</c> 越过 double 上限溢出成 +Infinity ——
    /// 恰好是 <see cref="DocCoord"/> 注释里要禁止的那种"静默污染"。</para>
    /// <para>因此校验落在<b>换算结果</b>上：结果不是有限值就抛
    /// <see cref="ArgumentOutOfRangeException"/>。</para>
    /// </remarks>
    [Fact]
    public void FromView_极小正缩放被拦下而不是产出Infinity()
    {
        var viewport = new Viewport(0.0, 0.0, double.Epsilon, 1.0);

        // 1.0 / double.Epsilon  → +Infinity
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DocCoord.FromView(new ViewPoint(1.0, 1.0), viewport));

        // -1.0 / double.Epsilon → -Infinity
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DocCoord.FromView(new ViewPoint(-1.0, -1.0), viewport));
    }

    /// <summary>
    /// 🔴 <see cref="DocCoord.ToView"/> 的<b>对称</b>防护：乘法同样会溢出。
    /// </summary>
    /// <remarks>
    /// <c>1e300 × 1e300 = 1e600</c> 越过 double 上限。
    /// 只在 <c>FromView</c> 上加防护会留下这个镜像漏洞 ——
    /// 视图坐标回传到文档坐标时同样会产出 Infinity 并污染整棵图层树。
    /// </remarks>
    [Fact]
    public void ToView_极大缩放乘出Infinity时被拦下()
    {
        var viewport = new Viewport(0.0, 0.0, 1e300, 1.0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DocCoord.ToView(new DocPoint(1e300, 1e300), viewport));
    }

    /// <summary>
    /// 防护不能误伤正常值：常规缩放仍要正常换算。
    /// </summary>
    /// <remarks>
    /// 这条是上一条的<b>反向对照</b> —— "结果必须有限"这条规则若写得太宽（例如误判 0 为非法），
    /// 整条管线就废了。
    /// </remarks>
    [Fact]
    public void FromView与ToView_常规缩放仍正常换算()
    {
        var viewport = new Viewport(10.0, 20.0, 2.0, 1.0);

        var doc = DocCoord.FromView(new ViewPoint(50.0, 60.0), viewport);
        Assert.Equal(new DocPoint(20.0, 20.0), doc);

        var view = DocCoord.ToView(new DocPoint(20.0, 20.0), viewport);
        Assert.Equal(new ViewPoint(50.0, 60.0), view);
    }

    // ═══════════════════ DocRect：四条边、中心、IsEmpty ═══════════════════

    /// <summary>
    /// 🔴 四条边的方向是铁律 1 的核心：<see cref="DocRect.Top"/> 是 Y <b>最小</b>那条边，
    /// <see cref="DocRect.Bottom"/> 是 Y <b>最大</b>那条边，本类型<b>不做任何 Y 翻转</b>。
    /// </summary>
    /// <remarks>
    /// 手算：<c>Origin=(1.5, 2.5)</c>、<c>Size=(4, 6)</c>
    /// <list type="bullet">
    /// <item>Left = 1.5、Top = 2.5（就是 Origin，不做任何变换）</item>
    /// <item>Right = 1.5 + 4 = 5.5、Bottom = 2.5 + 6 = 8.5</item>
    /// <item>Center = (1.5 + 4/2.0, 2.5 + 6/2.0) = (3.5, 5.5)</item>
    /// </list>
    /// 若实现里出现 <c>height - y</c> 或 <c>-y</c>，Bottom 会小于 Top，这里立刻失败。
    /// </remarks>
    [Fact]
    public void 四条边与中心按左上角加尺寸计算_不做Y翻转()
    {
        var rect = new DocRect(new DocPoint(1.5, 2.5), new DocSize(4, 6));

        Assert.Equal(1.5, rect.Left);
        Assert.Equal(2.5, rect.Top);
        Assert.Equal(5.5, rect.Right);
        Assert.Equal(8.5, rect.Bottom);
        Assert.Equal(3.5, rect.Center.X);
        Assert.Equal(5.5, rect.Center.Y);
        Assert.False(rect.IsEmpty);
    }

    /// <summary>
    /// <see cref="DocRect.IsEmpty"/> 必须把<b>零尺寸</b>和<b>负尺寸</b>一视同仁地判为空。
    /// </summary>
    /// <remarks>
    /// 只判 <c>== 0</c> 是不够的：默认 <c>DocSize</c> 是 (0,0)（空尺寸），
    /// 而负尺寸会来自"尺寸算错"的 bug。两者若都不算空，后续 <see cref="DocRect.Contains"/>、
    /// <see cref="DocRect.Integral"/> 会拿负半开区间去判点，命中逻辑全乱。
    /// <para>判定式是 <c>Width ≤ 0 || Height ≤ 0</c>，本测试逐个分量与两种符号组合。</para>
    /// </remarks>
    [Theory]
    [InlineData(0, 10, true)]
    [InlineData(10, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(-1, 10, true)]
    [InlineData(10, -1, true)]
    [InlineData(-1, -1, true)]
    [InlineData(1, 1, false)]
    [InlineData(10, 10, false)]
    public void IsEmpty_宽或高不大于0都算空(int width, int height, bool expectedEmpty)
    {
        var rect = new DocRect(new DocPoint(3.0, 4.0), new DocSize(width, height));

        Assert.Equal(expectedEmpty, rect.IsEmpty);
    }

    /// <summary>
    /// <see cref="DocRect.Empty"/> 必须真的是"原点与尺寸全零的空矩形"，
    /// 而不是某个被缓存的共享实例。
    /// </summary>
    /// <remarks>
    /// 多个契约成员（<see cref="DocRect.Union"/>、<see cref="DocRect.Intersect"/>、
    /// <see cref="DocRect.Integral"/>、<see cref="DocRect.Inflate"/>）都以"返回 Empty"表达
    /// "无结果"。若 Empty 不是 <c>default</c>，调用方就分不清"没有交集"和"交集恰好长这样"。
    /// </remarks>
    [Fact]
    public void Empty_是原点尺寸全零的空矩形()
    {
        var rect = DocRect.Empty;

        Assert.True(rect.IsEmpty);
        Assert.Equal(0.0, rect.Origin.X);
        Assert.Equal(0.0, rect.Origin.Y);
        Assert.Equal(0, rect.Size.Width);
        Assert.Equal(0, rect.Size.Height);
        Assert.Equal(default(DocRect), rect);
    }

    // ═══════════════════ DocRect.Contains：半开区间 ═══════════════════

    /// <summary>
    /// 🔴 <see cref="DocRect.Contains"/> 是<b>半开区间</b>：左边与上边<b>含</b>，右边与下边<b>不含</b>。
    /// </summary>
    /// <remarks>
    /// 判定式 <c>Left ≤ x &lt; Right</c>、<c>Top ≤ y &lt; Bottom</c>。用带小数的原点配整数尺寸
    /// <c>Origin=(1.25, 2.75), Size=(3, 4)</c>：<c>Right = 4.25</c>、<c>Bottom = 6.75</c>。
    /// <para>之所以是<b>小数原点 + 整数尺寸</b>：<c>DocSize</c> 按契约只能取整数量
    /// （像素尺寸无小数），而原点是小数——这样边界就是"非整数"，能真正测出
    /// 左/上边界上的点算内、右/下边界上的点算外，而整对齐的矩形测不出这个差别。</para>
    /// <para>半开区间是为了让相邻矩形无缝拼接时接缝列<b>只被覆盖一次</b>；改成闭区间就会出现
    /// 一列/一行被两层重复命中（叠加两次半透明 = 接缝发亮）。</para>
    /// </remarks>
    [Theory]
    [InlineData(1.25, 2.75, true)]     // 左上角点：左含、上含
    [InlineData(4.2499, 6.7499, true)] // 右下内侧（紧贴但仍在右/下边界之内）
    [InlineData(3.0, 5.0, true)]       // 正中
    [InlineData(4.25, 2.75, false)]    // 右边界：不含（1.25 + 宽 3 = 4.25）
    [InlineData(1.25, 6.75, false)]    // 下边界：不含（2.75 + 高 4 = 6.75）
    [InlineData(1.2499, 2.75, false)]  // 左边界外侧
    [InlineData(1.25, 2.7499, false)]  // 上边界外侧
    public void Contains_左与上含右与下不含(double x, double y, bool expected)
    {
        // DocSize 只能取整数（契约：像素尺寸为整数量），故取宽 3 / 高 4，
        // 使右边界 = 1.25 + 3 = 4.25、下边界 = 2.75 + 4 = 6.75，正好与上面两个边界用例重合。
        var rect = new DocRect(new DocPoint(1.25, 2.75), new DocSize(3, 4));

        Assert.Equal(expected, rect.Contains(new DocPoint(x, y)));
    }

    /// <summary>
    /// 半开区间在<b>拼接</b>上的实际后果：接缝列只能被右边那个矩形命中一次，绝不重复也不遗漏。
    /// </summary>
    /// <remarks>
    /// 左矩形 <c>[0,10) × [0,10)</c>、右矩形 <c>[10,20) × [0,10)</c>。
    /// 手算：x = 9.5 只在左里（左：9.5 ≥ 0 且 9.5 &lt; 10 ✓；右：9.5 ≥ 10 ✗）；
    /// x = 10 只在右里（左：10 &lt; 10 ✗；右：10 ≥ 10 ✓）。
    /// 若 Contains 改成闭区间，x = 10 会被两个矩形同时命中，拼接处就会有一列重复叠加。
    /// </remarks>
    [Fact]
    public void Contains_相邻矩形在接缝处不重复覆盖也不遗漏()
    {
        var left = new DocRect(new DocPoint(0.0, 0.0), new DocSize(10, 10));
        var right = new DocRect(new DocPoint(10.0, 0.0), new DocSize(10, 10));
        var seamJustLeft = new DocPoint(9.5, 5.0);
        var seamColumn = new DocPoint(10.0, 5.0);

        Assert.True(left.Contains(seamJustLeft));
        Assert.False(right.Contains(seamJustLeft));

        Assert.False(left.Contains(seamColumn));
        Assert.True(right.Contains(seamColumn));
    }

    /// <summary>
    /// 空矩形<b>一律</b>不命中任何点，连"恰好等于自己原点"的点也不命中。
    /// </summary>
    /// <remarks>
    /// <see cref="DocRect.Contains"/> 的实现是 <c>!IsEmpty &amp;&amp; ...</c>：
    /// 空矩形必须先被短路掉。若去掉短路，一个零尺寸矩形 <c>Origin=(0,0)</c> 会满足
    /// <c>0 ≥ 0 &amp;&amp; 0 &lt; 0</c> 的前半段，让"空区域里有内容"这种推理错误溜过去。
    /// </remarks>
    [Fact]
    public void Contains_空矩形一律返回false()
    {
        var empty = DocRect.Empty;
        var zeroWidth = new DocRect(new DocPoint(0.0, 0.0), new DocSize(0, 10));
        var negative = new DocRect(new DocPoint(0.0, 0.0), new DocSize(-5, 10));

        Assert.False(empty.Contains(new DocPoint(0.0, 0.0)));
        Assert.False(empty.Contains(new DocPoint(-1000.0, -1000.0)));
        Assert.False(zeroWidth.Contains(new DocPoint(0.0, 0.0)));
        Assert.False(negative.Contains(new DocPoint(0.0, 5.0)));
    }

    // ═══════════════════ DocRect.Intersect ═══════════════════

    /// <summary>
    /// 重叠矩形相交必须返回交集本身，位置与尺寸都要精确。
    /// </summary>
    /// <remarks>
    /// 手算：A = <c>[0,10) × [0,10)</c>、B = <c>[5,15) × [5,15)</c>
    /// <list type="bullet">
    /// <item>l = max(0,5) = 5、t = max(0,5) = 5</item>
    /// <item>r = min(10,15) = 10、b = min(10,15) = 10</item>
    /// <item>w = ceil(10) - floor(5) = 5、h = 10 - 5 = 5</item>
    /// </list>
    /// 期望 <c>Origin=(5,5), Size=(5,5)</c>。若实现误用 <c>-y</c> 之类的翻转，t/b 会互换。
    /// </remarks>
    [Fact]
    public void Intersect_重叠时返回交集矩形()
    {
        var a = new DocRect(new DocPoint(0.0, 0.0), new DocSize(10, 10));
        var b = new DocRect(new DocPoint(5.0, 5.0), new DocSize(10, 10));

        var intersection = a.Intersect(b);

        Assert.Equal(new DocPoint(5.0, 5.0), intersection.Origin);
        Assert.Equal(new DocSize(5, 5), intersection.Size);
        Assert.False(intersection.IsEmpty);
    }

    /// <summary>
    /// 🔴 完全不相交时必须返回 <see cref="DocRect.Empty"/>，不能返回一个负尺寸矩形。
    /// </summary>
    /// <remarks>
    /// 手算：A = <c>[0,10) × [0,10)</c>、B = <c>[20,30) × [20,30)</c>
    /// <list type="bullet">
    /// <item>l = max(0,20) = 20、r = min(10,30) = 10 → r ≤ l 成立 → 直接返回 Empty</item>
    /// </list>
    /// 返回负尺寸矩形会让 <c>Width = 10 - 20 = -10</c>，而"负尺寸"在别处被当成空矩形，
    /// 同一个事实会以两种形态出现在调用方，裁剪/合成路径因此会漏判。
    /// </remarks>
    [Fact]
    public void Intersect_完全不相交返回Empty()
    {
        var a = new DocRect(new DocPoint(0.0, 0.0), new DocSize(10, 10));
        var b = new DocRect(new DocPoint(20.0, 20.0), new DocSize(10, 10));

        var intersection = a.Intersect(b);

        Assert.True(intersection.IsEmpty);
        Assert.Equal(DocRect.Empty, intersection);
    }

    /// <summary>
    /// 🔴 半开区间下"贴边相邻"是<b>不相交</b>，交集必须为空。
    /// </summary>
    /// <remarks>
    /// A = <c>[0,10) × [0,10)</c>、B = <c>[10,20) × [0,10)</c>：l = max(0,10) = 10，r = min(10,20) = 10，
    /// <c>r ≤ l</c> 成立 → Empty。
    /// 这条与 <see cref="Contains"/> 的半开语义是同一件事的两面：如果这里错误地返回 1 列宽的交集，
    /// 拼接时接缝列就会被两个矩形同时裁到，等于多算一列像素。
    /// </remarks>
    [Fact]
    public void Intersect_贴边相邻不算相交返回Empty()
    {
        var a = new DocRect(new DocPoint(0.0, 0.0), new DocSize(10, 10));
        var b = new DocRect(new DocPoint(10.0, 0.0), new DocSize(10, 10));

        var intersection = a.Intersect(b);

        Assert.True(intersection.IsEmpty);
        Assert.Equal(DocRect.Empty, intersection);
    }

    /// <summary>
    /// 🔴 亚像素重叠必须用 Floor/Ceil <b>向外</b>取整，绝不能截断成 0 宽。
    /// </summary>
    /// <remarks>
    /// A = <c>[0.5, 4.5) × [0.5, 4.5)</c>、B = <c>[3.25, 7.25) × [3.25, 7.25)</c>
    /// <list type="bullet">
    /// <item>l = t = 3.25、r = b = 4.5</item>
    /// <item>l = floor(3.25) = 3、r = ceil(4.5) = 5 → w = 5 - 3 = 2</item>
    /// </list>
    /// 期望 <c>Origin=(3,3), Size=(2,2)</c>：它向左下扩了 0.25 像素，<b>完整盖住</b>真正的重叠区。
    /// 若改成截断，w = (int)4.5 - (int)3.25 = 4 - 3 = 1 尚可，但把 l 截断到 3、r 截断到 4 就会得到
    /// <c>[3,4)</c>，右边少掉 0.5 像素 —— 真正被两层覆盖的那部分像素反而丢了。
    /// </remarks>
    [Fact]
    public void Intersect_亚像素重叠向外取整_不丢任何被覆盖像素()
    {
        var a = new DocRect(new DocPoint(0.5, 0.5), new DocSize(4, 4));
        var b = new DocRect(new DocPoint(3.25, 3.25), new DocSize(4, 4));

        var intersection = a.Intersect(b);

        // floor(3.25)=3, ceil(4.5)=5, w=h=2
        Assert.Equal(new DocPoint(3.0, 3.0), intersection.Origin);
        Assert.Equal(new DocSize(2, 2), intersection.Size);

        // 真正的重叠区 [3.25, 4.5) × [3.25, 4.5) 必须被结果完全覆盖
        Assert.True(intersection.Left <= 3.25);
        Assert.True(intersection.Top <= 3.25);
        Assert.True(intersection.Right >= 4.5);
        Assert.True(intersection.Bottom >= 4.5);
    }

    /// <summary>
    /// 🔴 0.1 × 0.1 像素的重叠也必须留下 <b>1 × 1</b> 的结果，而不是被截断成空。
    /// </summary>
    /// <remarks>
    /// A = <c>[0,10) × [0,10)</c>、B = <c>[9.9, 19.9) × [9.9, 19.9)</c>：l = t = 9.9、r = b = 10，
    /// 交集宽高都是 0.1 像素。
    /// <list type="bullet">
    /// <item>w = ceil(10) - floor(9.9) = 10 - 9 = 1（若截断：(int)10 - (int)9.9 = 10 - 9 = 1，仍侥幸为 1）</item>
    /// <item>关键在于 l 被 floor 到 9 而不是截断到 9.9 后再参与运算</item>
    /// </list>
    /// 若用截断且把左边界也算成 (int)9.9 = 9，w 仍是 1；但把右边界算成 (int)10 = 10 时结果为 1，
    /// 恰好一样 —— 所以这条测试真正的价值在于锁住 <b>Floor/Ceil</b> 这两个算子的选择：
    /// 一旦换成"先各自截断再相减"，在重叠区小于 1 像素且不贴整像素边界时就会得到 0 宽矩形，
    /// 而 (0,0) 会被 <see cref="DocRect.IsEmpty"/> 判成空，语义从"有一点点重叠"塌成"完全不重叠"。
    /// </remarks>
    [Fact]
    public void Intersect_不足一个像素的重叠也至少保留一像素()
    {
        var a = new DocRect(new DocPoint(0.0, 0.0), new DocSize(10, 10));
        var b = new DocRect(new DocPoint(9.9, 9.9), new DocSize(10, 10));

        var intersection = a.Intersect(b);

        // floor(9.9)=9, ceil(10)=10, w=h=1
        Assert.Equal(new DocPoint(9.0, 9.0), intersection.Origin);
        Assert.Equal(new DocSize(1, 1), intersection.Size);
        Assert.False(intersection.IsEmpty);
    }

    /// <summary>
    /// <b>完全相同</b>的两个矩形相交，结果必须与自身逐字段相同。
    /// </summary>
    /// <remarks>
    /// A = <c>[2,6) × [3,8)</c>（Origin=(2,3)、Size=(4,5)）：
    /// l = 2、t = 3、r = 6、b = 8 → w = 6 - 2 = 4、h = 8 - 3 = 5。
    /// 这条锁住"相交不会把整数矩形挪动半像素"——它是蒙版合成可以复用输入矩形的前提。
    /// </remarks>
    [Fact]
    public void Intersect_与自身相交结果不变()
    {
        var rect = new DocRect(new DocPoint(2.0, 3.0), new DocSize(4, 5));

        var intersection = rect.Intersect(rect);

        Assert.Equal(rect, intersection);
        Assert.Equal(new DocPoint(2.0, 3.0), intersection.Origin);
        Assert.Equal(new DocSize(4, 5), intersection.Size);
    }

    /// <summary>
    /// <b>任一方为空</b>，交集必然为空 —— 空矩形与任何矩形都没有公共像素。
    /// </summary>
    /// <remarks>
    /// <c>Empty</c> 的 Right/Bottom 都是 0。
    /// 左空右不空：l = max(0,5) = 5、r = min(0,15) = 0 → r ≤ l → Empty；
    /// 左不空右空：l = max(0,0) = 0、r = min(10,0) = 0 → r ≤ l → Empty；
    /// 双方皆空同理。
    /// <para>必须显式覆盖"一方为空"而不是只覆盖"双方为空"，因为实现里对空矩形有专门的早退分支，
    /// 两个方向都得验。</para>
    /// </remarks>
    [Fact]
    public void Intersect_任一方为空则结果为空()
    {
        var normal = new DocRect(new DocPoint(5.0, 5.0), new DocSize(10, 10));

        Assert.True(DocRect.Empty.Intersect(normal).IsEmpty);
        Assert.True(normal.Intersect(DocRect.Empty).IsEmpty);
        Assert.True(DocRect.Empty.Intersect(DocRect.Empty).IsEmpty);
    }

    // ═══════════════════ DocRect.Union ═══════════════════

    /// <summary>
    /// 任一方为空时，并集必须<b>原样返回另一方</b>，不多不少。
    /// </summary>
    /// <remarks>
    /// 这条决定了调用方能不能用"空矩形当单位元"来做增量合并：
    /// <c>Union</c> 必须严格满足 <c>Empty ∪ B == B</c> 且 <c>A ∪ Empty == A</c>。
    /// 若实现改成"先取包围盒再取整"，空的那一方会引入 0 值边界，
    /// 而 <c>Floor/Ceil</c> 之后空的那一侧可能把结果<b>撑大</b>（例如 B = <c>[3.5, 4.0)</c>
    /// 会被撑成 <c>[3,5)</c>），从而凭空多出一圈本不该被选中的像素。
    /// </remarks>
    [Fact]
    public void Union_任一方为空返回另一方()
    {
        var normal = new DocRect(new DocPoint(5.0, 5.0), new DocSize(10, 10));

        Assert.Equal(normal, DocRect.Empty.Union(normal));
        Assert.Equal(normal, normal.Union(DocRect.Empty));
        Assert.True(DocRect.Empty.Union(DocRect.Empty).IsEmpty);
    }

    /// <summary>
    /// 部分重叠的两矩形合并，必须取最小外接矩形。
    /// </summary>
    /// <remarks>
    /// A = <c>[0,10) × [0,10)</c>、B = <c>[5,15) × [5,15)</c>
    /// <list type="bullet">
    /// <item>l = min(0,5) = 0、t = min(0,5) = 0</item>
    /// <item>r = max(10,15) = 15、b = max(10,15) = 15</item>
    /// <item>w = ceil(15) - floor(0) = 15、h = 15</item>
    /// </list>
    /// 期望 <c>Origin=(0,0), Size=(15,15)</c>。若把 t/b 弄反（Y 翻转），结果会变成
    /// <c>Origin=(0,5), Size=(15,10)</c> —— 少掉上半部分。
    /// </remarks>
    [Fact]
    public void Union_部分重叠取最小外接矩形()
    {
        var a = new DocRect(new DocPoint(0.0, 0.0), new DocSize(10, 10));
        var b = new DocRect(new DocPoint(5.0, 5.0), new DocSize(10, 10));

        var union = a.Union(b);

        Assert.Equal(new DocPoint(0.0, 0.0), union.Origin);
        Assert.Equal(new DocSize(15, 15), union.Size);
    }

    /// <summary>
    /// 🔴 相邻但不重叠的两个矩形，合并结果必须<b>无缝无洞</b>。
    /// </summary>
    /// <remarks>
    /// A = <c>[0,10) × [0,10)</c>、B = <c>[10,15) × [0,10)</c>
    /// <list type="bullet">
    /// <item>l = 0、t = 0、r = max(10,15) = 15、b = 10</item>
    /// <item>w = ceil(15) - floor(0) = 15、h = 10 - 0 = 10</item>
    /// </list>
    /// 期望 <c>Origin=(0,0), Size=(15,10)</c> —— 宽度是 10 + 5 而不是 9 + 5，
    /// 中间不出现被两者都漏掉的缝隙列。分块渲染按并集裁剪时，缝隙会让那一列没有内容却仍被合成。
    /// </remarks>
    [Fact]
    public void Union_相邻不重叠时无缝合并()
    {
        var left = new DocRect(new DocPoint(0.0, 0.0), new DocSize(10, 10));
        var right = new DocRect(new DocPoint(10.0, 0.0), new DocSize(5, 10));

        var union = left.Union(right);

        Assert.Equal(new DocPoint(0.0, 0.0), union.Origin);
        Assert.Equal(new DocSize(15, 10), union.Size);

        // 接缝两侧都必须被并集覆盖：中间不存在"谁都不含"的列
        Assert.True(union.Contains(new DocPoint(9.5, 5.0)));
        Assert.True(union.Contains(new DocPoint(10.0, 5.0)));
    }

    /// <summary>
    /// 亚像素矩形合并时同样用 Floor/Ceil <b>向外</b>取整，宁可多选不可漏选。
    /// </summary>
    /// <remarks>
    /// A = <c>[0.25, 3.25) × [0.25, 3.25)</c>（<c>0.25 + 3 = 3.25</c>，二进制精确）、
    /// B = <c>[4, 6) × [4, 6)</c>
    /// <list type="bullet">
    /// <item>l = min(0.25,4) = 0.25 → floor = 0</item>
    /// <item>t = min(0.25,4) = 0.25 → floor = 0</item>
    /// <item>r = max(3.25,6) = 6 → ceil = 6</item>
    /// <item>b = max(3.25,6) = 6 → 6</item>
    /// <item>w = 6 - 0 = 6、h = 6</item>
    /// </list>
    /// 期望 <c>Origin=(0,0), Size=(6,6)</c>。左上角向左上各扩了 0.25 像素，代价是多算一点，
    /// 换来"绝不含糊"——多选的像素会被上层裁剪，不会丢内容。
    /// </remarks>
    [Fact]
    public void Union_亚像素边界向外取整()
    {
        var a = new DocRect(new DocPoint(0.25, 0.25), new DocSize(3, 3));   // Right = Bottom = 3.25
        var b = new DocRect(new DocPoint(4.0, 4.0), new DocSize(2, 2));      // Right = Bottom = 6

        var union = a.Union(b);

        // floor(0.25)=0, ceil(6)=6 → 6×6
        Assert.Equal(new DocPoint(0.0, 0.0), union.Origin);
        Assert.Equal(new DocSize(6, 6), union.Size);
    }

    // ═══════════════════ DocRect.Integral ═══════════════════

    /// <summary>
    /// 🔴 <see cref="DocRect.Integral"/> 必须<b>向外</b>取整：Floor 左上、Ceil 右下。
    /// </summary>
    /// <remarks>
    /// 契约算例：<c>Origin=(0.3, 0.7)</c>、<c>Size=(2.1, 1.2)</c> → 期望 <c>Origin=(0,0), Size=(3,2)</c>。
    /// <list type="number">
    /// <item>Left = 0.3 → floor(0.3) = <b>0</b></item>
    /// <item>Top = 0.7 → floor(0.7) = <b>0</b></item>
    /// <item>Right = 0.3 + 2.1 ≈ 2.4 → ceil(2.4) = <b>3</b></item>
    /// <item>Bottom = 0.7 + 1.2 ≈ 1.9 → ceil(1.9) = <b>2</b></item>
    /// <item>w = 3 - 0 = <b>3</b>、h = 2 - 0 = <b>2</b></item>
    /// </list>
    /// 期望 <c>Origin=(0,0), Size=(3,2)</c>。
    /// <para><b>为什么必须向外</b>：重采样与蒙版合成都以像素为栅格。向内取整（右下 floor、左上 ceil）
    /// 会把边缘那 0.4 × 0.2 像素直接<b>裁掉</b>，表现为图层边缘发虚、少一圈不透明像素。
    /// 这条测试是"宁多勿少"的回归锁。</para>
    /// <para><b>本用例如何表达那个带小数的尺寸</b>：<see cref="DocSize"/> 的宽高是 <see cref="int"/>，
    /// 存不下 2.1 / 1.2，因此改用<b>最接近且给出同一组整数结果</b>的 <c>Size=(2,1)</c>：
    /// 四条边为 0.3 / 0.7 / 2.3 / 1.7 → floor/ceil 后同样是 0 / 0 / 3 / 2 → <c>Size=(3,2)</c>。
    /// 小数全部保留在原点上，覆盖的正是"Floor 左上、Ceil 右下"这两个方向。</para>
    /// <para><b>浮点说明</b>：本用例实际用的 0.3 + 2 = 2.3 与 0.7 + 1 = 1.7 都远离整数边界，
    /// 即便 0.3 在 IEEE754 下是 0.299999999999999988…，ceil(2.3) 与 ceil(1.7) 仍稳定为 3 与 2，
    /// 期望值不受舍入误差影响。</para>
    /// </remarks>
    [Fact]
    public void Integral_契约算例_小数原点向外取整为整数矩形()
    {
        var rect = new DocRect(new DocPoint(0.3, 0.7), new DocSize(2, 1));

        // Left=0.3→floor→0, Top=0.7→floor→0, Right=0.3+2=2.3→ceil→3, Bottom=0.7+1=1.7→ceil→2
        var integral = rect.Integral;

        Assert.Equal(new DocPoint(0.0, 0.0), integral.Origin);
        Assert.Equal(new DocSize(3, 2), integral.Size);
    }

    /// <summary>
    /// 同一条规则在<b>更大的小数矩形</b>上再验一次，确认 Ceil 作用在右下而不是左上。
    /// </summary>
    /// <remarks>
    /// 手算，<c>Origin=(0.3, 0.7)</c>、<c>Size=(3, 2)</c>：四条边为 0.3 / 0.7 / 3.3 / 2.7
    /// <list type="bullet">
    /// <item>floor(0.3) = 0、floor(0.7) = 0</item>
    /// <item>ceil(3.3) = 4、ceil(2.7) = 3 → w = 4 - 0 = 4、h = 3 - 0 = 3</item>
    /// </list>
    /// 期望 <c>Origin=(0,0), Size=(4,3)</c>。
    /// <para>若实现误用<b>向内</b>取整，会得到 floor(3.3) = 3、floor(2.7) = 2 → <c>Size=(3,2)</c>，
    /// 正好比期望少一格，边缘像素被裁掉 —— 这正是这条用例要挡的回归。</para>
    /// </remarks>
    [Fact]
    public void Integral_更大矩形同样向四个角扩张()
    {
        var rect = new DocRect(new DocPoint(0.3, 0.7), new DocSize(3, 2));

        // Left=0.3→0, Top=0.7→0, Right=0.3+3=3.3→4, Bottom=0.7+2=2.7→3
        var integral = rect.Integral;

        Assert.Equal(new DocPoint(0.0, 0.0), integral.Origin);
        Assert.Equal(new DocSize(4, 3), integral.Size);
    }

    /// <summary>
    /// 向外取整后的矩形必须<b>完全包住</b>原矩形，且原矩形里的点仍在其中。
    /// </summary>
    /// <remarks>
    /// 这条是把"向外"写成可判定的性质，避免只在某一个具体数字上碰巧正确：
    /// <c>floor(x) ≤ x</c> 且 <c>ceil(x) ≥ x</c> 恒成立，所以取整结果必然满足
    /// <c>integral.Left ≤ rect.Left</c>、<c>integral.Right ≥ rect.Right</c>（Y 同理）。
    /// <para>再补一条更强的：原矩形<b>左上角这个点</b>必须落在取整结果里
    /// （<see cref="DocRect.Contains"/> 是半开区间，但原点 X 严格小于取整后的右边界），
    /// 保证"被覆盖的像素一个都没丢"。</para>
    /// </remarks>
    [Theory]
    [InlineData(0.3, 0.7, 2, 1)]
    [InlineData(-1.7, -2.3, 3, 2)]
    [InlineData(10.5, 20.5, 1, 1)]
    public void Integral_向外取整必定完全包住原矩形(double originX, double originY, int width, int height)
    {
        var rect = new DocRect(new DocPoint(originX, originY), new DocSize(width, height));

        var integral = rect.Integral;

        Assert.True(integral.Left <= rect.Left, "左边界必须向左（或原地）扩张");
        Assert.True(integral.Top <= rect.Top, "上边界必须向上（或原地）扩张");
        Assert.True(integral.Right >= rect.Right, "右边界必须向右（或原地）扩张");
        Assert.True(integral.Bottom >= rect.Bottom, "下边界必须向下（或原地）扩张");
        Assert.True(integral.Contains(rect.Origin), "原矩形左上角必须仍在取整结果内");
    }

    /// <summary>
    /// 🔴 空矩形（零宽、零高、负宽、负高）取整后<b>仍然</b>是空，不能凭空长出像素。
    /// </summary>
    /// <remarks>
    /// <see cref="DocRect.Integral"/> 里对 <see cref="DocRect.IsEmpty"/> 有早退，
    /// 但还留了 <c>w ≤ 0 || h ≤ 0</c> 的兜底。必须确认兜底不会反过来把非空矩形误伤，
    /// 也不能让空矩形在取整中"复活"成 1×1 —— 那会让本该为空的蒙版多覆盖一格。
    /// </remarks>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    [InlineData(10, -5)]
    public void Integral_空矩形取整后仍为空(int width, int height)
    {
        var rect = new DocRect(new DocPoint(7.0, 9.0), new DocSize(width, height));

        var integral = rect.Integral;

        Assert.True(integral.IsEmpty);
        Assert.Equal(DocRect.Empty, integral);
    }

    /// <summary>
    /// 整数像素对齐的矩形取整后必须<b>原地不动</b>。
    /// </summary>
    /// <remarks>
    /// 手算：<c>Origin=(2,3)</c>、<c>Size=(4,5)</c> → <c>[2,6) × [3,8)</c>；
    /// floor(2)=2、floor(3)=3、ceil(6)=6、ceil(8)=8 → <c>Size=(4,5)</c>，与原矩形逐字段相同。
    /// 这条保证"已经对齐的图层不会被取整步骤无谓地放大一圈"（否则每次重采样都会累积像素膨胀）。
    /// </remarks>
    [Fact]
    public void Integral_整数对齐矩形保持不变()
    {
        var rect = new DocRect(new DocPoint(2.0, 3.0), new DocSize(4, 5));

        var integral = rect.Integral;

        Assert.Equal(rect, integral);
        Assert.Equal(new DocPoint(2.0, 3.0), integral.Origin);
        Assert.Equal(new DocSize(4, 5), integral.Size);
    }

    // ═══════════════════ DocRect.Inflate ═══════════════════

    /// <summary>
    /// 正数扩张必须对<b>四条边同时</b>生效，宽高各增加 <c>2d</c>。
    /// </summary>
    /// <remarks>
    /// 手算：<c>Origin=(10,20)</c>、<c>Size=(10,10)</c>，即 <c>[10,20) × [20,30)</c>，<c>d = 5</c>
    /// <list type="bullet">
    /// <item>l = 10 - 5 = 5、t = 20 - 5 = 15</item>
    /// <item>r = 20 + 5 = 25 → w = 25 - 5 = 20</item>
    /// <item>b = 30 + 5 = 35 → h = 35 - 15 = 20</item>
    /// </list>
    /// 期望 <c>Origin=(5,15), Size=(20,20)</c>：宽从 10 变 20（每边 +5，共 +10）✓。
    /// 若只扩了两条边，宽会变成 15，这个断言会失败。
    /// </remarks>
    [Fact]
    public void Inflate_正数向四边扩张()
    {
        var rect = new DocRect(new DocPoint(10.0, 20.0), new DocSize(10, 10));

        var inflated = rect.Inflate(5.0);

        Assert.Equal(new DocPoint(5.0, 15.0), inflated.Origin);
        Assert.Equal(new DocSize(20, 20), inflated.Size);
    }

    /// <summary>
    /// 负值必须等价于向内收缩：四边内缩 <c>|d|</c>，宽高各减少 <c>2|d|</c>。
    /// </summary>
    /// <remarks>
    /// 手算：<c>[10,20) × [20,30)</c>，<c>d = -2</c>
    /// <list type="bullet">
    /// <item>l = 10 - (-2) = 12、t = 20 - (-2) = 22</item>
    /// <item>r = 20 + (-2) = 18 → w = 18 - 12 = 6</item>
    /// <item>b = 30 + (-2) = 28 → h = 28 - 22 = 6</item>
    /// </list>
    /// 期望 <c>Origin=(12,22), Size=(6,6)</c>。收缩被用来做"安全边距"，所以收缩量必须精确，
    /// 否则羽化蒙版的实际宽度会随矩形大小漂移。
    /// </remarks>
    [Fact]
    public void Inflate_负数向内收缩()
    {
        var rect = new DocRect(new DocPoint(10.0, 20.0), new DocSize(10, 10));

        var shrunk = rect.Inflate(-2.0);

        Assert.Equal(new DocPoint(12.0, 22.0), shrunk.Origin);
        Assert.Equal(new DocSize(6, 6), shrunk.Size);
    }

    /// <summary>
    /// 🔴 收缩到<b>零宽</b>与收缩到<b>负宽</b>，都必须返回 <see cref="DocRect.Empty"/>。
    /// </summary>
    /// <remarks>
    /// 手算，<c>[10,20) × [20,30)</c>：
    /// <list type="bullet">
    /// <item><c>d = -5</c>：l = 15、t = 25、r = 15 → w = 15 - 15 = <b>0</b> → Empty</item>
    /// <item><c>d = -6</c>：l = 16、t = 26、r = 14 → w = 14 - 16 = <b>-2</b> → Empty</item>
    /// </list>
    /// 两条路径（<c>w == 0</c> 与 <c>w &lt; 0</c>）都要验：只挡负值会漏掉"恰好收到 0"，
    /// 那会留下一个 0×6 的"空但非空"矩形，调用方按非空处理后渲染出一张 0 宽的图层。
    /// </remarks>
    [Fact]
    public void Inflate_收缩到零或负尺寸都返回Empty()
    {
        var rect = new DocRect(new DocPoint(10.0, 20.0), new DocSize(10, 10));

        var zero = rect.Inflate(-5.0);
        Assert.True(zero.IsEmpty);
        Assert.Equal(DocRect.Empty, zero);

        var negative = rect.Inflate(-6.0);
        Assert.True(negative.IsEmpty);
        Assert.Equal(DocRect.Empty, negative);
    }

    /// <summary>
    /// 空矩形扩张后<b>仍然是空</b>：空是吸收态，不能被"救活"。
    /// </summary>
    /// <remarks>
    /// 若实现漏掉 <c>IsEmpty</c> 早退，空矩形会被当成 <c>[0,0) × [0,0)</c> 参与运算：
    /// <c>d = 5</c> 时 l = -5、r = 5，w = 10 —— 一个本该为空的蒙版会突然覆盖 10×10 像素，
    /// 而且不会有任何异常，只表现为"某处多了一块内容"。
    /// </remarks>
    [Fact]
    public void Inflate_空矩形扩张后仍为空()
    {
        var inflated = DocRect.Empty.Inflate(5.0);

        Assert.True(inflated.IsEmpty);
        Assert.Equal(DocRect.Empty, inflated);
    }

    /// <summary>
    /// 🔴 分数扩张量必须<b>向外取整</b>，否则"扩张 0.5 像素"等于什么都没做。
    /// </summary>
    /// <remarks>
    /// 手算 <c>[0,4) × [0,4)</c>、<c>d = 0.5</c>，<see cref="DocRect.Inflate"/> 用 <c>Floor</c>/<c>Ceil</c>：
    /// <list type="bullet">
    /// <item>l = <c>Floor(0 - 0.5)</c> = <c>Floor(-0.5)</c> = <b>-1</b></item>
    /// <item>r = <c>Ceil(4 + 0.5)</c> = <c>Ceil(4.5)</c> = <b>5</b></item>
    /// <item>w = 5 - (-1) = <b>6</b> → <c>[-1,5) × [-1,5)</c></item>
    /// </list>
    /// <para><b>为什么不能向零截断</b>：若用 <c>(int)v</c>，l = <c>(int)(-0.5)</c> = <b>0</b>、
    /// r = <c>(int)(4.5)</c> = <b>4</b>，w = 4 —— 矩形原封不动，<b>扩张静默失效</b>，
    /// 而且不抛任何异常。这与 <see cref="DocRect.Integral"/> 刻意使用 Floor/Ceil 的口径也不一致。</para>
    /// </remarks>
    [Fact]
    public void Inflate_分数扩张量按向外取整真正扩张()
    {
        var rect = new DocRect(new DocPoint(0.0, 0.0), new DocSize(4, 4));

        var inflated = rect.Inflate(0.5);

        Assert.Equal(new DocPoint(-1.0, -1.0), inflated.Origin);
        Assert.Equal(new DocSize(6, 6), inflated.Size);
    }

    /// <summary>
    /// 🔴 <b>反向对照</b>：负的分数收缩量按向外取整后<b>不该改变任何像素的归属</b>。
    /// </summary>
    /// <remarks>
    /// 手算 <c>[0,4) × [0,4)</c>、<c>d = -0.5</c>：
    /// <c>l = Floor(0 - (-0.5)) = Floor(0.5) = 0</c>、<c>r = Ceil(4 + (-0.5)) = Ceil(3.5) = 4</c>
    /// → <c>w = 4</c>，矩形不变。
    /// <para>这与上一条并不矛盾：<b>向外取整永远不裁像素</b>。
    /// 收缩不足一整像素时不裁，收缩超过一整像素时才减少覆盖。</para>
    /// </remarks>
    [Fact]
    public void Inflate_分数收缩量按向外取整不丢像素()
    {
        var rect = new DocRect(new DocPoint(0.0, 0.0), new DocSize(4, 4));

        Assert.Equal(rect, rect.Inflate(-0.5));

        // 收缩超过一整像素才真的减少覆盖：d = -1.5 → l = Floor(1.5) = 1，r = Ceil(2.5) = 3，w = 2
        var shrunk = rect.Inflate(-1.5);
        Assert.Equal(new DocPoint(1.0, 1.0), shrunk.Origin);
        Assert.Equal(new DocSize(2, 2), shrunk.Size);
    }

    // ═══════════════════ LayerTransform ═══════════════════

    /// <summary>
    /// <see cref="LayerTransform.Center"/> = <c>Origin + Size / 2</c>，
    /// 且 <c>int</c> 尺寸是<b>先转 double 再除</b>（除以 2.0，不是整数除法）。
    /// </summary>
    /// <remarks>
    /// 手算：<c>Origin=(10,20)</c>、<c>Size=(4,6)</c>
    /// <list type="bullet">
    /// <item>X = 10 + 4 / 2.0 = 10 + 2.0 = <b>12</b></item>
    /// <item>Y = 20 + 6 / 2.0 = 20 + 3.0 = <b>23</b></item>
    /// </list>
    /// <para>若写成 <c>Size.Width / 2</c>（整数除法），奇数尺寸的中心会向左上偏半个像素，
    /// 旋转与命中测试会整体漂移且很难察觉 —— 所以下一条专门用奇数尺寸锁住这个区别。</para>
    /// </remarks>
    [Fact]
    public void Center_等于原点加尺寸的一半()
    {
        var transform = new LayerTransform
        {
            Origin = new DocPoint(10.0, 20.0),
            Size = new DocSize(4, 6),
        };

        var center = transform.Center;

        Assert.Equal(12.0, center.X);
        Assert.Equal(23.0, center.Y);
    }

    /// <summary>
    /// 🔴 奇数尺寸的中心必须落在<b>半像素</b>上，证明除法是浮点除法而非整数除法。
    /// </summary>
    /// <remarks>
    /// 手算：<c>Origin=(10,20)</c>、<c>Size=(3,5)</c>
    /// <list type="bullet">
    /// <item>X = 10 + 3 / 2.0 = 10 + 1.5 = <b>11.5</b></item>
    /// <item>Y = 20 + 5 / 2.0 = 20 + 2.5 = <b>22.5</b></item>
    /// </list>
    /// 整数除法会给出 (11, 22)，与真值差半个像素：连续缩放/旋转后误差会逐层累积成可见的错位。
    /// </remarks>
    [Fact]
    public void Center_奇数尺寸落在半像素上_证明是浮点除法()
    {
        var transform = new LayerTransform
        {
            Origin = new DocPoint(10.0, 20.0),
            Size = new DocSize(3, 5),
        };

        var center = transform.Center;

        Assert.Equal(11.5, center.X);
        Assert.Equal(22.5, center.Y);
    }

    /// <summary>
    /// 🔴 <see cref="DocRect.Center"/> 与 <see cref="LayerTransform.Center"/> 必须用<b>同一个公式</b>，
    /// 否则图层矩形与图层变换会在中心点上不一致。
    /// </summary>
    /// <remarks>
    /// 两处源码都写的是 <c>Origin.X + Size.Width / 2.0</c>。凡是"图层矩形选区"和"图层变换"
    /// 分开算中心的地方，中心不一致会让旋转/翻转围绕错误轴发生，肉眼看是"转着转着偏了"。
    /// 这里用同一组数据同时驱动两个类型，锁住公式一致。
    /// </remarks>
    [Fact]
    public void Center_DocRect与LayerTransform公式一致()
    {
        var origin = new DocPoint(7.0, 13.0);
        var size = new DocSize(5, 9);
        var rect = new DocRect(origin, size);
        var transform = new LayerTransform { Origin = origin, Size = size };

        // 7 + 5/2.0 = 9.5；13 + 9/2.0 = 17.5
        Assert.Equal(rect.Center, transform.Center);
        Assert.Equal(9.5, rect.Center.X);
        Assert.Equal(17.5, rect.Center.Y);
    }

    /// <summary>
    /// <see cref="LayerTransform.Radians"/>：0 度与整圈都必须精确等于 0。
    /// </summary>
    /// <remarks>
    /// 手算：<c>d % 360</c> 在 0、360、-360 上都为 0，再乘 π 除以 180 仍是 0（精确值，非近似）。
    /// 这条同时钉住"归一化用 <c>% 360</c>"：若改成 <c>Math.IEEERemainder</c> 或别的算法，
    /// 整圈会变成 ±2π 的小数残量，累积成可见的旋转漂移。
    /// </remarks>
    [Fact]
    public void Radians_零度与整圈都精确等于零()
    {
        Assert.Equal(0.0, new LayerTransform { RotationDegrees = 0.0 }.Radians);
        Assert.Equal(0.0, new LayerTransform { RotationDegrees = 360.0 }.Radians);
        Assert.Equal(0.0, new LayerTransform { RotationDegrees = -360.0 }.Radians);
    }

    /// <summary>
    /// 🔴 <b>负角度用 C# 的余数语义</b>：<c>-90 % 360 == -90</c>，所以弧度是 <b>负的</b> −π/2。
    /// </summary>
    /// <remarks>
    /// C# 的 <c>%</c> 是<b>取余</b>（结果与左操作数同号），不是数学取模：
    /// <list type="bullet">
    /// <item><c>-90 % 360 = -90</c> → <c>-90 * π / 180 = -π/2 ≈ -1.5707963</c></item>
    /// <item><c>-180 % 360 = -180</c> → <c>-π ≈ -3.1415927</c></item>
    /// <item><c>-450 % 360 = -450 - (-1 × 360) = -90</c>（商 -1.25 向零截断为 -1）→ <c>-π/2</c></item>
    /// </list>
    /// <para><b>为什么必须钉死符号</b>：若哪天有人"顺手规范化"成 <c>((d % 360) + 360) % 360</c>
    /// （很多语言/库里是这么写的），<c>-90</c> 会变成 <c>270</c>，弧度从 −π/2 变成 +3π/2 ——
    /// 同一个负角度图层会被渲染成<b>几乎转了一圈的另一个方向</b>，而所有测试依然通过。
    /// 本文件用"断言为负 + 断言不等于 +3π/2"两重来防住它。</para>
    /// </remarks>
    [Fact]
    // ⚠️ 方法名<b>不能带 "#"</b>：C# 的 # 不是合法标识符字符，词法器会把 `C#余数` 里的
    // `#余数` 当成预处理指令开头，直接报 CS1040 并级联出 20+ 条假错误。
    // 故写作 `CSharp`。命名沿用同一铁律：宁可多打两个字，也不要把编译错误卷进来。
    public void Radians_负角度按CSharp余数语义得到负弧度_不归一化到正区间()
    {
        var minus90 = new LayerTransform { RotationDegrees = -90.0 };
        var minus180 = new LayerTransform { RotationDegrees = -180.0 };
        var minus450 = new LayerTransform { RotationDegrees = -450.0 };

        // -90 % 360 == -90 → -π/2
        Assert.Equal(-Math.PI / 2.0, minus90.Radians, 12);
        // -180 % 360 == -180 → -π
        Assert.Equal(-Math.PI, minus180.Radians, 12);
        // -450 % 360 == -90 → -π/2
        Assert.Equal(-Math.PI / 2.0, minus450.Radians, 12);

        // 反向对照：若被"规范化到 [0,360)"，-90 会得到 270° = +3π/2
        Assert.NotEqual(Math.PI * 1.5, minus90.Radians, 12);
        Assert.NotEqual(Math.PI * 1.5, minus450.Radians, 12);
    }

    /// <summary>
    /// 🔴 超过一圈的角度必须先按 <c>% 360</c> 归一化再转弧度。
    /// </summary>
    /// <remarks>
    /// 手算：<c>90 % 360 = 90</c>、<c>450 % 360 = 90</c>、<c>810 % 360 = 90</c>，
    /// 三者都等于 90°，弧度都是 π/2 ≈ 1.5707963267948966。
    /// <para>契约书里 <c>.comp</c> 的旋转角是累积写回的，图层被反复旋转后会很容易超过 ±180；
    /// 若实现忘了归一化，<c>450°</c> 会被当成 <c>450π/180 = 2.5π</c> 送进渲染矩阵，
    /// 图层会转到一个完全错误的角度（等效 -270°）。</para>
    /// </remarks>
    [Fact]
    public void Radians_超过一圈的角度先归一化()
    {
        Assert.Equal(Math.PI / 2.0, new LayerTransform { RotationDegrees = 90.0 }.Radians, 12);
        Assert.Equal(Math.PI / 2.0, new LayerTransform { RotationDegrees = 450.0 }.Radians, 12);
        Assert.Equal(Math.PI / 2.0, new LayerTransform { RotationDegrees = 810.0 }.Radians, 12);

        // 顺带锁住 180° / 270° 这两个在 UI 上会被直接拖出来的值
        Assert.Equal(Math.PI, new LayerTransform { RotationDegrees = 180.0 }.Radians, 12);
        Assert.Equal(Math.PI * 1.5, new LayerTransform { RotationDegrees = 270.0 }.Radians, 12);
    }

    /// <summary>
    /// 🔴 <see cref="LayerTransform.IsValid"/>：分量必须有限，且原点不能超出 ±1,000,000。
    /// </summary>
    /// <remarks>
    /// <para><b>有限性</b>：NaN / ±Infinity 会污染渲染矩阵且不抛异常，必须判为无效。
    /// <c>Math.Abs(NaN) &lt;= 1e6</c> 本身是 false，所以 NaN 两道关卡都能拦下。</para>
    /// <para><b>原点范围</b>：判定式是 <c>Math.Abs(x) ≤ 1_000_000</c>，是<b>闭区间</b> ——
    /// 恰好 1,000,000 合法，1,000,000.5 就已越界。这条边界必须精确锁住，
    /// 因为越界后的浮点在 <see cref="LayerTransform.Center"/> 里会彻底丢失精度，
    /// 表现为图层"跳到很远的地方"，且没有任何异常。</para>
    /// </remarks>
    [Theory]
    [InlineData(0.0, 0.0, 0.0, true)]
    [InlineData(100.0, 200.0, 45.0, true)]
    [InlineData(-500.0, -500.0, -45.0, true)]
    [InlineData(1_000_000.0, 1_000_000.0, 0.0, true)]     // 闭区间上界，恰好合法
    [InlineData(-1_000_000.0, 0.0, 0.0, true)]            // 闭区间下界，恰好合法
    [InlineData(1_000_000.5, 0.0, 0.0, false)]            // 刚刚越界
    [InlineData(0.0, -1_000_001.0, 0.0, false)]
    [InlineData(-1_000_001.0, 0.0, 0.0, false)]
    [InlineData(double.NaN, 0.0, 0.0, false)]
    [InlineData(0.0, double.NaN, 0.0, false)]
    [InlineData(0.0, 0.0, double.NaN, false)]
    [InlineData(double.PositiveInfinity, 0.0, 0.0, false)]
    [InlineData(0.0, double.NegativeInfinity, 0.0, false)]
    [InlineData(0.0, 0.0, double.PositiveInfinity, false)]
    [InlineData(0.0, 0.0, double.NegativeInfinity, false)]
    [InlineData(double.NaN, double.NaN, double.NaN, false)]
    public void IsValid_分量必须有限且原点不越界(
        double originX, double originY, double rotationDegrees, bool expected)
    {
        var transform = new LayerTransform
        {
            Origin = new DocPoint(originX, originY),
            Size = new DocSize(4, 4),
            RotationDegrees = rotationDegrees,
        };

        Assert.Equal(expected, transform.IsValid);
    }

    /// <summary>
    /// ⚠️ <b>现状记录（不代表认可）</b>：<see cref="LayerTransform.IsValid"/> 完全<b>不校验尺寸的符号</b>，
    /// 零尺寸与负尺寸都被判为"有效"。
    /// </summary>
    /// <remarks>
    /// <see cref="LayerTransform.IsValid"/> 里虽然写了 <c>double.IsFinite(Size.Width)</c>，
    /// 但 <see cref="DocSize"/> 的宽高是 <see cref="int"/>，<c>int → double</c> 永远有限，
    /// 因此这两项检查<b>恒真、是死代码</b>；尺寸的正负则根本没被检查。
    /// <para>手算：<c>Size = (0, 0)</c> → 有限 ✓、原点 (0,0) 在范围内 ✓ → IsValid 为 true；
    /// <c>Size = (int.MaxValue, int.MinValue)</c> 同理为 true，尽管一个荒谬地大、一个为负。</para>
    /// <para>这与 <see cref="DocSize"/> 自己文档里"不变量：Width &gt; 0 且 Height &gt; 0"并不矛盾 ——
    /// 那条明确声明"约定而非强制"。但一旦调用方把 <c>IsValid</c> 当成"可以放心渲染"的唯一依据，
    /// 负尺寸图层就会进入渲染管线。本测试把现状钉住，供后续决定是补尺寸校验还是改文档。</para>
    /// </remarks>
    [Fact]
    public void IsValid_不校验尺寸符号_零尺寸与负尺寸都算有效_现状记录()
    {
        var zeroSize = new LayerTransform { Origin = new DocPoint(0.0, 0.0), Size = new DocSize(0, 0) };
        var negativeSize = new LayerTransform
        {
            Origin = new DocPoint(0.0, 0.0),
            Size = new DocSize(-10, 10),
        };
        var extremeSize = new LayerTransform
        {
            Origin = new DocPoint(0.0, 0.0),
            Size = new DocSize(int.MaxValue, int.MinValue),
        };

        Assert.True(zeroSize.IsValid);
        Assert.True(negativeSize.IsValid);
        Assert.True(extremeSize.IsValid);
    }

    /// <summary>
    /// ⚠️ <b>现状记录（不代表认可）</b>：<c>default(LayerTransform)</c>（全零）被判为有效。
    /// </summary>
    /// <remarks>
    /// 这是"缺省值即有效"的设计：反序列化时一条字段缺失的记录会落到 <c>default</c>，
    /// 此时判无效会把整篇文档一起拒掉。手算：原点 (0,0) 有限且在 ±1e6 内、尺寸 (0,0) 有限、
    /// 角度 0 有限 → <c>IsValid == true</c>。
    /// <para>把它显式写进测试，是为了让"零尺寸图层合法"这个决定不再是无意的。</para>
    /// </remarks>
    [Fact]
    public void IsValid_缺省的零值变换算有效_现状记录()
    {
        Assert.True(new LayerTransform().IsValid);
    }

    // ═══════════════════ 与 ContractStringTests 的分工 ═══════════════════

    /// <summary>
    /// <see cref="SamplingStrings.Parse"/> 传 <see langword="null"/> 必须抛
    /// <see cref="ArgumentNullException"/> 而不是 <see cref="FormatException"/>。
    /// </summary>
    /// <remarks>
    /// <see cref="ContractStringTests"/> 已经把三档字面量的逐字往返、未知字面量抛
    /// <see cref="FormatException"/>、大小写敏感、枚举连续性都覆盖了，这里<b>不重复</b>。
    /// 唯一补上的是它只在 <c>BlendModeStrings</c> 上写过的 null 分支 ——
    /// 契约表头声明"字段缺失一律失败"，两条解析路径必须给出同一类异常，
    /// 否则反序列化端要写两套 catch。
    /// </remarks>
    [Fact]
    public void SamplingStrings_null抛ArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => SamplingStrings.Parse(null!));
}