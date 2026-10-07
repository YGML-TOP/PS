using Compositor.Adjust;
using Compositor.Core;
using Compositor.Core.Composite;
using Compositor.Core.Pixels;

namespace Compositor.MaskEffects;

/// <summary>一次渲染中参与活蒙版解析的图层，按<b>自上而下</b>顺序排列。</summary>
/// <param name="Id">图层 id。</param>
/// <param name="SourceId">该图层的蒙版来源图层 id；<see langword="null"/> 表示无来源蒙版。</param>
/// <param name="ParentId">父图层（分组）id；<see langword="null"/> 表示顶层。</param>
/// <param name="Blend">该图层的混合模式。</param>
/// <param name="Adjustment">该图层的调整层参数；<see langword="null"/> 表示像素层。</param>
public readonly record struct MaskLayer(Guid Id, Guid? SourceId, Guid? ParentId, BlendMode Blend, LayerAdjustment? Adjustment);

/// <summary>
/// 活蒙版渲染器。决定哪些图层构成一个剪贴栈，以及栈怎么合成。
/// </summary>
/// <remarks>
/// <para>移植自 <c>Rendering/LiveMaskRenderer.swift</c>（170 行）。</para>
/// <para><b>覆盖度的定义</b>（源文件 <c>:5-6</c>）：用<b>源 alpha</b>，包含它自己的蒙版，
/// 与源的可见性和颜色无关。只为当前被剪贴的区域分配缓冲。</para>
///
/// <para>🔴 <b>两套并存的机制，最容易做错的地方</b>：
/// <list type="number">
/// <item><b>成栈判定</b>（<c>:78-92</c>）——剪贴栈<b>共享 base 的 alpha</b>，
/// 而不是把那个 alpha 盖在自身之上。其他依赖链接（分组端点等）保持各自的独立蒙版行为。</item>
/// <item><b>成栈的连续性</b>——只有<b>紧邻且连续</b>的子层才算同一个栈。
/// 源文件 <c>:84</c> 写的是 <c>guard … else { break }</c>，<b>是 <c>break</c> 不是 <c>continue</c></b>：
/// 一旦某个子层不满足条件，<b>后面所有层都停止收集</b>。写成 <c>continue</c> 会让
/// 「A 被 B 剪贴，B 被 D 剪贴，中间夹一个无关的 C」错误地成栈。</item>
/// </list></para>
///
/// <para>🔴 <b>预乘不变量（铁律 2）</b>（<c>:105-116</c>）：栈合成走
/// <b>抽 alpha → 反预乘 → 画子层 → 恢复预乘</b>四步。
/// 不抽 alpha 就画子层，子层会把自己的 alpha 也算进去；
/// 不反预乘就混合，颜色会偏；不恢复预乘，最后混合的输入就不是预乘值。</para>
///
/// <para>🔴 <b>调整层在栈里的两种身份</b>——<b>看着矛盾，其实不是</b>：
/// <list type="bullet">
/// <item><c>:81</c>：<b>调整层不能当 base</b>（成栈判定时 <c>adjustment(base) == nil</c> 是前置条件）。</item>
/// <item><c>:113</c>：<b>调整层可以当栈内子层</b>（收集子层后逐个绘制时先判 <c>adjustment(child)</c>）。</item>
/// </list>
/// 前者说的是「<b>栈的基底</b>」，后者说的是「<b>栈里的成员</b>」——两个不同的位置。
/// 实现时不要把它们合并成一条规则。</para>
/// </remarks>
public sealed class LiveMaskRenderer
{
    /// <summary>剪贴栈的基底线数上限（源文件 <c>:156</c> 的 <c>visiting.count &lt; 256</c>）。</summary>
    public const int MaxStackDepth = 256;

    /// <summary>单个中间表面的像素数上限，防止大画布把内存吃光。</summary>
    /// <remarks>
    /// 对应源文件 <c>:30</c> 的 <c>DocumentLimits.maxSurfaceExtent</c>。
    /// <b>🔴 契约层目前没有 <c>DocumentLimits</c> 类型</b>，这是本项目的一个缺口，
    /// 已登记在交付报告里。此处先取一个保守常量。
    /// </remarks>
    public const long MaxSurfaceExtent = 64L * 1024 * 1024;

    private readonly List<MaskLayer> _layers;
    private readonly Dictionary<Guid, MaskLayer> _byId = [];

    /// <summary>已并入某个栈的图层（源文件 <c>stacked</c>）。</summary>
    private readonly HashSet<Guid> _stacked = [];

    /// <summary>base 图层 id → 其栈内子层序列（源文件 <c>stacks</c>）。</summary>
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _stacks = [];

