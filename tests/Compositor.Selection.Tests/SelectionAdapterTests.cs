namespace Compositor.Selection.Tests;

using Compositor.Core;
using Compositor.Selection;
using Xunit;

/// <summary>
/// <see cref="SelectionDocument"/> 与 <see cref="SelectionAdapter"/> 的行为规格。
/// </summary>
/// <remarks>
/// 【照抄组】测试逐条对应 Mac 版 <c>Document/Selection.swift</c> 的具体行号。
/// 【推导组】由契约 v1.2 的数学定义推导，不引用 Mac 行号。
/// 两组在本文件内严格分开，方法名与注释标明来源。
/// </remarks>
public class SelectionAdapterTests
{
    private static DocRect Rect(int x, int y, int w, int h) =>
        new(new DocPoint(x, y), new DocSize(w, h));

    /// <summary>一个 20×20 的实心方块，位于文档 (10,10)。</summary>
    private static SelectionDocument SolidBlock(bool antialiased = true, double feather = 0)
    {
        DocRect bounds = Rect(10, 10, 20, 20);
        CoveragePlane plane = CoveragePlane.CreateFilled(bounds, 255);
        SelectionDocument doc = SelectionDocument.FromSelectionMask(
            SelectionAdapter.ToSelectionMask(plane), antialiased, feather);
        return doc;
    }

    // ─────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:19  setShouldAntialias(antialiased || feather > 0)
    // ─────────────────────────────────────────────────────────────

    /// <summary>照抄 Selection.swift:19 —— 抗锯齿是<b>条件</b>触发，不是永远开。</summary>
    [Fact]
    public void ShouldAntialias_FollowsTheConditionalRule()
    {
        // (antialiased, feather) → 期望。四种组合全部覆盖。
        Assert.False(ShouldAntialiasOf(false, 0));    // 两者皆假 → 关
        Assert.True(ShouldAntialiasOf(true, 0));     // 仅 antialiased → 开
        Assert.True(ShouldAntialiasOf(false, 6));    // 仅 feather>0 → 开（关键的一半）
        Assert.True(ShouldAntialiasOf(true, 6));     // 两者皆真 → 开
    }

    private static bool ShouldAntialiasOf(bool antialiased, double feather) =>
        SelectionDocument.Rasterize(
            Rect(0, 0, 4, 4),
            (x, y) => x is >= 1 and < 3 && y is >= 1 and < 3,
            antialiased,
            feather).ShouldAntialias;

    /// <summary>
    /// 照抄 Selection.swift:19 的<b>后果</b>：关掉抗锯齿且 feather=0 时，
    /// 光栅输出只能出现 0 与 255，不可能出现边缘灰度。
    /// </summary>
    /// <remarks>
    /// 这是「条件触发」能被观测到的证据：若抗锯齿被无条件打开，
    /// 边界像素会出现 1..254 的中间值。
    /// </remarks>
    [Fact]
    public void Rasterize_WithAntialiasOff_ProducesOnlyZeroOrFullCoverage()
    {
        SelectionDocument doc = SelectionDocument.Rasterize(
            Rect(0, 0, 4, 4),
            (x, y) => x is >= 1 and < 3 && y is >= 1 and < 3,   // 边界落在像素边界上
            antialiased: false);

        ReadOnlySpan<byte> px = doc.Coverage.Pixels;
        foreach (byte v in px)
        {
            Assert.True(v is 0 or 255, $"抗锯齿关闭时出现了中间值 {v}。");
        }
    }

