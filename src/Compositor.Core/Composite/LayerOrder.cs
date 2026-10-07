namespace Compositor.Core.Composite;

/// <summary>图层树解析结果（<c>LayerOrder.Result</c> 的等价物）。</summary>
/// <param name="Order">
/// 全部图层与分组，按遍历序排列，<b>含不可见节点、含分组自身</b>，每个分组排在其内容之前。
/// </param>
/// <param name="Visible">其中真正显示的节点 id。</param>
/// <param name="Drawn">需要绘制的<b>叶子层</b> id（<b>不含分组</b>），<b>自底向上</b>。</param>
/// <remarks>
/// 三个集合的差异本身就是语义的一部分：<see cref="Order"/> 收所有节点，
/// <see cref="Visible"/> 收「自身或全部祖先可见」的节点，<see cref="Drawn"/> 再从中剔除分组。
/// 把三者合并成一个会丢掉「分组本身不可见 ⇒ 子孙不进 Drawn」这条传递规则。
/// </remarks>
public sealed record LayerOrderResult(
    IReadOnlyList<Guid> Order,
    IReadOnlySet<Guid> Visible,
    IReadOnlyList<Guid> Drawn);

/// <summary>图层遍历顺序求解。对应 Mac 的 <c>LayerOrder.resolve</c>（<c>LayerGroups.swift:119-140</c>）。</summary>
/// <remarks>
/// <para><b>索引 0 = 最底层。</b>Mac 的 <c>EditorSession.swift:67</c> 原文
/// <c>var layers: [ImageLayer] = [] // Bottom to top.</c>，
/// 所以合成循环<b>不反向迭代</b>，直接把绘制序喂进去。
/// 只有图层面板是顶在前（<c>LayerGroups.swift:242</c> 的 <c>topFirst: true</c>）——搞反会整个渲染颠倒。</para>
/// </remarks>
public static class LayerOrder
{
    /// <summary>
    /// 遍历深度上限。<c>depth &lt;= 64</c> 仍会访问，<c>65</c> 起<b>整棵子树被静默丢弃</b>。
    /// </summary>
    /// <remarks>
    /// 对应 <c>LayerGroups.swift:125</c> 的 <c>guard depth &lt;= 64 else { return }</c>。
    /// 注意它是 <c>return</c>（丢掉整个子树）而不是 <c>break</c>（只跳过这一层），
    /// 也<b>不抛异常</b> —— 这是一条静默截断规则，不是校验。
    /// </remarks>
    public const int MaxDepth = 64;

