using Compositor.Core;
using Compositor.Core.Composite;
using Xunit;

namespace Compositor.Core.Tests.Composite;

/// <summary>
/// 合成器主干（<c>LayerCompositor.Compose</c>）的测试。
/// </summary>
/// <remarks>
/// <para><b>🔴 本文件能证明什么、不能证明什么</b>：
/// <list type="bullet">
/// <item>✅ 能证明：遍历顺序、可见性过滤、有效不透明度接入 <see cref="BlendOps.Composite"/>
/// 这条链条在本实现里是通的，且<b>顺序方向没搞反</b>。</item>
/// <item>❌ <b>不能</b>证明与 Mac 版逐像素一致。Mac 的 <c>drawLiveComposite</c>
/// 驱动的是闭源 Core Graphics，本类是 CPU 逐像素，机制不同。
/// 这一点<b>只有 Tier 2 黄金样本能回答</b>，目前一个样本都没有。</item>
/// </list></para>
///
/// <para><b>期望值全部手算</b>，算式写在断言注释里。没有任何一条是「跑一遍实现再抄输出」——
/// 本文件里最容易被自欺的地方就是顺序：搞反了也不会崩，只会画错颜色，
/// 而「先跑一遍看输出对不对」会把错误输出变成期望值。</para>
///
/// <para><b>算式约定</b>：<c>ToByte(v) = Round(v × 255)</c>（远离零），
/// 见 <c>BlendOps.ToByte</c>。预乘值用 <c>PixelBuffer.Fill</c> 产生（内部做
/// <c>Premultiply(c, a) = (c × a + 127) / 255</c>）。</para>
/// </remarks>
public sealed class LayerCompositorTests
{
    // ══════════════════════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════════════════════

    private const int W = 2;
    private const int H = 2;

    private static LayerNode Leaf(Guid id, Guid? parent = null, bool visible = true, float opacity = 1.0f) =>
        new LayerNode
        {
            Id = id, Transform = new LayerTransform(),
            IsGroup = false, ParentId = parent, IsVisible = visible, Opacity = opacity,
        };

    private static LayerNode Group(Guid id, Guid? parent = null, bool visible = true, float opacity = 1.0f) =>
        new LayerNode
        {
            Id = id, Transform = new LayerTransform(),
            IsGroup = true, ParentId = parent, IsVisible = visible, Opacity = opacity,
        };

    private static LayerNode Adjustment(Guid id, Guid? parent = null) =>
        new LayerNode
        {
            Id = id, Transform = new LayerTransform(),
            ParentId = parent, Adjustment = new AdjustmentSpec { Kind = "Levels" },
        };

    /// <summary>整块填成同色的直通像素（<see cref="PixelBuffer.Fill"/> 内部会预乘）。</summary>
    private static PixelBuffer Solid(RgbaColor straight) => PixelBuffer.Create(W, H) is var b ? Fill(b, straight) : b;

    private static PixelBuffer Fill(PixelBuffer buffer, RgbaColor straight)
    {
        buffer.Fill(straight);
        return buffer;
    }