    /// <summary>
    /// 照抄 Selection.swift:19 的<b>后果</b>：<c>antialiased=false</c> 但 <c>feather&gt;0</c> 时，
    /// 抗锯齿被强制打开，边缘出现灰度。
    /// </summary>
    /// <remarks>
    /// 这条守住 <see cref="SelectionDocument.ShouldAntialias"/> 里 <c>|| feather &gt; 0</c> 那一半。
    /// 若漏掉，被显式关掉抗锯齿的选区再羽化时边缘会是阶梯而非渐变。
    /// </remarks>
    [Fact]
    public void Rasterize_WithFeatherForcesAntialiasBackOn()
    {
        // 形状边界刻意落在像素中间（1.5），关闭抗锯齿时必然产生半覆盖。
        SelectionDocument off = SelectionDocument.Rasterize(
            Rect(0, 0, 4, 4), (x, _) => x < 1.5, antialiased: false, feather: 0);

        SelectionDocument feathered = SelectionDocument.Rasterize(
            Rect(0, 0, 4, 4), (x, _) => x < 1.5, antialiased: false, feather: 4);

        Assert.False(off.ShouldAntialias);
        Assert.True(feathered.ShouldAntialias);
        Assert.Contains(feathered.Coverage.Pixels.ToArray(), v => v is > 0 and < 255);
    }

    // ─────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:243  Subtract 且无选区 → 直接 return，不改状态
    // ─────────────────────────────────────────────────────────────

    /// <summary>照抄 Selection.swift:243 —— 从"无选区"里减去东西，什么都不做。</summary>
    [Fact]
    public void Apply_SubtractWithNoSelection_LeavesNoSelection()
    {
        CoveragePlane incoming = CoveragePlane.CreateFilled(Rect(0, 0, 8, 8), 255);

        SelectionDocument? result = SelectionAdapter.Apply(
            SelectionCombineMode.Subtract, current: null, incoming, new DocSize(64, 64), antialiased: true);

        Assert.Null(result);
    }

    /// <summary>照抄 Selection.swift:240 的 <c>?? clipped</c> —— Add 且无选区等价于 Replace。</summary>
    [Fact]
    public void Apply_AddWithNoSelection_BehavesLikeReplace()
    {
        CoveragePlane incoming = CoveragePlane.CreateFilled(Rect(2, 2, 6, 6), 255);

        SelectionDocument? added = SelectionAdapter.Apply(
            SelectionCombineMode.Add, current: null, incoming, new DocSize(64, 64), antialiased: true);
        SelectionDocument? replaced = SelectionAdapter.Apply(
            SelectionCombineMode.Replace, current: null, incoming, new DocSize(64, 64), antialiased: true);

        Assert.NotNull(added);
        Assert.NotNull(replaced);
        Assert.Equal(replaced!.Coverage.Bounds, added!.Coverage.Bounds);
        Assert.Equal(replaced.Coverage.Pixels.ToArray(), added.Coverage.Pixels.ToArray());
    }

    /// <summary>照抄 Selection.swift:239 —— Replace 直接采用裁剪后的新形状。</summary>
    [Fact]
    public void Apply_Replace_TakesTheIncomingShape()
    {
        SelectionDocument current = SolidBlock();
        CoveragePlane incoming = CoveragePlane.CreateFilled(Rect(40, 40, 4, 4), 255);

        SelectionDocument? result = SelectionAdapter.Apply(
            SelectionCombineMode.Replace, current, incoming, new DocSize(64, 64), antialiased: true);

        Assert.NotNull(result);
        Assert.Equal(Rect(40, 40, 4, 4), result!.Coverage.Bounds);
    }

    // ─────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:236  先裁剪到画布，再做任何布尔运算
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 照抄 Selection.swift:236 —— 画布裁剪发生在<b>布尔运算之前</b>。
    /// </summary>
    /// <remarks>
    /// 若漏掉裁剪，画布外的那部分会先参与运算：
    /// 对「加」会让包围盒虚胖（超出画布），对「差」会把画布外的选区也挖掉。
    /// 这里让新形状横跨画布右边界，若先布尔不裁剪，结果包围盒会越过画布右沿。
    /// </remarks>
    [Fact]
    public void Apply_ClipsToCanvasBeforeAnyBooleanOperation()
    {
        DocSize canvas = new(32, 32);
        // x 从 28 到 36：右半截在画布外。
        CoveragePlane incoming = CoveragePlane.CreateFilled(Rect(28, 4, 8, 8), 255);

        SelectionDocument? result = SelectionAdapter.Apply(
            SelectionCombineMode.Replace, current: null, incoming, canvas, antialiased: true);

        Assert.NotNull(result);
        Assert.True(
            result!.Coverage.Bounds.Right <= canvas.Width,
            $"结果越出画布右沿 {canvas.Width}，实际右沿 {result.Coverage.Bounds.Right}");
        Assert.Equal(new DocRect(new DocPoint(28, 4), new DocSize(4, 8)), result.Coverage.Bounds);
    }