    /// <summary>按 <paramref name="layers"/> 的形状求解遍历顺序。</summary>
    /// <param name="layers">全部图层，<b>索引 0 是最底层</b>。</param>
    /// <returns>遍历结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers"/> 为 <see langword="null"/>。</exception>
    public static LayerOrderResult Resolve(IReadOnlyList<LayerNode> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        // Dictionary(grouping: nodes, by: \.parentID)（LayerGroups.swift:122）的等价物。
        //
        // 🔴 Swift 用的是 `Dictionary<UUID?, [Node]>`——`nil` 是合法的键，根层就是 `children[nil]`。
        // C# 不能这么写：`Dictionary<Guid?, _>` 违反 TKey 的 notnull 约束（CS8714），
        // 会把「根层」这一档静默折成「键永远命中不了」。因此把根层单独拆成一个列表，
        // 其余按父 id 分桶——与原字典**逐键等价**：
        //   · parentID == nil            → roots，visit(nil) 消费
        //   · parentID == <任意 Guid>     → byParent[该 id]，visit(node.id) 消费
        // 而 visit 传的第二个参数 `parent: UUID?` 只有两个来源：初始的 nil，和 `node.id`（永不为 nil）。
        // 没有第三种键，所以拆开不会产生新键或漏掉旧键。
        //
        // ⚠️ Guid.Empty 不是「无父」：Mac 的 UUID 没有空值概念，
        // 一个 parentID 恰好等于某图层的 Guid.Empty 在两侧都会正常命中同一个桶。
        // 这里 ParentId 是 Guid?，Guid.Empty 是非 null，落进 byParent —— 与 Mac 一致，不特殊处理。
        //
        // 🔴 桶内顺序必须是原数组升序：Swift 的 Dictionary(grouping:) 逐个 append，天然保序。
        // 手写循环把这条契约摆在明面上，不依赖读者记得 GroupBy 是稳定的。
        var roots = new List<LayerNode>();
        var byParent = new Dictionary<Guid, List<LayerNode>>(layers.Count);
        foreach (LayerNode layer in layers)
        {
            if (layer.ParentId is not Guid parentId)
            {
                roots.Add(layer);
                continue;
            }

            if (!byParent.TryGetValue(parentId, out List<LayerNode>? bucket))
            {
                bucket = new List<LayerNode>();
                byParent[parentId] = bucket;
            }

            bucket.Add(layer);
        }

        var order = new List<Guid>(layers.Count);
        var visible = new HashSet<Guid>();
        var drawn = new List<Guid>(layers.Count);

        void Visit(List<LayerNode> children, int depth, bool shown)
        {
            // guard depth <= 64 else { return }（LayerGroups.swift:125）
            // 注意它拦在取桶**之前**：整棵子树连 order 都不进，不是「这一层跳过、孩子照样画」。
            if (depth > MaxDepth)
            {
                return;
            }

            foreach (LayerNode node in children)
            {
                // shown: effective 逐层传递（:127）—— 分组不可见会一路传下去，
                // 于是它的子孙 effective 全是 false，一个像素都不画。
                bool effective = shown && node.IsVisible;

                // order.append 在 if 之外（:128）：不可见节点也进 order。
                order.Add(node.Id);

                if (effective)
                {
                    visible.Add(node.Id);
                    if (!node.IsGroup)
                    {
                        // 分组自身不进 drawn（:131）—— 它没有像素。
                        drawn.Add(node.Id);
                    }
                }

                if (node.IsGroup)
                {
                    // visit(node.id, depth + 1, shown: effective)（:133）。
                    // 括号里 `children[node.id] ?? []`：父 id 指向一个**不是分组**的图层时，
                    // 那个桶永远没人取（只有分组才会 visit 它的 id），整棵子树被丢弃。
                    if (byParent.TryGetValue(node.Id, out List<LayerNode>? inner))
                    {
                        Visit(inner, depth + 1, effective);
                    }
                }
            }
        }

        // visit(nil, depth: 0, shown: true)（:136）
        Visit(roots, 0, shown: true);

        return new LayerOrderResult(order, visible, drawn);
    }
}

/// <summary>父链查找表的一行：不透明度 + 父 id。</summary>
/// <param name="Opacity">该节点的不透明度。</param>
/// <param name="ParentId">该节点的父 id。<see langword="null"/> = 根层。</param>
/// <remarks>
/// 为什么不用 <c>(double, Guid?)</c> 元组：元组<b>写不进 XML 的 <c>cref</c></b>（编译器报 CS1584
/// 并级联 CS1658/CS1003 一串假语法错误），而 <c>LayerOpacity.Effective</c> 的引用必须能点。
/// 另有一条语义理由——名字能把「查找表里的祖先」和「要乘的祖先」区分开，
/// 前者可能根本不是分组。
/// </remarks>
public readonly record struct LayerOpacityEntry(double Opacity, Guid? ParentId);

/// <summary>有效不透明度。对应 Mac 的 <c>LayerOpacity.effective</c>（<c>LayerGroups.swift:53-63</c>）。</summary>
public static class LayerOpacity
{
    /// <summary>
    /// 父链相乘的跳数上限。<c>depth &lt; 64</c> 才继续，<b>最多相乘 64 个祖先</b>，超出静默截断。
    /// </summary>
    /// <remarks>
    /// 对应 <c>LayerGroups.swift:57</c> 的 <c>while ... depth &lt; 64 ...</c>。
    /// 🔴 它与 <see cref="LayerOrder.MaxDepth"/> 是<b>两条不同的规则</b>，差一：
    /// 遍历是 <c>depth &lt;= 64</c>（65 层丢弃子树），不透明度是 <c>depth &lt; 64</c>（最多 64 次相乘）。
    /// 写成一个常量用两处会悄悄改掉其中一条的行为。
    /// </remarks>
    public const int MaxHops = 64;

