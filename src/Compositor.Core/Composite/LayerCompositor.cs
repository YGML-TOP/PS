namespace Compositor.Core.Composite;

/// <summary>一层参与合成的像素。尺寸必须与画布一致。</summary>
/// <param name="Id">所属图层 id。</param>
/// <param name="Pixels">
/// 已摆放到<b>画布坐标系</b>的预乘 RGBA8 缓冲。
/// 🔴 本方法<b>不做任何几何变换</b>，调用方必须先摆好。
/// </param>
/// <remarks>
/// <b>为什么这里不带 <c>BlendMode</c> 与 <c>Opacity</c>：</b>它们都取自
/// <see cref="LayerNode"/>，而不是取自像素。Mac 的 <c>drawLiveComposite</c>
/// （<c>LiveLayerMask.swift:181</c>）也是 <c>let mode = self.displayedBlendMode(for: layer)</c>
/// —— 模式来自图层记录，图像本身不知道自己的混合模式。
/// 让像素携带模式，就等于允许两条来源不一致，且没人能一眼看出哪条赢了。
/// </remarks>
public sealed record LayerSurface(Guid Id, PixelBuffer Pixels);

/// <summary>一次合成的结果。</summary>
/// <param name="Canvas">画布。<b>调用方不要复用这个实例</b>，它是本次新建的。</param>
/// <param name="Painted">实际参与混合的图层 id，<b>自底向上</b>。</param>
/// <param name="Skipped">
/// 在绘制序里但<b>没有像素</b>、被跳过的图层 id。
/// 🔴 这不是错误：调整层本来就没有栅格内容（Mac 侧对应
/// <c>guard let image = layer.asset?.image else { return }</c>，
/// <c>LiveLayerMask.swift:166</c>），它要等 <c>AdjustmentSurface</c> 的两段式渲染才生效。
/// </param>
public sealed record CompositeResult(
    PixelBuffer Canvas,
    IReadOnlyList<Guid> Painted,
    IReadOnlyList<Guid> Skipped);