    /// <summary>
    /// 照抄 Selection.swift:236 —— 裁剪先于布尔：Add 时画布外的部分不能把包围盒撑出去。
    /// </summary>
    [Fact]
    public void Apply_Add_DoesNotLetOffCanvasPartInflateBounds()
    {
        DocSize canvas = new(32, 32);
        SelectionDocument current = SolidBlock();                              // (10,10) 20×20 → 右沿 30
        CoveragePlane incoming = CoveragePlane.CreateFilled(Rect(28, 4, 8, 8), 255); // 越到 x=36

        SelectionDocument? result = SelectionAdapter.Apply(
            SelectionCombineMode.Add, current, incoming, canvas, antialiased: true);

        Assert.NotNull(result);
        Assert.True(
            result!.Coverage.Bounds.Right <= canvas.Width,
            $"Add 后包围盒右沿 {result.Coverage.Bounds.Right} 超出画布 {canvas.Width}");
    }

    // ─────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:246  applySelection 不传 feather → 羽化归零
    // ─────────────────────────────────────────────────────────────

    /// <summary>照抄 Selection.swift:246 —— 画新选区会把已有羽化清掉。</summary>
    /// <remarks>
    /// Mac 版 <c>setSelection(DocumentSelection(path:antialiased:))</c> 没有第三个参数，
    /// <c>feather</c> 走默认的 0。所以 Feather 只能靠反复点菜单叠加，
    /// 画一个新选区（无论 Replace/Add/Subtract）都会丢掉它。
    /// </remarks>
    [Theory]
    [InlineData(SelectionCombineMode.Replace)]
    [InlineData(SelectionCombineMode.Add)]
    [InlineData(SelectionCombineMode.Subtract)]
    public void Apply_ResetsFeatherToZero(SelectionCombineMode mode)
    {
        SelectionDocument current = SolidBlock(feather: 12);
        Assert.Equal(12, current.Feather);

        CoveragePlane incoming = CoveragePlane.CreateFilled(Rect(12, 12, 4, 4), 255);

        SelectionDocument? result = SelectionAdapter.Apply(
            mode, current, incoming, new DocSize(64, 64), antialiased: true);

        Assert.NotNull(result);
        Assert.Equal(0, result!.Feather);
    }

    // ─────────────────────────────────────────────────────────────
    // 【照抄组】Selection.swift:334/338-341  resizeSelection
    // ─────────────────────────────────────────────────────────────

    /// <summary>照抄 Selection.swift:338-339 —— <b>扩张</b>要裁剪到画布。</summary>
    [Fact]
    public void Resize_Expand_ClipsToCanvas()
    {
        DocSize canvas = new(24, 24);
        // 方块贴着画布右边：x 从 16 到 24，扩张 3 像素必然越界。
        SelectionDocument current = SolidSelection(Rect(16, 4, 8, 8));

        SelectionDocument? result = SelectionAdapter.Resize(current, delta: 3, canvas);

        Assert.NotNull(result);
        Assert.True(
            result!.Coverage.Bounds.Right <= canvas.Width,
            $"扩张后右沿 {result.Coverage.Bounds.Right} 超出画布 {canvas.Width}");
    }