    /// <summary>base 图层 id → 栈合成所用的混合模式（源文件 <c>stackModes</c>）。</summary>
    private readonly Dictionary<Guid, BlendMode> _stackModes = [];

    /// <summary>缓存的覆盖度（源文件 <c>cache</c>）。</summary>
    private readonly Dictionary<Guid, Coverage8> _coverageCache = [];

    /// <summary>正在解析中的图层，用于检测自引用与环（源文件 <c>visiting</c>）。</summary>
    private readonly HashSet<Guid> _visiting = [];

    /// <summary>各图层的混合模式（源文件 <c>blendMode</c>）。</summary>
    private readonly Dictionary<Guid, BlendMode> _blendModes = [];

    /// <summary>渲染区域（文档坐标），已取整（源文件 <c>bounds.integral</c>）。</summary>
    public DocRect Bounds { get; }

    /// <summary>每单位 <see cref="Bounds"/> 对应的像素数（源文件 <c>resolution</c>）。</summary>
    public double Resolution { get; set; } = 1.0;

    /// <summary>
    /// 绘制单个图层自身内容的回调（源文件 <c>drawOwn</c>）。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>契约：第二个参数是「蒙版门限覆盖度」，<see langword="null"/> 表示无蒙版。</b>
    /// 回调写入的每个像素必须<b>按该门限加权</b>（等价于 source-over 一个乘了覆盖度的图层）。
    /// <para>之所以不靠目标缓冲的初始 alpha 承载门限：那会让「无蒙版」与
    /// 「蒙版全黑」两种情形无法区分（缓冲都是全 0），回调无从判断该不该加权。</para>
    /// <para>源文件用 <c>CGContext.clip(to:mask:)</c> 表达同一件事，裁剪在绘制<b>之前</b>生效。</para>
    /// </remarks>
    public Action<Guid, PixelBuffer, Coverage8?> DrawOwn { get; }

    /// <summary>读某图层的混合模式。</summary>
    public Func<Guid, BlendMode> BlendModeOf { get; }

    /// <summary>读某图层的调整层参数。</summary>
    public Func<Guid, LayerAdjustment?> AdjustmentOf { get; }

    /// <summary>读某图层的来源蒙版图层 id。</summary>
    public Func<Guid, Guid?> SourceOf { get; }

    /// <summary>调整层的不透明度（源文件 <c>adjustmentOpacity</c>）。</summary>
    public Func<Guid, double> AdjustmentOpacityOf { get; } = _ => 1.0;

    /// <summary>调整层渲染时的取样区域（源文件 <c>adjustmentRegion</c>），供颗粒等固定图案。</summary>
    public Func<DocRect, DocRect> AdjustmentRegionOf { get; } = r => r;

    /// <summary>
    /// 创建渲染器。
    /// </summary>
    /// <param name="bounds">渲染区域（文档坐标），内部取整为像素对齐。</param>
    /// <param name="layers">参与解析的图层，自上而下排列。</param>
    /// <param name="drawOwn">绘制单层自身内容的回调，第二个参数是蒙版门限（无蒙版时为 <see langword="null"/>）。</param>
    public LiveMaskRenderer(DocRect bounds, IReadOnlyList<MaskLayer> layers, Action<Guid, PixelBuffer, Coverage8?> drawOwn)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(drawOwn);

        Bounds = bounds.Integral;
        _layers = [.. layers];
        DrawOwn = drawOwn;

        foreach (var l in _layers)
        {
            _byId[l.Id] = l;
            _blendModes[l.Id] = l.Blend;
        }