    /// <summary>
    /// 沿 parent 链把祖先的不透明度逐级相乘进 <paramref name="own"/>。
    /// </summary>
    /// <param name="own">图层自身的不透明度。</param>
    /// <param name="parentId">图层的父 id。<see langword="null"/> = 根层。</param>
    /// <param name="byId">
    /// id → (不透明度, 父 id) 的查找表。
    /// 🔴 键必须覆盖<b>全部图层</b>，不只是分组 —— Mac 的 <c>LayerGroups.swift:84</c>
    /// 变量名叫 <c>folders</c>，但它是用 <c>layers.lazy.map</c> 建的，即所有图层。
    /// 因此一个 <c>parentID</c> 指向非分组图层的层，那个图层的 opacity 仍会被相乘进来。
    /// </param>
    /// <returns>有效不透明度。<b>不做任何 clamp</b>，也不因结果为 0 而提前退出。</returns>
    /// <remarks>
    /// <para><b>这是 pass-through 语义</b>，源码注释原文（<c>LayerGroups.swift:49-52</c>）：
    /// <i>"A folder's opacity multiplies into everything inside it: a layer at 50% in a folder at 50%
    /// shows at 25%, while the layer itself still reads 50% in the panel.
    /// Folders are pass-through — what's inside is drawn straight onto what is below,
    /// never composited as a unit — so the folder's opacity is applied to each of those layers
    /// rather than to the folder as a whole."</i></para>
    /// <para>翻译成代码就是：<b>分组永不作为独立 surface 参与合成</b>，
    /// 它的不透明度是<b>逐层相乘</b>进去的。因此本方法返回的是一个<b>单层</b>的最终不透明度，
    /// 而不是「先各自合成、再整体乘分组不透明度」那套分组合成模型。</para>
    /// <para><b>什么都不跳过</b>：不检查 <c>IsVisible</c>、不看 <c>MaskSourceId</c>、
    /// 不区分调整层、不 clamp、不因 opacity 已为 0 而提前 break。
    /// 剔除全部发生在别处（见 <see cref="LayerOrder"/>）——
    /// 换句话说，<b>一个 opacity 为 0 的分组里的图层仍会参与合成，只是乘出来是 0</b>。</para>
    /// </remarks>
    public static double Effective(
        double own,
        Guid? parentId,
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId)
    {
        ArgumentNullException.ThrowIfNull(byId);

        double opacity = own;
        Guid? id = parentId;
        int depth = 0;

        // `while let current = id, depth < 64, let node = folder(current)`（LayerGroups.swift:57）
        // 三个条件是**并列的 while 条件**，任一不满足就整体退出，不是嵌套 if。
        // 等价成一个 C# while 的 && 链。
        while (id is Guid current && depth < MaxHops && byId.TryGetValue(current, out LayerOpacityEntry node))
        {
            opacity *= node.Opacity;
            id = node.ParentId;
            depth++;
        }

        return opacity;
    }

    /// <summary>单个图层的有效不透明度（<see cref="LayerNode.Opacity"/> 自身 + 全部祖先）。</summary>
    /// <param name="node">目标图层。</param>
    /// <param name="byId">由 <see cref="BuildLookup"/> 建立的查找表。</param>
    /// <returns>有效不透明度。</returns>
    /// <remarks>
    /// 等价于 Mac 的 <c>ImageLayer.effectiveOpacity(in:)</c>（<c>LayerGroups.swift:68-70</c>），
    /// 它做的也只是把 <c>$0.opacity</c> 与 <c>$0.parentID</c> 取出来喂给
    /// <see cref="Effective(double, Guid?, IReadOnlyDictionary{Guid, LayerOpacityEntry})"/>。
    /// <b>不做任何剔除</b>：分组、调整层、不可见图层一样算。
    /// </remarks>
    public static double Effective(LayerNode node, IReadOnlyDictionary<Guid, LayerOpacityEntry> byId)
    {
        ArgumentNullException.ThrowIfNull(node);

        return Effective(node.Opacity, node.ParentId, byId);
    }