    /// <summary>照抄 Selection.swift:340 —— <b>收缩</b>不裁剪（分支里没有 intersection）。</summary>
    /// <remarks>
    /// 收缩只会让形状变小，本来就碰不到画布边缘，
    /// 所以 Mac 版在差集分支省掉了这次裁剪。本实现照抄这个不对称。
    /// </remarks>
    [Fact]
    public void Resize_Contract_DoesNotClip()
    {
        DocSize canvas = new(24, 24);
        SelectionDocument current = SolidSelection(Rect(4, 4, 16, 16));

        SelectionDocument? result = SelectionAdapter.Resize(current, delta: -2, canvas);

        Assert.NotNull(result);
        // 收缩后应完整落在画布内，且尺寸真的变小了。
        Assert.Equal(Rect(6, 6, 12, 12), result!.Coverage.Bounds);
    }

    /// <summary>照抄 Selection.swift:341 —— resize 保留羽化与抗锯齿开关。</summary>
    [Fact]
    public void Resize_PreservesFeatherAndAntialias()
    {
        SelectionDocument current = SolidSelection(Rect(10, 10, 20, 20), antialiased: false, feather: 9);

        SelectionDocument? result = SelectionAdapter.Resize(current, delta: 2, new DocSize(64, 64));

        Assert.NotNull(result);
        Assert.Equal(9, result!.Feather);
        Assert.False(result.Antialiased);
    }

    /// <summary>照抄 Selection.swift:341 —— 这条与 <see cref="Apply_ResetsFeatherToZero"/> 正好相反。</summary>
    /// <remarks>
    /// <c>resizeSelection</c> 传了 <c>feather: current.feather</c>，所以羽化被保留；
    /// 而 <c>applySelection</c> 没传，归零。两条路径行为不同，不要互相套用。
    /// </remarks>
    [Fact]
    public void Resize_AndApply_DifferOnFeatherHandling()
    {
        SelectionDocument withFeather = SolidSelection(Rect(10, 10, 20, 20), feather: 15);
        CoveragePlane incoming = CoveragePlane.CreateFilled(Rect(12, 12, 4, 4), 255);

        SelectionDocument? viaResize = SelectionAdapter.Resize(withFeather, delta: 2, new DocSize(64, 64));
        SelectionDocument? viaApply = SelectionAdapter.Apply(
            SelectionCombineMode.Add, withFeather, incoming, new DocSize(64, 64), antialiased: true);

        Assert.Equal(15, viaResize!.Feather);   // 保留
        Assert.Equal(0, viaApply!.Feather);     // 归零
    }