        AdjustmentOf = id => _byId.TryGetValue(id, out var l) ? l.Adjustment : null;
        BlendModeOf = id => _blendModes.TryGetValue(id, out var m) ? m : BlendMode.Normal;
        SourceOf = id => _byId.TryGetValue(id, out var l) ? l.SourceId : null;
    }

    /// <summary>
    /// 判定剪贴栈。必须在渲染前调用一次。
    /// </summary>
    /// <remarks>
    /// 移植自 <c>:78-92</c>。三条前置条件缺一不可：
    /// <list type="number">
    /// <item>base <b>没有</b>来源蒙版（<c>source(base) == nil</c>）；</item>
    /// <item>base <b>不是</b>调整层（<c>adjustment(base) == nil</c>）；</item>
    /// <item>子层必须<b>连续</b>满足「来源是 base」且「父分组相同」。</item>
    /// </list>
    /// </remarks>
    public void PrepareStacks()
    {
        for (int index = 0; index < _layers.Count; index++)
        {
            var baseLayer = _layers[index];

            // 🔴 调整层不能当 base（:81）。它可以作为栈内子层——那在 Render 里另行处理。
            if (baseLayer.SourceId is not null) continue;
            if (baseLayer.Adjustment is not null) continue;

            var children = new List<Guid>();
            for (int k = index + 1; k < _layers.Count; k++)
            {
                var child = _layers[k];
                // 🔴 :84 是 break 不是 continue —— 不连续就整个停止收集。
                if (child.SourceId != baseLayer.Id) break;
                if (child.ParentId != baseLayer.ParentId) break;
                children.Add(child.Id);
            }

            if (children.Count == 0) continue;

            _stacks[baseLayer.Id] = children;
            _stackModes[baseLayer.Id] = baseLayer.Blend;
            foreach (var c in children) _stacked.Add(c);
        }
    }

    /// <summary>
    /// 把某个图层的合成结果画进 <paramref name="target"/>。
    /// </summary>
    /// <param name="id">图层 id。</param>
    /// <param name="target">目标缓冲，尺寸须与 <see cref="TryGetSurfaceSize"/> 一致。</param>
    public void Render(Guid id, PixelBuffer target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_stacked.Contains(id)) return;

        var layer = Lookup(id);

        // 调整层：只有「不是别人的 base」时才应用（:95-97）。
        if (layer.Adjustment is not null)
        {
            if (layer.SourceId is null) ApplyAdjustment(id, target);
            return;
        }

        if (!_stacks.TryGetValue(id, out var children) || !TryGetSurfaceSize(out int w, out int h))
        {
            // 表面超限时退回到直接绘制（:102-103）。
            if (_stacks.TryGetValue(id, out var stale)) foreach (var c in stale) _stacked.Remove(c);
            DrawWithMask(id, target);
            return;
        }

        var group = PixelBuffer.Create(w, h);
        // 栈内成员直接绘制，不带各自蒙版门限：源文件 :107 / :114 都是裸 drawOwn，
        // 因为「被谁剪贴」这件事在成栈判定里已经表达了（子层有 source → 已并入 base 的栈）。
        DrawOwn(id, group, null);

        // 🔴 铁律 2 四步（:108-116）：抽 alpha → 反预乘 → 画子层 → 恢复预乘。
        var coverage = new byte[w * h];
        BrushPixels.ExtractAlpha(group.Raw, group.Stride, coverage, w, w, h);
        BrushPixels.UnpremultiplyOpaque(group.Raw, group.Stride, w, h);

        foreach (var child in children)
        {
            // 🔴 调整层可以作为栈内子层（:113）——这是与「不能当 base」不同的位置。
            if (AdjustmentOf(child) is not null) ApplyAdjustment(child, group);
            else DrawOwn(child, group, null);
        }

        BrushPixels.RestoreAlpha(group.Raw, group.Stride, coverage, w, w, h);

        var mode = _stackModes.TryGetValue(id, out var m) ? m : BlendMode.Normal;
        BlendOps.Composite(target, group, mode);
    }

    /// <summary>
    /// 算某个来源图层的覆盖度：抽它（含自己的蒙版）的 alpha，缓存复用。
    /// </summary>
    /// <param name="id">来源图层 id。</param>
    /// <returns>8 位覆盖度；自引用、超深或表面过大时返回 <see langword="null"/>。</returns>
    /// <remarks>移植自 <c>:154-169</c>。<c>visiting</c> 同时防自引用与链长超 256。</remarks>
    public Coverage8? CoverageOf(Guid id)
    {
        if (_coverageCache.TryGetValue(id, out var cached)) return cached;

        // 🔴 自引用与环在这里被挡住：已在解析中 → 直接失败。
        if (_visiting.Contains(id)) return null;
        if (_visiting.Count >= MaxStackDepth) return null;
        if (!TryGetSurfaceSize(out int w, out int h)) return null;

        _visiting.Add(id);
        try
        {
            var pixels = PixelBuffer.Create(w, h);
            var gray = new byte[w * h];
            Render(id, pixels);
            BrushPixels.ExtractAlpha(pixels.Raw, pixels.Stride, gray, w, w, h);

            var coverage = Coverage8.FromData(gray, w, h);
            _coverageCache[id] = coverage;
            return coverage;
        }
        finally
        {
            _visiting.Remove(id);
        }
    }

    /// <summary>栈里的子层序列；该图层不是 base 时返回空。</summary>
    /// <param name="id">图层 id。</param>
    public IReadOnlyList<Guid> ChildrenOf(Guid id)
        => _stacks.TryGetValue(id, out var c) ? c : [];

    /// <summary>该图层是否已被并入某个栈（并入者不再单独绘制）。</summary>
    /// <param name="id">图层 id。</param>
    public bool IsStacked(Guid id) => _stacked.Contains(id);

    /// <summary>栈合成所用的混合模式；不是栈时为 <see cref="BlendMode.Normal"/>。</summary>
    /// <param name="id">base 图层 id。</param>
    public BlendMode StackModeOf(Guid id)
        => _stackModes.TryGetValue(id, out var m) ? m : BlendMode.Normal;

    /// <summary>中间表面的像素尺寸。超出 <see cref="MaxSurfaceExtent"/> 时返回 <see langword="false"/>。</summary>
    private bool TryGetSurfaceSize(out int w, out int h)
    {
        double rx = Bounds.Size.Width * Resolution;
        double ry = Bounds.Size.Height * Resolution;
        w = (int)Math.Round(rx, MidpointRounding.AwayFromZero);
        h = (int)Math.Round(ry, MidpointRounding.AwayFromZero);
        if (w <= 0 || h <= 0) return false;
        if ((long)w * h > MaxSurfaceExtent) return false;
        return true;
    }

    /// <summary>
    /// 先按来源蒙版裁剪，再绘制自身内容（源文件 <c>draw</c>，<c>:140-153</c>）。
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>顺序至关重要</b>：源文件用 <c>context.clip(to:mask:)</c> 裁剪
    /// <b>上下文</b>，因此 <c>drawOwn</c> 落在裁剪区外时<b>根本不产生像素</b>。
    /// 它<b>不是</b>「先画再按覆盖度乘 alpha」——那两件事不等价：
    /// 后者会让图层在透明区域仍写出像素，把外发散的像素混进合成结果。</para>
    /// <para>这里改为把覆盖度<b>显式传给回调</b>，由回调按门限加权。
    /// 不预置目标缓冲的 alpha：那会让「无蒙版」与「蒙版全黑」无法区分。</para>
    /// <para>coverage 使用<b>源 alpha</b>（含它自己的蒙版），与可见性和颜色无关。</para>
    /// </remarks>
    private void DrawWithMask(Guid id, PixelBuffer target)
    {
        var layer = Lookup(id);
        if (layer.SourceId is not { } sourceId)
        {
            DrawOwn(id, target, null);
            return;
        }

        // 🔴 源文件 :144 是 guard let … else { return } —— 覆盖度算不出来（自引用、成环、
        // 链长超限、表面超限）时**整个图层不画**，不是「画出未遮罩的样子」。
        // 写成软失败会让环被悄悄绕过，画出一张不该存在的图。
        if (CoverageOf(sourceId) is not { } coverage)
        {
            target.Raw.Clear();
            return;
        }

        DrawOwn(id, target, coverage);
    }

    /// <summary>应用调整层（源文件 <c>adjust</c>，<c>:33-75</c>）。</summary>
    private void ApplyAdjustment(Guid id, PixelBuffer target)
    {
        var settings = AdjustmentOf(id);
        if (settings is null) return;

        var original = target.Clone();
        var region = AdjustmentRegionOf(Bounds);
        double unitsPerPixel = Bounds.Size.Width / Math.Max(1, target.Width);

        AdjustmentApplier.Apply(target.Raw, target.Width, target.Height, target.Stride,
            settings, region.Origin.X, region.Origin.Y, unitsPerPixel);

        var mode = BlendModeOf(id);
        if (mode != BlendMode.Normal)
        {
            // 🔴 在<b>满覆盖</b>下混合颜色，再把原 alpha 还原回去。
            // 两个半透明副本做 source-over 会让软边变厚（:37-38）。
            var coverage = new byte[target.Width * target.Height];
            BrushPixels.ExtractAlpha(original.Raw, original.Stride, coverage, target.Width, target.Width, target.Height);
            BrushPixels.UnpremultiplyOpaque(original.Raw, original.Stride, target.Width, target.Height);
            BrushPixels.UnpremultiplyOpaque(target.Raw, target.Stride, target.Width, target.Height);

            BlendOps.Composite(original, target, mode);

            BrushPixels.RestoreAlpha(original.Raw, original.Stride, coverage, target.Width, target.Width, target.Height);
            original.Raw.CopyTo(target.Raw);
        }

        double opacity = AdjustmentOpacityOf(id);
        if (opacity < 1.0)
        {
            var k = (byte)Math.Clamp(Math.Round(opacity * 255.0, MidpointRounding.AwayFromZero), 0.0, 255.0);
            var raw = target.Raw;
            for (int i = 0; i < raw.Length; i += 4)
            {
                raw[i] = (byte)((raw[i] * k + 127) / 255);
                raw[i + 1] = (byte)((raw[i + 1] * k + 127) / 255);
                raw[i + 2] = (byte)((raw[i + 2] * k + 127) / 255);
            }
        }
    }

    private MaskLayer Lookup(Guid id)
        => _byId.TryGetValue(id, out var l) ? l : new MaskLayer(id, null, null, BlendMode.Normal, null);
}