/// <summary>
/// 合成器主干：按绘制序把各层依次混合到同一块画布上。
/// </summary>
/// <remarks>
/// <para><b>🔴 本类与 Mac 版的关系必须说清楚，否则会被误读成「已对齐」。</b>
/// Mac 的离线渲染 <c>drawLiveComposite</c>（<c>LiveLayerMask.swift:159-200</c>）
/// 是<b>驱动 Core Graphics 发 draw 调用</b>——真正混合像素的是闭源的 CG。
/// 本类是<b>CPU 逐像素</b>，机制完全不同。所以：</para>
/// <list type="bullet">
/// <item>✅ <b>可直译、已直译</b>的是「调度语义」：谁参与、按什么顺序、
/// 用什么系数（<see cref="LayerOrder"/>、<see cref="LayerOpacity"/>、本类）。</item>
/// <item>❌ <b>不可直译</b>的是「像素怎么混」——那是 <see cref="BlendOps"/> 按 W3C 手写的，
/// 与 CG 的关系<b>只有 Tier 2 黄金样本能回答</b>，目前一个样本都没有。</item>
/// </list>
///
/// <para><b>本批次刻意不做的事</b>（都不是忘了，是排在后面，且每一件都有依赖）：
/// <list type="bullet">
/// <item><b>剪贴蒙版</b>（活蒙版链两套机制，见 <c>LiveMaskRenderer:76-116</c>）。
/// 本批次一律 <c>maskCoverage = 1.0</c>，等价于「没有蒙版」。</item>
/// <item><b>分组蒙版作为几何裁剪</b>。Mac 用 <c>FolderMaskClip</c> 往 CGContext 上加 clip
/// （<c>LiveLayerMask.swift:194-199</c>），那是<b>几何裁剪</b>；
/// 本批的 <c>maskCoverage</c> 是<b>逐像素数值相乘</b>，两者不是一回事，不能互相冒充。</item>
/// <item><b>调整层的两段式渲染</b>（<c>AdjustmentSurface.draw</c>，
/// <c>LiveLayerMask.swift:160-163</c>）。调整层被如实记入 <see cref="CompositeResult.Skipped"/>。</item>
/// <item><b>几何变换</b>（<c>displayedTransform</c>）。输入必须已摆到画布坐标。</item>
/// <item><b>编辑期预览值</b>。<c>displayedBlendMode</c>（<c>LayerAppearance.swift:76-79</c>）
/// 只在拖拽预览命中活动图层时偏离存储值；离线渲染没有活动图层，
/// 所以本类直接用 <see cref="LayerNode.BlendMode"/>，这与 Mac 离线路径<b>是同一条</b>。</item>
/// </list></para>
///
/// <para><b>不透明度在 Mac 侧是单个数</b>：<c>LiveLayerMask.swift:167</c> 拿到
/// <c>layer.effectiveOpacity(in: records)</c> 就直接当 <c>opacity:</c> 传给
/// <c>LayerRenderer.draw</c>，蒙版走另一个参数。本类保持这个形状：
/// <c>opacity = Effective(...)</c>，<c>maskCoverage = 1.0</c>（本批次）。</para>
/// </remarks>
public static class LayerCompositor
{
    /// <summary>把所有图层合成到一块新画布上。</summary>
    /// <param name="layers">全部图层，<b>索引 0 是最底层</b>。</param>
    /// <param name="surfaces">图层 id → 像素。缺项视为「该层没有栅格内容」而跳过。</param>
    /// <param name="width">画布宽，&gt; 0。</param>
    /// <param name="height">画布高，&gt; 0。</param>
    /// <returns>合成结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers"/> 或 <paramref name="surfaces"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> 或 <paramref name="height"/> 非正。</exception>
    /// <exception cref="ArgumentException">
    /// 某个 <paramref name="surfaces"/> 条目的尺寸与画布不一致。
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// 某个图层的有效不透明度超出 0–1，即 <see cref="LayerNode.Opacity"/> 违反了契约。
    /// </exception>
    /// <remarks>
    /// <para><b>不反向迭代</b>：<see cref="LayerOrderResult.Drawn"/> 的索引 0 就是最底层，
    /// 照着往前走即可。依据 <c>EditorSession.swift:67</c> 的
    /// <c>var layers: [ImageLayer] = [] // Bottom to top.</c>。
    /// 写成 <c>for (int i = Drawn.Count - 1; i &gt;= 0; i--)</c> 会整个渲染颠倒，
    /// 而且<b>测试全绿</b>（层数少时只是颜色顺序看着不对），这是本方法最容易犯也最难发现的错。</para>
    ///
    /// <para><b>为什么不把不透明度静默 clamp 到 0–1</b>：Mac 侧最终由 CG 内部处理越界值，
    /// 画出来是什么没人验证过。本实现选择<b>当场拒绝并点名图层</b>，
    /// 因为 <see cref="LayerNode.Opacity"/> 的契约白纸黑字写着「有限且落在 0..1」，
    /// 越界说明<b>上游已经出错了</b>——静默 clamp 会把一个数据错误变成一个看不出来的画面偏差。
    /// 若 Tier 2 证明 Mac 的行为是 clamp，改这一处即可，范围就一行。</para>
    /// </remarks>
    public static CompositeResult Compose(
        IReadOnlyList<LayerNode> layers,
        IReadOnlyDictionary<Guid, LayerSurface> surfaces,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(surfaces);

        LayerOrderResult order = LayerOrder.Resolve(layers);
        IReadOnlyDictionary<Guid, LayerOpacityEntry> lookup = LayerOpacity.BuildLookup(layers);

        // AdjustmentSurface 画的离屏面就是全透明的（LiveLayerMask.swift:160-163）。
        // 预乘下全 0 天然等于「透明」，不需要任何特判。
        PixelBuffer canvas = PixelBuffer.Create(width, height);

        var painted = new List<Guid>(order.Drawn.Count);
        var skipped = new List<Guid>();
        Dictionary<Guid, LayerNode> byId = BuildIdIndex(layers);

        foreach (Guid id in order.Drawn)
        {
            if (!byId.TryGetValue(id, out LayerNode? layer))
            {
                // Drawn 是 Resolve 从 layers 自己推出来的，理论到不了这里。
                // 真到了说明 Resolve 与本方法拿到的不是同一份 layers —— 直接炸，别静默跳过。
                throw new InvalidOperationException(
                    $"绘制序里出现了不在图层列表中的 id：{id}。这是 LayerOrder 与合成器的输入不一致，不是用户数据问题。");
            }

            if (!surfaces.TryGetValue(id, out LayerSurface? surface))
            {
                // 调整层走的就是这条路：它在 Drawn 里（Resolve 不看 IsAdjustment），
                // 但没有栅格像素。对应 Mac 的 guard let image = layer.asset?.image else { return }。
                skipped.Add(id);
                continue;
            }

            PixelBuffer pixels = surface.Pixels
                ?? throw new InvalidOperationException($"图层 {id} 的 {nameof(LayerSurface.Pixels)} 为 null。");

            if (pixels.Width != width || pixels.Height != height)
            {
                throw new ArgumentException(
                    $"图层 {id} 的像素尺寸 {pixels.Width}×{pixels.Height} 与画布 {width}×{height} 不一致。"
                    + "本方法不做几何变换，调用方必须先把图层摆到画布坐标系。",
                    nameof(surfaces));
            }

            double opacity = LayerOpacity.Effective(layer, lookup);
            if (!double.IsFinite(opacity) || opacity < 0.0 || opacity > 1.0)
            {
                throw new InvalidOperationException(
                    $"图层 {id} 的有效不透明度是 {opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}，"
                    + $"越出 0–1。自身 Opacity = {layer.Opacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}，"
                    + "根因通常在图层的 Opacity 字段（契约要求有限且落在 0..1）。");
            }

            // maskCoverage 恒为 1.0：剪贴蒙版与分组蒙版都还没接（见类注释的「本批次不做」）。
            // 🔴 这一行是「蒙版机制未接入」的显式标记，不要为了省参数删掉它——
            // 删掉之后将来接蒙版的人不会知道这里曾经该有一个系数。
            BlendOps.Composite(canvas, pixels, layer.BlendMode, maskCoverage: 1.0, opacity: opacity);
            painted.Add(id);
        }

        return new CompositeResult(canvas, painted, skipped);
    }