    /// <summary>一次算出<b>所有</b>图层的有效不透明度。</summary>
    /// <param name="layers">全部图层。</param>
    /// <returns>图层 id → 有效不透明度。<b>含分组与不可见图层</b>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">存在<b>重复的图层 id</b>。</exception>
    /// <remarks>
    /// <para>对应 <c>CanvasDocument.effectiveOpacities</c>（<c>LayerGroups.swift:83-86</c>）。</para>
    /// <para>Mac 用的是 <c>folders.mapValues { ... }</c>——Swift 字典无序，
    /// 所以那边本来就不保证迭代序；本实现返回的也是字典，<b>不承诺顺序</b>，
    /// 需要顺序请用 <see cref="LayerOrderResult.Drawn"/>。</para>
    /// <para>🔴 结果<b>包含分组自己</b>：一个分组 G 的有效不透明度 = G.Opacity × G 的祖先，
    /// <b>不含 G 自身被相乘两次</b>。绘制时用的是<b>叶子层</b>的条目，
    /// 分组条目只在面板显示或调试时用得上。</para>
    /// <para>复杂度 <b>O(n × 跳数)</b>，最坏 O(64n)。Mac 版同构，
    /// 没有做「一次 DFS 顺带算出全链」的优化——1000 层 × 64 跳仍是 6.4 万次乘法，
    /// 不构成瓶颈，且保持与原实现逐字可比。</para>
    /// </remarks>
    public static IReadOnlyDictionary<Guid, double> EffectiveOpacities(IReadOnlyList<LayerNode> layers)
    {
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = BuildLookup(layers);

        var result = new Dictionary<Guid, double>(byId.Count);
        foreach (LayerNode layer in layers)
        {
            // 🔴 必须按 layers 的顺序逐个算，不能对 byId 做 mapValues：
            // 重复 id 已在 BuildLookup 里抛掉，所以此处 layers 的 id 与 byId 的键严格一一对应，
            // 不会静默覆盖。Mac 的 mapValues 是无序的，这一步不引入任何新语义。
            result[layer.Id] = Effective(layer, byId);
        }

        return result;
    }

    /// <summary>由图层列表建立
    /// <see cref="Effective(double, Guid?, IReadOnlyDictionary{Guid, LayerOpacityEntry})"/> 需要的查找表。</summary>
    /// <param name="layers">全部图层。</param>
    /// <returns>id → (不透明度, 父 id)。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layers"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">存在<b>重复的图层 id</b>。</exception>
    /// <remarks>
    /// <para>重复 id 在 Mac 侧是 <c>Dictionary(uniqueKeysWithValues:)</c> 的<b>运行时陷阱</b>，
    /// 这里改为显式抛 <see cref="ArgumentException"/>：静默取最后一个会让绘制顺序与不透明度
    /// 各走各的（一个按首次出现、一个按末次），产出一张说不清来源的图。</para>
    /// <para>🔴 <see cref="LayerNode.Opacity"/> 是 <see cref="float"/>，Mac 的
    /// <c>ImageLayer.opacity</c> 是 <c>Double</c>。本实现把 float 提升为 double 后参与相乘，
    /// 因此<b>输入值本身已带一次 float 舍入</b>，第 3 个及以后的乘积可能与 Mac 的 double 链差 1 ULP。
    /// 这是契约定的 <see cref="float"/> 带来的，<b>不是本方法的取舍</b>；
    /// 改它要动契约（<see cref="LayerNode"/> 全局），不在 M2 范围内。</para>
    /// </remarks>
    public static IReadOnlyDictionary<Guid, LayerOpacityEntry> BuildLookup(
        IReadOnlyList<LayerNode> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var byId = new Dictionary<Guid, LayerOpacityEntry>(layers.Count);
        foreach (LayerNode layer in layers)
        {
            if (!byId.TryAdd(layer.Id, new LayerOpacityEntry(layer.Opacity, layer.ParentId)))
            {
                throw new ArgumentException(
                    $"图层 id 重复：{layer.Id}。id 必须在文档内唯一，否则绘制顺序与不透明度会取到不同的图层。",
                    nameof(layers));
            }
        }

        return byId;
    }
}