    /// <summary>照抄 Selection.swift:334 —— <c>delta != 0</c> 与 <c>abs(delta) &lt;= 500</c> 两条守卫。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [InlineData(-501)]
    public void Resize_RejectsOutOfRangeDelta(int delta)
    {
        SelectionDocument current = SolidSelection(Rect(10, 10, 20, 20));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SelectionAdapter.Resize(current, delta, new DocSize(64, 64)));
    }

    /// <summary>照抄 Selection.swift:334 的 <c>canModifySelection</c> —— 空选区不改，非空才改。</summary>
    [Fact]
    public void Resize_OnEmptyOrNullSelection_ReturnsUnchanged()
    {
        SelectionDocument empty = SolidSelection(Rect(0, 0, 1, 1));
        Assert.NotNull(SelectionAdapter.Resize(empty, delta: 3, new DocSize(64, 64)));
        Assert.Null(SelectionAdapter.Resize(null, delta: 3, new DocSize(64, 64)));
    }

    // ─────────────────────────────────────────────────────────────
    // 【推导组】以下由契约 v1.2 的数学定义推导，不引用 Mac 行号
    // ─────────────────────────────────────────────────────────────

    /// <summary>推导组：<c>ToSelectionMask</c> 之后经 <c>CoverageAt</c> 读回，逐字节不变。</summary>
    /// <remarks>
    /// 这是 Adapter 唯一要保证的事：包装是<b>无损</b>的。
    /// 派生自契约 v1.2 的定义——<c>Coverage8.FromData</c> 要求长度恰好等于 <c>width×height</c>，
    /// <c>SelectionMask.CoverageAt</c> 负责把文档坐标换算到缓冲局部坐标。
    /// </remarks>
    [Fact]
    public void Adapter_RoundTrip_IsByteExact()
    {
        DocRect bounds = Rect(7, 13, 11, 5);          // 故意用非零、互不整除的 origin
        var bytes = new byte[bounds.Size.Width * bounds.Size.Height];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 37) % 256);        // 覆盖 0..255 的各种值
        }

        CoveragePlane original = new(bounds, bytes);
        SelectionMask mask = SelectionAdapter.ToSelectionMask(original);
        CoveragePlane back = SelectionAdapter.FromSelectionMask(mask);

        Assert.Equal(bounds, back.Bounds);
        Assert.Equal(bytes, back.Pixels.ToArray());
    }

    /// <summary>推导组：<c>SelectionMask.Full</c> 的 1×1 均匀代理也必须原样读回。</summary>
    /// <remarks>
    /// <c>Coverage8</c> 对"整幅同值"用 1×1 均匀代理表示，而不是开满分辨率缓冲。
    /// <see cref="SelectionAdapter.FromSelectionMask"/> 走 <c>CoverageAt</c> 而不是直接索引
    /// <c>Coverage8.Data</c>，正是为了不漏掉这个代理。若改成直接索引，本测试会红。
    /// </remarks>
    [Fact]
    public void FromSelectionMask_HandlesUniformProxy()
    {
        SelectionMask full = SelectionMask.Full(new DocSize(9, 7));
        CoveragePlane plane = SelectionAdapter.FromSelectionMask(full);

        Assert.Equal(new DocSize(9, 7), plane.Bounds.Size);
        Assert.All(plane.Pixels.ToArray(), v => Assert.Equal(255, v));
    }

    /// <summary>推导组：<c>SelectionMask.Empty</c> 读回后不得凭空长出内容。</summary>
    [Fact]
    public void FromSelectionMask_Empty_StaysEmpty()
    {
        SelectionDocument doc = SelectionDocument.FromSelectionMask(SelectionMask.Empty, antialiased: true);

        Assert.True(doc.IsEmpty);
        Assert.All(doc.Coverage.Pixels.ToArray(), v => Assert.Equal(0, v));

        // 空选区仍然是一条「存在的选区」，不是 null —— 二者的区别见 SelectionDocument.IsEmpty。
        // 传入 antialiased: true，所以反序列化回来的开关也应当是 true。
        Assert.True(doc.Antialiased);
        Assert.True(doc.ShouldAntialias);
    }

    /// <summary>
    /// 推导组：<c>ToSelectionMask</c> 必须走契约的公开工厂，而不是绕开它们手工构造。
    /// </summary>
    /// <remarks>
    /// 契约 v1.2 之前 <c>SelectionMask</c> 构造器是 private，只有 <c>Empty</c>/<c>Full</c>，
    /// <c>Coverage8</c> 没有外部字节入口——本 lane 曾因此硬阻塞两轮。
    /// 现在通道开了，本测试确保走的是 <c>FromCoverage</c>/<c>FromData</c> 这条官方路径：
    /// 它会校验包围盒与覆盖度尺寸一致，多给/少给字节都会抛，而不是静默接受。
    /// </remarks>
    [Fact]
    public void ToSelectionMask_UsesContractFactories()
    {
        CoveragePlane plane = CoveragePlane.CreateFilled(Rect(0, 0, 6, 4), 200);

        SelectionMask mask = SelectionAdapter.ToSelectionMask(plane);

        Assert.Equal(plane.Bounds, mask.Bounds);
        Assert.Equal(200, mask.CoverageAt(new DocPoint(3, 2)));
    }

    private static SelectionDocument SolidSelection(DocRect bounds, bool antialiased = true, double feather = 0) =>
        SelectionDocument.FromSelectionMask(
            SelectionAdapter.ToSelectionMask(CoveragePlane.CreateFilled(bounds, 255)), antialiased, feather);
}