    private static Dictionary<Guid, LayerSurface> Surfaces(params (Guid Id, PixelBuffer Pixels)[] items)
    {
        var map = new Dictionary<Guid, LayerSurface>();
        foreach ((Guid id, PixelBuffer pixels) in items)
        {
            map[id] = new LayerSurface(id, pixels);
        }

        return map;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(PixelBuffer b, int x = 0, int y = 0)
    {
        int i = (y * b.Width + x) * 4;
        return (b.Raw[i], b.Raw[i + 1], b.Raw[i + 2], b.Raw[i + 3]);
    }

    private static bool IsFullyTransparent(PixelBuffer b)
    {
        foreach (byte v in b.Raw)
        {
            if (v != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static (byte R, byte G, byte B, byte A) Rgba(byte r, byte g, byte bl, byte a) => (r, g, bl, a);

    private static readonly RgbaColor Red = new(255, 0, 0, 255);
    private static readonly RgbaColor Green = new(0, 255, 0, 255);
    private static readonly RgbaColor White = new(255, 255, 255, 255);
    private static readonly RgbaColor Gray128 = new(128, 128, 128, 255);

    // ══════════════════════════════════════════════════════════════════════
    //  一、空输入
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>空文档 → 全透明画布（预乘下全 0），两个列表都空。</summary>
    /// <remarks>手算：<c>PixelBuffer.Create</c> 不填任何字节 ⇒ 全 0 ⇒ 预乘语义下就是透明。</remarks>
    [Fact]
    public void Compose_空图层_画布全透明且两个列表都空()
    {
        CompositeResult r = LayerCompositor.Compose(
            Array.Empty<LayerNode>(), new Dictionary<Guid, LayerSurface>(), W, H);

        Assert.True(IsFullyTransparent(r.Canvas));
        Assert.Empty(r.Painted);
        Assert.Empty(r.Skipped);
    }

    /// <summary>所有图层都不可见 → 画布仍全透明。</summary>
    [Fact]
    public void Compose_全部图层不可见_画布全透明()
    {
        Guid a = Guid.NewGuid();
        CompositeResult r = LayerCompositor.Compose(
            [Leaf(a, visible: false)], Surfaces((a, Solid(Red))), W, H);

        Assert.True(IsFullyTransparent(r.Canvas));
        Assert.Empty(r.Painted);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  二、顺序（本文件最重要的一组）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 单层不透明 Normal → 画布<b>逐字节等于</b>该层。
    /// </summary>
    /// <remarks>
    /// 手算：画布起点全 0（ba = 0），红色预乘后是 (255,0,0,255)/255 = (1,0,0,1)，sa = 1。
    /// <c>BlendPixel</c> 命中 <c>ba == 0</c> 捷径，直接返回源 (1,0,0,1)。
    /// <c>ToByte(1.0) = Round(255) = 255</c>。期望 (255, 0, 0, 255)。
    /// </remarks>
    [Fact]
    public void Compose_单层不透明_画布逐字节等于该层()
    {
        Guid a = Guid.NewGuid();
        CompositeResult r = LayerCompositor.Compose([Leaf(a)], Surfaces((a, Solid(Red))), W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(r.Canvas));
        Assert.Equal(new[] { a }, r.Painted);
    }

    /// <summary>
    /// 🔴 <b>索引 0 在最底层</b>：数组 [红, 绿] 的结果是<b>绿</b>。
    /// </summary>
    /// <remarks>
    /// 这是整份规格里最容易搞反、且<b>搞反了也不会崩</b>的一条。
    /// <para>手算（照源码走一遍）：</para>
    /// <list type="number">
    /// <item>先混红。画布全 0（ba=0）⇒ 捷径返回红 ⇒ 画布 = (255,0,0,255)。</item>
    /// <item>再混绿。此时 ba = 1，不走捷径。cs = (0,1,0)（源直通），cb = (1,0,0)（底直通）。
    /// Normal ⇒ B = Cs = (0,1,0)。wSrc = 1×(1−1) = 0，wMix = 1×1 = 1，wBkd = 0×1 = 0。
    /// 结果 = 0×Cs + 1×B + 0×Cb = (0,1,0)，α = 1 + 0 = 1 ⇒ (0,255,0,255)。</item>
    /// </list>
    /// <b>若实现反向迭代</b>（先绿后红），同样两步会得到 (255,0,0,255) ——
    /// 一个稳定的、看起来正常的错误。所以这条断言必须显式写死「结果是绿」。
    /// </remarks>
    [Fact]
    public void Compose_两个不透明层_索引0在下所以上层颜色胜出()
    {
        Guid bottom = Guid.NewGuid(), top = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Leaf(bottom), Leaf(top)],
            Surfaces((bottom, Solid(Red)), (top, Solid(Green))),
            W, H);

        Assert.Equal(Rgba(0, 255, 0, 255), Pixel(r.Canvas));
        Assert.Equal(new[] { bottom, top }, r.Painted);   // 报出顺序也是自底向上
    }

    /// <summary>🔴 反向对照组：数组 [绿, 红] 的结果必须是<b>红</b>。</summary>
    /// <remarks>
    /// 与上一条互为镜像。两条同时成立，才能证明结果真的跟数组顺序绑定，
    /// 而不是碰巧恒等于某个固定颜色。
    /// <para>手算：先混绿（ba=0 捷径）⇒ (0,255,0,255)；再混红 ⇒ (255,0,0,255)。</para>
    /// </remarks>
    [Fact]
    public void Compose_交换数组顺序_结果跟着换成另一层颜色()
    {
        Guid bottom = Guid.NewGuid(), top = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Leaf(bottom), Leaf(top)],
            Surfaces((bottom, Solid(Green)), (top, Solid(Red))),
            W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(r.Canvas));
    }

    /// <summary>半透明两层叠加，顺序反了 alpha 与颜色都会不同 —— 再加一道顺序探针。</summary>
    /// <remarks>
    /// 手算：底层黑不透明 (0,0,0,255)，顶层白 50% 不透明（直通 255 预乘后 = 128）。
    /// <para>正向 [黑, 白]：先混黑 ⇒ (0,0,0,255)。再混白：sa = 128/255，spr = 128/255 = 0.50196，
    /// ba = 1，cb = (0,0,0)。Normal ⇒ B = Cs = (1,1,1)。wSrc = sa×0 = 0，wMix = sa×1 = 0.50196，
    /// wBkd = (1−sa)×1 = 0.49804。结果 R = 0×1 + 0.50196×1 + 0.49804×0 = 0.50196 ⇒ 128。
    /// α = sa + (1−sa)×1 = 0.50196 + 0.49804 = 1 ⇒ 255。期望 (128,128,128,255)。</para>
    /// <para>反向 [白, 黑]：先混白 50% 于透明底 ⇒ ba=0 捷径 ⇒ (128,128,128,128)。
    /// 再混黑不透明：sa=1，spr=0，ba=0.50196，cb = 128/128… 实际 cs=(0,0,0)。
    /// wSrc = 1×(1−0.50196) = 0.49804 乘 Cs=0；wMix = 1×0.50196 乘 B=0；wBkd = 0×… = 0。
    /// 结果 = (0,0,0)，α = 1 ⇒ (0,0,0,255)。<b>与正向结果完全不同。</b></para>
    /// </remarks>
    [Fact]
    public void Compose_半透明叠层_顺序反了结果完全不同()
    {
        Guid bottom = Guid.NewGuid(), top = Guid.NewGuid();
        PixelBuffer halfWhite = Solid(new RgbaColor(255, 255, 255, 128));

        (byte R, byte G, byte B, byte A) forward = Pixel(
            LayerCompositor.Compose(
                [Leaf(bottom), Leaf(top)],
                Surfaces((bottom, Solid(new RgbaColor(0, 0, 0, 255))), (top, halfWhite)),
                W, H).Canvas);

        (byte R, byte G, byte B, byte A) reversed = Pixel(
            LayerCompositor.Compose(
                [Leaf(bottom), Leaf(top)],
                Surfaces((bottom, halfWhite), (top, Solid(new RgbaColor(0, 0, 0, 255)))),
                W, H).Canvas);

        Assert.Equal(Rgba(128, 128, 128, 255), forward);
        Assert.Equal(Rgba(0, 0, 0, 255), reversed);
        Assert.NotEqual(forward, reversed);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  三、可见性与分组传递
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>🔴 不可见层<b>不进 Painted</b>，也不影响画布。</summary>
    /// <remarks>
    /// 手算：只有红层参与 ⇒ 画布 = (255,0,0,255)；绿层被 Resolve 挡在 Drawn 之外，
    /// 主循环根本不会看到它。
    /// </remarks>
    [Fact]
    public void Compose_不可见层不参与合成()
    {
        Guid visible = Guid.NewGuid(), hidden = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Leaf(visible), Leaf(hidden, visible: false)],
            Surfaces((visible, Solid(Red)), (hidden, Solid(Green))),
            W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(r.Canvas));
        Assert.Equal(new[] { visible }, r.Painted);
        Assert.Empty(r.Skipped);
    }

