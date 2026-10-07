using Compositor.Adjust;
using Compositor.Core;
using Xunit;

namespace Compositor.MaskEffects.Tests;

/// <summary>
/// 成栈判定的测试。最要紧的两条：<c>:84</c> 是 <c>break</c> 不是 <c>continue</c>；
/// 调整层<b>不能当 base</b>但<b>可以当栈内子层</b>。
/// </summary>
public class ClippingStackTests
{
    // 构造形如 "00000000-0000-0000-0000-00000000000N" 的确定性 Guid，便于排错。
    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static MaskLayer Layer(int id, Guid? source = null, Guid? parent = null,
        bool isAdjustment = false, BlendMode blend = BlendMode.Normal)
        => new(G(id), source, parent, blend,
            isAdjustment ? new LayerAdjustment { Kind = AdjustmentKind.Invert } : null);

    private static LiveMaskRenderer Make(IReadOnlyList<MaskLayer> layers)
        => new(new DocRect { Origin = new DocPoint(0, 0), Size = new DocSize(8, 8) },
               layers, (_, _, _) => { });

    // ── 基本成栈 ──────────────────────────────────────────────────────────

    [Fact]
    public void 连续子层成栈()
    {
        var r = Make(
        [
            Layer(1),                       // base
            Layer(2, G(1)),                 // 子层 1
            Layer(3, G(1)),                 // 子层 2
        ]);
        r.PrepareStacks();

        Assert.Equal(2, r.ChildrenOf(G(1)).Count);
        Assert.True(r.IsStacked(G(2)));
        Assert.True(r.IsStacked(G(3)));
        Assert.False(r.IsStacked(G(1)));
    }

    [Fact]
    public void 没有子层时不成栈()
    {
        var r = Make([Layer(1)]);
        r.PrepareStacks();
        Assert.Empty(r.ChildrenOf(G(1)));
        Assert.False(r.IsStacked(G(1)));
    }

    [Fact]
    public void 有来源蒙版的层不能当base()
    {
        // 2 自己被 1 剪贴 → 它自己有 source，不满足 base 的前置条件。
        // 3 想被 2 剪贴，但 2 不能当 base，所以 3 不会被并入。
        var r = Make(
        [
            Layer(1),                       // 无 source，合法 base
            Layer(2, G(1)),                 // 有 source → 不能当 base
            Layer(3, G(2)),                 // 想被 2 剪贴
        ]);
        r.PrepareStacks();

        Assert.Empty(r.ChildrenOf(G(2)));
        Assert.False(r.IsStacked(G(3)));
        // 但 2 仍然是 1 的合法子层。
        Assert.Contains(G(2), r.ChildrenOf(G(1)));
        Assert.True(r.IsStacked(G(2)));
    }

    [Fact]
    public void 无来源蒙版的层可以当base()
    {
        var r = Make([Layer(1), Layer(2, G(1))]);
        r.PrepareStacks();
        Assert.Contains(G(2), r.ChildrenOf(G(1)));
    }

    // ── :84 是 break 不是 continue ──────────────────────────────────────────

    [Fact]
    public void 中间夹一个无关层则不再收集后续子层()
    {
        // A 被 B 剪贴；C 与 A 无关；D 也被 A 剪贴。
        // 源文件 :84 的 else { break } 意味着：B 收下，C 打断，D **不再收**。
        var r = Make(
        [
            Layer(1),                       // A
            Layer(2, G(1)),                 // B  → 收
            Layer(3),                       // C  → 打断
            Layer(4, G(1)),                 // D  → 不应被收
        ]);
        r.PrepareStacks();

        var children = r.ChildrenOf(G(1));
        Assert.Single(children);
        Assert.Equal(G(2), children[0]);
        // 写成 continue 时 D 会被收进来 —— 这条测试就是防那个错误。
        Assert.DoesNotContain(G(4), children);
        Assert.False(r.IsStacked(G(4)));
    }

    [Fact]
    public void 父分组不同则打断收集()
    {
        var r = Make(
        [
            Layer(1, parent: G(100)),       // A 在分组 100
            Layer(2, G(1), G(100)),         // 同分组 → 收
            Layer(3, G(1), G(200)),         // 不同分组 → 打断
            Layer(4, G(1), G(100)),         // 即使父分组又相同，也不再收
        ]);
        r.PrepareStacks();

        var children = r.ChildrenOf(G(1));
        Assert.Single(children);
        Assert.Equal(G(2), children[0]);
    }

    [Fact]
    public void 同分组连续子层都能收()
    {
        var r = Make(
        [
            Layer(1, parent: G(100)),
            Layer(2, G(1), G(100)),
            Layer(3, G(1), G(100)),
            Layer(4, G(1), G(100)),
        ]);
        r.PrepareStacks();
        Assert.Equal(3, r.ChildrenOf(G(1)).Count);
    }

    // ── 调整层的两种身份 ──────────────────────────────────────────────────

    [Fact]
    public void 调整层不能当base()
    {
        // :81 —— adjustment(base) == nil 是成栈前置条件。
        var r = Make(
        [
            Layer(1, isAdjustment: true),  // 调整层当 base
            Layer(2, G(1)),                 // 想被它剪贴
        ]);
        r.PrepareStacks();

        Assert.Empty(r.ChildrenOf(G(1)));
        Assert.False(r.IsStacked(G(2)));
    }