    /// <summary>按 id 建索引。<b>重复 id 取最后一个</b>，理由见 <see cref="LayerOrderResult"/> 的文档。</summary>
    /// <param name="layers">全部图层。</param>
    /// <returns>id → 图层。</returns>
    /// <remarks>
    /// 🔴 这里用 <c>ToDictionary</c> 的「后者覆盖前者」语义，与 <see cref="LayerOpacity.BuildLookup"/>
    /// 的「重复即抛」<b>故意不同</b>，理由是两者服务的对象不同：
    /// <list type="bullet">
    /// <item><b>不透明度</b>是纯函数：同一个 id 有两份记录时，「取哪个」会直接改变像素值，
    /// 所以宁可炸。</item>
    /// <item><b>本索引</b>只在「Drawn 里的 id → 节点」这一步用，
    /// 而 Drawn 本身已经按遍历序把每个 id 至少记了一次，重复 id 早已在
    /// <see cref="LayerOpacity.BuildLookup"/> 那一步被挡住了
    /// （<see cref="Compose"/> 必定先调它）。所以这里根本走不到重复分支，
    /// 写成覆盖语义只是为了让代码不依赖调用顺序。</item>
    /// </list>
    /// 换句话说：<b>重复 id 在本方法里只有一个入口被把守，就是 BuildLookup。</b>
    /// </remarks>
    private static Dictionary<Guid, LayerNode> BuildIdIndex(IReadOnlyList<LayerNode> layers)
    {
        var byId = new Dictionary<Guid, LayerNode>(layers.Count);
        foreach (LayerNode layer in layers)
        {
            byId[layer.Id] = layer;
        }

        return byId;
    }
}