    /// <summary>🔴 分组不可见 ⇒ 组内叶子一个像素都不画。</summary>
    /// <remarks>
    /// 手算：叶子在 Drawn 里吗？不在 —— Resolve 的 <c>shown: effective</c> 沿树传下去，
    /// 分组不可见时叶子 effective = false ⇒ 不进 Drawn ⇒ 主循环看不到它 ⇒ 画布全透明。
    /// <b>注意</b>：把 surface 传给叶子<b>不会</b>让它被画，因为决定权在 Resolve，不在 surface。
    /// </remarks>
    [Fact]
    public void Compose_分组不可见_组内叶子完全不画()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Group(g, visible: false), Leaf(leaf, parent: g)],
            Surfaces((leaf, Solid(Red))),
            W, H);

        Assert.True(IsFullyTransparent(r.Canvas));
        Assert.Empty(r.Painted);
    }

    /// <summary>分组自身即使传了 surface 也不会被画 —— <c>Drawn</c> 里没有分组。</summary>
    /// <remarks>
    /// 这条锁住一个 API 形状上的不变量：合成循环只对<b>叶子层</b>调 <see cref="BlendOps.Composite"/>。
    /// 若哪天有人把分组塞进 <c>Drawn</c>，合成器就会试图对一张没有像素的 surface 做混合。
    /// </remarks>
    [Fact]
    public void Compose_分组传了surface也不参与合成()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Group(g), Leaf(leaf, parent: g)],
            Surfaces((g, Solid(Green)), (leaf, Solid(Red))),
            W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(r.Canvas));
        Assert.Equal(new[] { leaf }, r.Painted);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  四、有效不透明度接入
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>🔴 分组不透明度<b>逐层相乘</b>进子层，0.5 的文件夹里不透明的层画出 50%。</summary>
    /// <remarks>
    /// 手算：effective(leaf) = 1.0 × 0.5 = 0.5。
    /// <c>Composite</c> 里 k = 1.0 × 0.5 × (1/255) = 0.5/255。
    /// sa = 255 × k = 0.5；sr = 255 × k = 0.5；底 ba = 0 ⇒ 捷径直接返回源。
    /// <c>ToByte(0.5) = Round(127.5) = 128</c>（远离零）。
    /// 期望 (128, 0, 0, 128) —— <b>alpha 也是 128</b>，因为预乘下缩放同时压 alpha。
    /// </remarks>
    [Fact]
    public void Compose_分组不透明度相乘进子层()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Group(g, opacity: 0.5f), Leaf(leaf, parent: g)],
            Surfaces((leaf, Solid(Red))),
            W, H);

        Assert.Equal(Rgba(128, 0, 0, 128), Pixel(r.Canvas));
    }

    /// <summary>两层嵌套各 0.5 → 有效 0.25。</summary>
    /// <remarks>
    /// 手算：effective = 1.0 × 0.5 × 0.5 = 0.25。k = 0.25/255。
    /// sa = 255 × 0.25/255 = 0.25，sr = 0.25，ba = 0 ⇒ 捷径返回 (0.25, 0, 0, 0.25)。
    /// <c>ToByte(0.25) = Round(63.75) = 64</c>。期望 (64, 0, 0, 64)。
    /// </remarks>
    [Fact]
    public void Compose_两层嵌套各半_相乘得到四分之一()
    {
        Guid outer = Guid.NewGuid(), inner = Guid.NewGuid(), leaf = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Group(outer, opacity: 0.5f), Group(inner, parent: outer, opacity: 0.5f), Leaf(leaf, parent: inner)],
            Surfaces((leaf, Solid(Red))),
            W, H);

        Assert.Equal(Rgba(64, 0, 0, 64), Pixel(r.Canvas));
    }

    /// <summary>不透明度为 0 的层画出来<b>什么都不改变</b>（不是被跳过，是画了等于没画）。</summary>
    /// <remarks>
    /// 手算：k = 0 ⇒ sa = 0 ⇒ <c>BlendPixel</c> 命中 <c>sa == 0</c> 捷径返回底色。
    /// 底色是底下的红层 ⇒ 结果仍是红，且它<b>出现在 Painted 里</b>。
    /// 这条区分「跳过」与「画了等于没画」两种语义 —— 两者像素相同但 <c>Painted</c> 不同。
    /// </remarks>
    [Fact]
    public void Compose_不透明度为0的层画了等于没画但仍记入Painted()
    {
        Guid bottom = Guid.NewGuid(), top = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Leaf(bottom), Leaf(top, opacity: 0.0f)],
            Surfaces((bottom, Solid(Red)), (top, Solid(Green))),
            W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(r.Canvas));
        Assert.Equal(new[] { bottom, top }, r.Painted);
    }

    /// <summary>分组的混合模式被忽略 —— 分组根本不参与合成，叶子用自己的模式。</summary>
    /// <remarks>
    /// <b>手算（Multiply）：</b>底层灰 128 不透明，预乘后 (128,128,128,255) ⇒ cb = 128/255 ≈ 0.50196。
    /// 顶层白不透明 ⇒ cs = (1,1,1)，sa = 1，ba = 1。
    /// B = cb × cs = 0.50196。wSrc = 0，wMix = 1，wBkd = 0 ⇒ 结果 = 0.50196。
    /// <c>ToByte(0.50196 × 255) = Round(127.99999…) = 128</c>。α = 1 ⇒ 255。
    /// 期望 (128,128,128,255)。<br/>
    /// <b>若模式错取成 Normal</b>，结果会是顶层白 (255,255,255,255) —— 明显不同。
    /// </remarks>
    [Fact]
    public void Compose_混合模式取自叶子图层_Multiply结果与Normal不同()
    {
        Guid bottom = Guid.NewGuid(), top = Guid.NewGuid();

        LayerNode topNode = Leaf(top);
        CompositeResult multiply = LayerCompositor.Compose(
            [Leaf(bottom), topNode with { BlendMode = BlendMode.Multiply }],
            Surfaces((bottom, Solid(Gray128)), (top, Solid(White))),
            W, H);

        CompositeResult normal = LayerCompositor.Compose(
            [Leaf(bottom), topNode with { BlendMode = BlendMode.Normal }],
            Surfaces((bottom, Solid(Gray128)), (top, Solid(White))),
            W, H);

        Assert.Equal(Rgba(128, 128, 128, 255), Pixel(multiply.Canvas));   // Multiply
        Assert.Equal(Rgba(255, 255, 255, 255), Pixel(normal.Canvas));     // Normal
    }

    /// <summary>分组的混合模式即使设成 Multiply，也<b>不会</b>作用到子层。</summary>
    /// <remarks>
    /// <c>LayerAppearance.swift:86-88</c> 明确写了分组自身的 BlendMode 被关掉
    /// （<c>LayerNode.IsGroup</c> 恒为 <see cref="BlendMode.Normal"/> 的注释在
    /// <c>Abstractions.cs:90</c>）。本测试从结果侧验证它：分组是 Multiply、叶子是 Normal 时，
    /// 结果与「分组是 Normal、叶子是 Normal」逐字节相同。
    /// </remarks>
    [Fact]
    public void Compose_分组的混合模式不作用于子层()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid();

        CompositeResult groupMultiply = LayerCompositor.Compose(
            [Group(g) with { BlendMode = BlendMode.Multiply }, Leaf(leaf, parent: g)],
            Surfaces((leaf, Solid(Gray128))),
            W, H);

        CompositeResult groupNormal = LayerCompositor.Compose(
            [Group(g) with { BlendMode = BlendMode.Normal }, Leaf(leaf, parent: g)],
            Surfaces((leaf, Solid(Gray128))),
            W, H);

        Assert.Equal(Pixel(groupNormal.Canvas), Pixel(groupMultiply.Canvas));
        Assert.Equal(Rgba(128, 128, 128, 255), Pixel(groupMultiply.Canvas));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  五、深度截断在主循环上的表现
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>🔴 超过 65 层的叶子<b>不会被画</b>，因为它压根不在 Drawn 里。</summary>
    /// <remarks>
    /// 手算：70 层嵌套，最深处的叶子由 <c>Visit(depth=65)</c> 处理，
    /// guard <c>65 &lt;= 64</c> 失败 ⇒ 不进 Drawn ⇒ 主循环看不到 ⇒ 画布全透明。
    /// <para>这条把 <b>Resolve 层的截断</b>和<b>合成层的可见性</b>连起来：
    /// 主循环自己没有深度概念，它只信任 Drawn。</para>
    /// </remarks>
    [Fact]
    public void Compose_超过65层的叶子不被绘制()
    {
        Guid[] ids = Enumerable.Range(0, 70).Select(_ => Guid.NewGuid()).ToArray();
        Guid deep = Guid.NewGuid();

        List<LayerNode> nodes = [];
        for (int i = 0; i < ids.Length; i++)
        {
            nodes.Add(Group(ids[i], parent: i == 0 ? null : ids[i - 1]));
        }

        nodes.Add(Leaf(deep, parent: ids[69]));

        CompositeResult r = LayerCompositor.Compose(nodes, Surfaces((deep, Solid(Red))), W, H);

        Assert.True(IsFullyTransparent(r.Canvas));
        Assert.Empty(r.Painted);
    }

    /// <summary>恰好第 64 层下的叶子<b>会</b>被画 —— 上一条的边界对照。</summary>
    /// <remarks>
    /// 手算：叶子由 <c>Visit(depth=64)</c> append，guard <c>64 &lt;= 64</c> 通过 ⇒ 进 Drawn ⇒ 被画。
    /// 结果应是全不透明红。
    /// </remarks>
    [Fact]
    public void Compose_第64层下的叶子会被绘制()
    {
        Guid[] ids = Enumerable.Range(0, 64).Select(_ => Guid.NewGuid()).ToArray();
        Guid deep = Guid.NewGuid();

        List<LayerNode> nodes = [];
        for (int i = 0; i < ids.Length; i++)
        {
            nodes.Add(Group(ids[i], parent: i == 0 ? null : ids[i - 1]));
        }

        nodes.Add(Leaf(deep, parent: ids[63]));

        CompositeResult r = LayerCompositor.Compose(nodes, Surfaces((deep, Solid(Red))), W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(r.Canvas));
        Assert.Equal(new[] { deep }, r.Painted);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  六、没有栅格内容的图层
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>🔴 调整层被记入 <see cref="CompositeResult.Skipped"/>，不记入 Painted，且不影响画布。</summary>
    /// <remarks>
    /// 调整层<b>会</b>出现在 Drawn 里（Resolve 不看 <c>IsAdjustment</c>），
    /// 主循环靠「surface 查不到」把它认出来。这对应 Mac 的
    /// <c>guard let image = layer.asset?.image else { return }</c>（<c>LiveLayerMask.swift:166</c>）——
    /// 调整层的 <c>asset</c> 本来就是 nil。
    /// <para>期望：画布全透明，Painted 空，Skipped = [调整层 id]。</para>
    /// </remarks>
    [Fact]
    public void Compose_调整层无栅格内容_记入Skipped()
    {
        Guid adj = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose([Adjustment(adj)], Surfaces(), W, H);

        Assert.True(IsFullyTransparent(r.Canvas));
        Assert.Empty(r.Painted);
        Assert.Equal(new[] { adj }, r.Skipped);
    }

    /// <summary>调整层夹在两个像素层之间时，前后两层照常合成。</summary>
    /// <remarks>
    /// 手算：红色底层、调整层（跳过）、绿色顶层 ⇒ 结果是绿 (0,255,0,255)。
    /// Painted = [红, 绿]（跳过的不在里面），Skipped = [调整层]。
    /// </remarks>
    [Fact]
    public void Compose_调整层夹在中间不打断前后层的合成()
    {
        Guid bottom = Guid.NewGuid(), adj = Guid.NewGuid(), top = Guid.NewGuid();

        CompositeResult r = LayerCompositor.Compose(
            [Leaf(bottom), Adjustment(adj), Leaf(top)],
            Surfaces((bottom, Solid(Red)), (top, Solid(Green))),
            W, H);

        Assert.Equal(Rgba(0, 255, 0, 255), Pixel(r.Canvas));
        Assert.Equal(new[] { bottom, top }, r.Painted);
        Assert.Equal(new[] { adj }, r.Skipped);
    }

    /// <summary>没有栅格的普通层也走 Skipped（不只是调整层）。</summary>
    /// <remarks>缺 surface 与「是调整层」是同一条代码路径，这里确认它不挑图层类型。</remarks>
    [Fact]
    public void Compose_普通层缺surface也记入Skipped()
    {
        Guid a = Guid.NewGuid();
        CompositeResult r = LayerCompositor.Compose([Leaf(a)], Surfaces(), W, H);

        Assert.Empty(r.Painted);
        Assert.Equal(new[] { a }, r.Skipped);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  七、参数与不变量的拒绝路径
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>surface 尺寸与画布不一致 → <see cref="ArgumentException"/>，消息<b>点名图层</b>。</summary>
    /// <remarks>期望消息里含图层 id 与两侧尺寸，方便直接定位。</remarks>
    [Fact]
    public void Compose_像素尺寸与画布不一致_抛异常并点名图层()
    {
        Guid a = Guid.NewGuid();
        PixelBuffer wrong = PixelBuffer.Create(3, 5);

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            LayerCompositor.Compose([Leaf(a)], Surfaces((a, wrong)), W, H));

        Assert.Contains(a.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("3×5", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>🔴 有效不透明度越界 → <see cref="InvalidOperationException"/>，点名图层与数值。</summary>
    /// <remarks>
    /// 构造 <c>Opacity = 1.5f</c>（违反契约「有限且落在 0..1」）。
    /// 手算：effective = 1.5 → 越界 → 拒绝。
    /// <para>为什么不静默 clamp：见 <see cref="LayerCompositor.Compose"/> 的参数文档。
    /// 若这里改成 clamp，本测试会红 —— 这是有意的，它锁住「大声失败」这个决定。</para>
    /// </remarks>
    [Fact]
    public void Compose_不透明度越界_抛异常并点名图层()
    {
        Guid a = Guid.NewGuid();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            LayerCompositor.Compose([Leaf(a, opacity: 1.5f)], Surfaces((a, Solid(Red))), W, H));

        Assert.Contains(a.ToString(), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>分组不透明度 0.5 × 自身 3.0 越界 → 同样被拒（不只查自身字段）。</summary>
    /// <remarks>手算：effective = 3.0 × 0.5 = 1.5 → 越界。</remarks>
    [Fact]
    public void Compose_父链相乘后才越界_同样被拒()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() => LayerCompositor.Compose(
            [Group(g, opacity: 0.5f), Leaf(leaf, parent: g, opacity: 3.0f)],
            Surfaces((leaf, Solid(Red))),
            W, H));
    }

    /// <summary>负不透明度同样被拒。</summary>
    /// <remarks>手算：effective = −0.5 ⇒ 越界。</remarks>
    [Fact]
    public void Compose_负不透明度_抛异常()
    {
        Guid a = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() => LayerCompositor.Compose(
            [Leaf(a, opacity: -0.5f)], Surfaces((a, Solid(Red))), W, H));
    }

    /// <summary>NaN 不透明度被拒，且消息里可读（不抛 <c>FormatException</c>）。</summary>
    /// <remarks>消息用 <c>"R"</c> 格式 + 不变文化，否则不同区域设置的机器上会输出不同的破折号。</remarks>
    [Fact]
    public void Compose_NaN不透明度_抛InvalidOperationException而不是格式异常()
    {
        Guid a = Guid.NewGuid();
        LayerNode node = Leaf(a) with { Opacity = float.NaN };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            LayerCompositor.Compose([node], Surfaces((a, Solid(Red))), W, H));

        Assert.Contains("NaN", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>图层列表为 null → <see cref="ArgumentNullException"/>, paramName = "layers"。</summary>
    [Fact]
    public void Compose_图层列表为null_抛ArgumentNullException()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            LayerCompositor.Compose(null!, new Dictionary<Guid, LayerSurface>(), W, H));

        Assert.Equal("layers", ex.ParamName);
    }

    /// <summary>surface 表为 null → <see cref="ArgumentNullException"/>, paramName = "surfaces"。</summary>
    [Fact]
    public void Compose_surface表为null_抛ArgumentNullException()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            LayerCompositor.Compose(Array.Empty<LayerNode>(), null!, W, H));

        Assert.Equal("surfaces", ex.ParamName);
    }

    /// <summary>画布宽高非正 → 由 <see cref="PixelBuffer.Create"/> 抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
    /// <remarks>主循环自己不校验尺寸，尺寸校验统一交给缓冲工厂，避免两处规则漂移。</remarks>
    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    public void Compose_画布尺寸非正_抛ArgumentOutOfRangeException(int w, int h)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LayerCompositor.Compose(Array.Empty<LayerNode>(), new Dictionary<Guid, LayerSurface>(), w, h));
    }

    /// <summary>重复图层 id → 重复 id 的守门在 <c>BuildLookup</c>，这里确认它会冒到最外层。</summary>
    /// <remarks>
    /// 🔴 <b>同一份重复 id，<see cref="LayerOpacity.BuildLookup"/> 抛
    /// <see cref="ArgumentException"/><c>，而合成器的 id 索引是「后者覆盖」。</c></b>
    /// 这不是矛盾 —— 见 <c>LayerCompositor.BuildIdIndex</c> 的文档：
    /// 纯函数（不透明度）必须炸，取谁会改变像素；索引只服务于「Drawn 里的 id → 节点」，
    /// 而它<b>必定在 BuildLookup 之后</b>执行，重复分支根本走不到。
    /// 这条测试把这个先后次序钉住：谁先谁后一旦调换，异常类型就会变。
    /// </remarks>
    [Fact]
    public void Compose_重复图层id_抛ArgumentException而不是静默覆盖()
    {
        Guid dup = Guid.NewGuid();
        List<LayerNode> nodes = [Leaf(dup, opacity: 0.5f), Leaf(dup, opacity: 1.0f)];

        Assert.Throws<ArgumentException>(() =>
            LayerCompositor.Compose(nodes, Surfaces((dup, Solid(Red))), W, H));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  八、画布不被复用
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>两次 Compose 各自新建画布，互不污染。</summary>
    /// <remarks>
    /// 如果实现复用上一块画布（比如缓存成员字段），第二次结果会带上第一次的像素 ——
    /// 而这种 bug 在「每次都传新参数」的测试里完全看不出来。
    /// </remarks>
    [Fact]
    public void Compose_两次调用互不污染()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        CompositeResult first = LayerCompositor.Compose(
            [Leaf(a)], Surfaces((a, Solid(Red))), W, H);

        CompositeResult second = LayerCompositor.Compose(
            [Leaf(b)], Surfaces((b, Solid(Green))), W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(first.Canvas));
        Assert.Equal(Rgba(0, 255, 0, 255), Pixel(second.Canvas));
        Assert.NotSame(first.Canvas, second.Canvas);
    }

    /// <summary>输入的 surface 缓冲<b>不被就地修改</b>。</summary>
    /// <remarks>
    /// <c>BlendOps.Composite</c> 只写 backdrop，读 source。这条锁住「源缓冲只读」，
    /// 否则重复调用同一份 surface 渲染两遍会得到不同结果（第二遍更淡）。
    /// </remarks>
    [Fact]
    public void Compose_不修改输入的surface缓冲()
    {
        Guid a = Guid.NewGuid();
        PixelBuffer source = Solid(Red);

        LayerCompositor.Compose([Leaf(a)], Surfaces((a, source)), W, H);

        Assert.Equal(Rgba(255, 0, 0, 255), Pixel(source));
    }

    /// <summary>同一份 surface 连渲两遍，结果完全一致（上一条的推论）。</summary>
    [Fact]
    public void Compose_同一份surface连渲两遍结果一致()
    {
        Guid a = Guid.NewGuid();
        PixelBuffer source = Solid(new RgbaColor(255, 255, 255, 128));
        Dictionary<Guid, LayerSurface> surfaces = Surfaces((a, source));

        (byte R, byte G, byte B, byte A) first = Pixel(
            LayerCompositor.Compose([Leaf(a)], surfaces, W, H).Canvas);

        (byte R, byte G, byte B, byte A) second = Pixel(
            LayerCompositor.Compose([Leaf(a)], surfaces, W, H).Canvas);

        Assert.Equal(first, second);
    }
}