    [Fact]
    public void 调整层可以当栈内子层()
    {
        // :113 —— 收集子层后逐个绘制时，adjustment(child) != nil 走 adjust 分支。
        var r = Make(
        [
            Layer(1),                              // base 是像素层
            Layer(2, G(1), isAdjustment: true),    // 调整层作为栈内成员
        ]);
        r.PrepareStacks();

        // 它确实被并入了栈。
        Assert.True(r.IsStacked(G(2)));
        Assert.Contains(G(2), r.ChildrenOf(G(1)));
        // 但绘制时走的是 ApplyAdjustment，不是 DrawOwn。
        Assert.NotNull(r.AdjustmentOf(G(2)));
        Assert.Null(r.AdjustmentOf(G(1)));
    }

    [Fact]
    public void 两种身份不冲突_调整层不是base却能进栈()
    {
        // :81 与 :113 看着矛盾，其实说的是不同位置：前者是「栈的基底」，后者是「栈里的成员」。
        var r = Make(
        [
            Layer(1),                                  // 像素层 base
            Layer(2, G(1)),                            // 像素层成员
            Layer(3, G(1), isAdjustment: true),        // 调整层成员
        ]);
        r.PrepareStacks();

        var children = r.ChildrenOf(G(1));
        Assert.Equal(2, children.Count);
        Assert.Contains(G(2), children);
        Assert.Contains(G(3), children);
        Assert.False(r.IsStacked(G(1)));
    }

    // ── 栈的混合模式 ──────────────────────────────────────────────────────

    [Fact]
    public void 栈用base的混合模式()
    {
        // :89 —— stackModes[base] = blend(base)，不是子层的。
        var r = Make(
        [
            Layer(1, blend: BlendMode.Multiply),
            Layer(2, G(1), blend: BlendMode.Screen),
            Layer(3, G(1), blend: BlendMode.Darken),
        ]);
        r.PrepareStacks();
        Assert.Equal(BlendMode.Multiply, r.StackModeOf(G(1)));
    }

    [Fact]
    public void 非base层的栈模式为Normal()
        => Assert.Equal(BlendMode.Normal, Make([Layer(1)]).StackModeOf(G(1)));

    // ── 覆盖度：自引用 / 成环 / 链长 ─────────────────────────────────────

    /// <summary>
    /// 遵守 <c>DrawOwn</c> 契约的夹具：第二个参数是蒙版门限，
    /// 为 <see langword="null"/> 时不受限；为 0 的像素不产生任何写入。
    /// </summary>
    private static void FillOverGate(PixelBuffer buf, Coverage8? gate)
    {
        var raw = buf.Raw;
        for (int i = 0; i < raw.Length; i += 4)
        {
            int k = gate is null ? 255 : gate.Data[(i / 4) % gate.Width];
            if (k == 0) continue;
            raw[i] = (byte)k; raw[i + 1] = (byte)k; raw[i + 2] = (byte)k; raw[i + 3] = (byte)k;
        }
    }

    /// <summary>
    /// 🔴 语义澄清：<c>visiting</c> 挡的是<b>递归途中重复进入的 id</b>，
    /// 而<b>入口调用者本身不受影响</b>——它先被放进 <c>visiting</c>，再递归。
    /// 这与源文件 <c>:157</c> 完全一致（<c>visiting.insert(id)</c> 在调用 <c>draw</c> <b>之前</b>）。
    /// </summary>
    private static Coverage8? CoverageThrough(IReadOnlyList<MaskLayer> layers, Guid probe)
    {
        var r = new LiveMaskRenderer(
            new DocRect { Origin = new DocPoint(0, 0), Size = new DocSize(4, 4) },
            layers, (_, buf, gate) => FillOverGate(buf, gate));
        r.PrepareStacks();
        return r.CoverageOf(probe);
    }

    [Fact]
    public void 自引用时不产生像素()
    {
        var self = G(1);
        var cov = CoverageThrough([new MaskLayer(self, self, null, BlendMode.Normal, null)], self);
        Assert.NotNull(cov);
        Assert.Equal(0, System.Linq.Enumerable.Max(cov!.Data.ToArray()));
    }

    [Fact]
    public void 成环时不产生像素且不死循环()
    {
        var layers = new MaskLayer[]
        {
            new MaskLayer(G(1), G(2), null, BlendMode.Normal, null),
            new MaskLayer(G(2), G(1), null, BlendMode.Normal, null),
        };
        var cov = CoverageThrough(layers, G(1));
        Assert.NotNull(cov);
        Assert.Equal(0, System.Linq.Enumerable.Max(cov!.Data.ToArray()));
    }

    [Fact]
    public void 正常链的覆盖度为满()
    {
        // 反面对照：无 source 的层 alpha=255 → coverage 应为 255。
        var cov = CoverageThrough([new MaskLayer(G(1), null, null, BlendMode.Normal, null)], G(1));
        Assert.NotNull(cov);
        Assert.Equal(255, System.Linq.Enumerable.Max(cov!.Data.ToArray()));
    }

    [Fact]
    public void 活蒙版链超256时被截断而不死循环()
    {
        var layers = new List<MaskLayer> { new(G(0), null, null, BlendMode.Normal, null) };
        for (int i = 1; i <= 300; i++) layers.Add(new MaskLayer(G(i), G(i - 1), null, BlendMode.Normal, null));
        // 深度 300 > 256；不抛异常、不栈溢出即达标。
        var cov = CoverageThrough(layers, G(300));
        Assert.NotNull(cov);
    }

    [Fact]
    public void 深度上限常量是256()
        => Assert.Equal(256, LiveMaskRenderer.MaxStackDepth);
}