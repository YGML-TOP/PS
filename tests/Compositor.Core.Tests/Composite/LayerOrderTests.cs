using Compositor.Core;
using Compositor.Core.Composite;
using Xunit;

namespace Compositor.Core.Tests.Composite;

/// <summary>
/// 图层树遍历（<c>LayerOrder.resolve</c>）与有效不透明度（<c>LayerOpacity.effective</c>）的测试。
/// </summary>
/// <remarks>
/// <para><b>🔴 本文件是「自写 smoke」，不是 Tier 1 照抄基准。</b>
/// <c>CompositorTests/</c> 里没有针对这两个函数的断言（它们是 Mac 内部辅助类型，
/// 只有 <c>EditorCanvas</c> 的渲染测试间接覆盖），所以本文件的期望值<b>全部手算</b>，
/// 算式写在每条断言的注释里，没有一条是「先跑一遍实现再抄输出」。</para>
///
/// <para><b>本文件能证明什么、不能证明什么</b>：
/// <list type="bullet">
/// <item>✅ 能证明：本实现<b>逐字复刻了 <c>LayerGroups.swift:49-140</c> 的控制流</b>，
/// 包括那三条没人会主动想到的静默截断规则。</item>
/// <item>❌ <b>不能</b>证明与 Mac 版逐像素一致 —— 那是 Tier 2 黄金样本的事，目前一个样本都没有。
/// 本文件全绿只说明「按源码推导的规格基准成立」，<b>不是</b>「与 Mac 输出对上了」。</item>
/// </list></para>
///
/// <para><b>三条静默规则（本文件的主要价值就在把它们钉死）</b>：
/// <list type="number">
/// <item><b>不可见节点也进 <c>Order</c></b> —— <c>order.append</c> 在 <c>if effective</c> 之外
/// （<c>LayerGroups.swift:128</c>）。把 <c>Order</c> 当成「可见的东西」用会直接画错。</item>
/// <item><b>深度 &gt; 64 丢整棵子树</b> —— 是 <c>return</c> 不是 <c>break</c>，<b>且不抛异常</b>
/// （<c>LayerGroups.swift:125</c>）。</item>
/// <item><b>父链查找表覆盖全部图层，不只是分组</b> —— 变量名叫 <c>folders</c>，
/// 但它是用 <c>layers.lazy.map</c> 建的（<c>LayerGroups.swift:84</c>）。</item>
/// </list></para>
///
/// <para><b>🔴 两条深度上限差一，不能共用常量</b>：遍历是 <c>depth &lt;= 64</c>（65 层可用），
/// 不透明度是 <c>depth &lt; 64</c>（只乘 64 个祖先）。下面
/// <see cref="遍历上限与不透明度上限差一"/> 专门测这个差一。</para>
/// </remarks>
public sealed class LayerOrderTests
{
    // ══════════════════════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>造一个普通像素层。<paramref name="opacity"/> 刻意用 0.5f 这类精确值。</summary>
    private static LayerNode Leaf(Guid id, Guid? parent = null, bool visible = true, float opacity = 1.0f) =>
        new LayerNode
        {
            Id = id, Transform = new LayerTransform(),
            IsGroup = false, ParentId = parent, IsVisible = visible, Opacity = opacity,
        };

    /// <summary>造一个分组。<paramref name="opacity"/> 参与 pass-through 相乘。</summary>
    private static LayerNode Group(Guid id, Guid? parent = null, bool visible = true, float opacity = 1.0f) =>
        new LayerNode
        {
            Id = id, Transform = new LayerTransform(),
            IsGroup = true, ParentId = parent, IsVisible = visible, Opacity = opacity,
        };

    /// <summary>
    /// 造 <paramref name="count"/> 个从根串到底的分组：g1 在根，g2 在 g1 里 ……
    /// 返回下标 0 对应 g1。
    /// </summary>
    private static LayerNode[] NestedGroups(Guid[] ids)
    {
        LayerNode[] result = new LayerNode[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            result[i] = Group(ids[i], parent: i == 0 ? null : ids[i - 1]);
        }

        return result;
    }

    /// <summary>
    /// 造一条「从最深往上爬」的分组链，返回下标 0 是<b>最深</b>那一层（离叶子最近）。
    /// </summary>
    /// <remarks>
    /// 也就是 <c>ids[0].parentID = ids[1]</c>、…、<c>ids[n-1].parentID = null</c>。
    /// 方向必须这样，因为 <c>LayerOpacity.Effective</c> 是<b>往上</b>爬的
    /// （<c>id = node.ParentId</c>），所以喂给 <c>parentId</c> 形参的必须是<b>最近</b>那层。
    /// 喂成根层会只乘一次就被 null 截断，测出来是 0.5 而不是 2^-64。
    /// </remarks>
    private static LayerNode[] AscendingGroups(Guid[] ids, float opacity = 0.5f)
    {
        LayerNode[] result = new LayerNode[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            result[i] = Group(ids[i], parent: i == ids.Length - 1 ? null : ids[i + 1], opacity: opacity);
        }

        return result;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  一、LayerOrder.Resolve —— 基本形状
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>空文档 → 三个集合全空，且不是 null。</summary>
    [Fact]
    public void Resolve_空图层列表_三个集合全空()
    {
        LayerOrderResult r = LayerOrder.Resolve(Array.Empty<LayerNode>());

        Assert.Empty(r.Order);
        Assert.Empty(r.Visible);
        Assert.Empty(r.Drawn);
    }

    /// <summary>
    /// 三个平铺层全部可见 → 顺序<b>就是数组原序</b>，索引 0 是最底层。
    /// </summary>
    /// <remarks>
    /// 依据 <c>EditorSession.swift:67</c>：<c>var layers: [ImageLayer] = [] // Bottom to top.</c>
    /// —— 合成循环<b>不反向迭代</b>，直接把 <c>renderLayers</c> 喂进去。
    /// 期望：A B C（不是 C B A，也不是按名字排序）。
    /// </remarks>
    [Fact]
    public void Resolve_三个平铺可见层_顺序即数组原序_索引0为最底层()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Leaf(a), Leaf(b), Leaf(c) });

        Assert.Equal(new[] { a, b, c }, r.Order);
        Assert.Equal(new[] { a, b, c }, r.Drawn);
        Assert.Equal(3, r.Visible.Count);
    }

    /// <summary>
    /// 🔴 不可见层<b>仍然进 Order</b>，只是不进 Visible / Drawn。
    /// </summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:128</c> 的 <c>order.append(node.id)</c> 在 <c>if effective</c>
    /// <b>之外</b>（:129）。这条若实现成「只 append 可见的」，Order 就退化成了 Visible 的别名，
    /// 而图层面板要用 Order 来展示整棵树 —— 隐藏的层必须仍列在树上。
    /// 期望：Order = [A, B]，Visible = {B}，Drawn = [B]。
    /// </remarks>
    [Fact]
    public void Resolve_不可见层仍进Order_但不进Visible与Drawn()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Leaf(a, visible: false), Leaf(b) });

        Assert.Equal(new[] { a, b }, r.Order);
        Assert.Equal(new[] { b }, r.Drawn);
        Assert.DoesNotContain(a, r.Visible);
        Assert.Contains(b, r.Visible);
    }

    /// <summary>分组自身进 Order 与 Visible，但<b>不进 Drawn</b>（它没有像素）。</summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:131</c> 的 <c>if !node.isGroup { drawn.append(node.id) }</c>。
    /// 期望：Order = [G, leaf]，Visible = {G, leaf}，Drawn = [leaf]。
    /// </remarks>
    [Fact]
    public void Resolve_分组进Order与Visible_但分组自身不进Drawn()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Group(g), Leaf(leaf, parent: g) });

        Assert.Equal(new[] { g, leaf }, r.Order);
        Assert.Equal(new[] { leaf }, r.Drawn);
        Assert.Contains(g, r.Visible);
    }

    /// <summary>分组排在它的内容之前（深度优先先序）。</summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:128</c> 先 append 自身、<c>:133</c> 再递归子桶。
    /// 期望：G1, L1, G2, L2 —— L1 在 G2 之前，说明 G1 的整棵子树被<b>完整走完</b>
    /// 才开始 G2，而不是「所有分组先、所有叶子后」。
    /// </remarks>
    [Fact]
    public void Resolve_分组排在其内容之前_且子树是深度优先()
    {
        Guid g1 = Guid.NewGuid(), g2 = Guid.NewGuid();
        Guid l1 = Guid.NewGuid(), l2 = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[]
        {
            Group(g1), Group(g2), Leaf(l1, parent: g1), Leaf(l2, parent: g2),
        });

        Assert.Equal(new[] { g1, l1, g2, l2 }, r.Order);
        Assert.Equal(new[] { l1, l2 }, r.Drawn);
    }

    /// <summary>
    /// 数组顺序与树顺序<b>不一致</b>时，树顺序说了算。
    /// </summary>
    /// <remarks>
    /// 数组是 [leaf, G, other]，但 leaf 的父是 G。桶内保持<b>数组升序</b>
    /// （Swift <c>Dictionary(grouping:)</c> 逐个 append，天然保序），
    /// 而 roots 桶只有 G 和 other。
    /// 期望：先 roots 桶 [G, other] 里的 G → 立刻递归它的桶 [leaf] → 然后才是 other。
    /// 即 Order = [G, leaf, other]。若实现成「先收集全部父再收集子」，
    /// 会得到 [G, other, leaf] —— 绘制时 other 会在 leaf 之上，顺序反了。
    /// </remarks>
    [Fact]
    public void Resolve_数组序与树序不一致时按树序_桶内保持数组升序()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid(), other = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Leaf(leaf, parent: g), Group(g), Leaf(other) });

        Assert.Equal(new[] { g, leaf, other }, r.Order);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  二、LayerOrder.Resolve —— 不可见的传递性
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 分组不可见 ⇒ 整棵子树的子孙全都不显示，<b>即使子孙自己 IsVisible = true</b>。
    /// </summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:127</c> 的 <c>let effective = shown &amp;&amp; node.isVisible</c>
    /// 加 <c>:133</c> 的 <c>shown: effective</c> —— shown 沿树<b>传下去</b>。
    /// 期望：Order 仍是 [G, leaf]（不可见也进 Order），Visible 与 Drawn 都空。
    /// </remarks>
    [Fact]
    public void Resolve_分组不可见_子孙即使自身可见也不再显示()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Group(g, visible: false), Leaf(leaf, parent: g) });

        Assert.Equal(new[] { g, leaf }, r.Order);
        Assert.Empty(r.Visible);
        Assert.Empty(r.Drawn);
    }

    /// <summary>三层嵌套，最外层不可见 → 最内层叶子不可见。</summary>
    /// <remarks>同上，shown 逐层传递。期望 Visible/Drawn 全空，Order 仍收全部 3 层。</remarks>
    [Fact]
    public void Resolve_三层嵌套_最外层不可见_则最内层叶子也不可见()
    {
        Guid g1 = Guid.NewGuid(), g2 = Guid.NewGuid(), leaf = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[]
        {
            Group(g1, visible: false),
            Group(g2, parent: g1),
            Leaf(leaf, parent: g2),
        });

        Assert.Equal(new[] { g1, g2, leaf }, r.Order);
        Assert.Empty(r.Visible);
        Assert.Empty(r.Drawn);
    }

    /// <summary>中间层不可见 → 只有它自己和它的子孙被排除，同层的兄弟照常显示。</summary>
    /// <remarks>
    /// 结构：G1(可见) ├ G2(不可见) ├ leafA
    ///                      └ leafB(与 G2 同父)
    /// 期望：Drawn = [leafB]。这排除了两种常见错实现：
    /// ① 把 shown 做成全局开关（一关全关，leafB 也丢）；
    /// ② 只看自身 IsVisible（leafA 会被画出来）。
    /// </remarks>
    [Fact]
    public void Resolve_中间层不可见_只排除它自己的子树_兄弟照常()
    {
        Guid g1 = Guid.NewGuid(), g2 = Guid.NewGuid();
        Guid leafA = Guid.NewGuid(), leafB = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[]
        {
            Group(g1), Group(g2, parent: g1, visible: false),
            Leaf(leafA, parent: g2), Leaf(leafB, parent: g1),
        });

        Assert.Equal(new[] { leafB }, r.Drawn);
        Assert.Equal(new[] { g1, g2, leafA, leafB }, r.Order);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  三、LayerOrder.Resolve —— 孤儿子节点的丢弃
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>🔴 父 id 指向不存在的图层 → 该层<b>连 Order 都不进</b>。</summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:136</c> 只调 <c>visit(nil, ...)</c> —— 没有第二个根入口，
    /// 所以「挂在不存在的父下」= 不可达 = 丢弃。<b>不是</b>被当成根层。
    /// 期望：Order/Drawn 只有 B。
    /// </remarks>
    [Fact]
    public void Resolve_父id悬空_该层连Order都不进()
    {
        Guid ghost = Guid.NewGuid(), b = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Leaf(ghost, parent: Guid.NewGuid()), Leaf(b) });

        Assert.Equal(new[] { b }, r.Order);
        Assert.Equal(new[] { b }, r.Drawn);
    }

    /// <summary>
    /// 🔴 <b>父 id 指向一个不是分组的普通图层</b> → 那棵子树被丢弃。
    /// </summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:133</c> 的 <c>if node.isGroup { visit(node.id, ...) }</c>
    /// —— <b>只有分组才会把它的 id 拿去当桶键</b>。所以普通图层 P 下面的子 C 永远不会被访问。
    /// 这与「不透明度那张表覆盖全部图层」<b>正好相反</b>（见下文第 4 组），
    /// 两处不对称是源码原样，不是笔误。
    /// 期望：Order/Drawn 只有 P，C 消失。
    /// </remarks>
    [Fact]
    public void Resolve_父id指向非分组图层_该子树被丢弃()
    {
        Guid p = Guid.NewGuid(), child = Guid.NewGuid(), ok = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[]
        {
            Leaf(p), Leaf(child, parent: p), Leaf(ok),
        });

        Assert.Equal(new[] { p, ok }, r.Order);
        Assert.Equal(new[] { p, ok }, r.Drawn);
    }

    /// <summary>自环（A 是分组且 A.parentID == A）→ 不可达，什么都不产出。</summary>
    /// <remarks>A 不在 roots 桶里，也没有任何人会去取 byParent[A] 桶（只有它自己会，而它不可达）。期望全空。</remarks>
    [Fact]
    public void Resolve_自环分组_不可达_什么都不产出()
    {
        Guid a = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Group(a, parent: a) });

        Assert.Empty(r.Order);
        Assert.Empty(r.Visible);
        Assert.Empty(r.Drawn);
    }

    /// <summary>互环（A 父 B、B 父 A，都是分组）→ 都不可达，全空。</summary>
    /// <remarks>与自环同理：没有任何一个在 roots 桶里，就没有遍历起点。期望全空。</remarks>
    [Fact]
    public void Resolve_互环分组_都不可达_什么都不产出()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Group(a, parent: b), Group(b, parent: a) });

        Assert.Empty(r.Order);
        Assert.Empty(r.Visible);
        Assert.Empty(r.Drawn);
    }

    /// <summary>
    /// 🔴 环之所以不炸，<b>不是因为深度上限</b>，而是因为环上的节点全都不在 roots 桶里。
    /// </summary>
    /// <remarks>
    /// <para>这一点很容易搞反，值得单独钉住：
    /// <c>visit(nil, depth: 0, ...)</c> 是<b>唯一</b>的遍历起点，而环上每个节点的
    /// <c>parentID</c> 都指向环内另一个节点，<b>永远不为 nil</b>，
    /// 所以它们压根不会被取到桶 —— 递归一次都没发生。</para>
    /// <para>推论（比测试本身更重要）：
    /// <list type="bullet">
    /// <item>只要图层 id 唯一，<c>resolve</c> 的递归<b>结构上就必然有限</b>
    /// —— 每个节点被访问的次数等于「以它为父的桶被取了几次」，id 唯一就只一次。</item>
    /// <item>因此 <c>LayerGroups.swift:125</c> 的 <c>guard depth &lt;= 64</c>
    /// <b>不是终止保护，是截断规则</b>：它只在「树真的超过 65 层」时才起作用
    /// （见 <see cref="Resolve_深度超64_整棵子树被静默丢弃"/>）。</item>
    /// <item>反过来，<c>LayerOpacity.effective</c> 的 <c>depth &lt; 64</c> <b>是</b>终止保护：
    /// 它沿 <c>parentID</c> 往上爬，环是可达的（<c>a→b→a</c>），
    /// 没有跳数上限就是死循环。见 <see cref="Effective_父链成环_由64跳上限兜住不死循环"/>。</item>
    /// </list></para>
    /// <para>期望：两个环节点都不可达，结果全空，且方法正常返回（没有栈溢出）。</para>
    /// </remarks>
    [Fact]
    public void Resolve_环不可达_因为环上节点的parentID永不为null()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(new[] { Group(a, parent: b), Group(b, parent: a) });

        // a.parentID == b（非 null）、b.parentID == a（非 null）⇒ children[nil] 为空 ⇒ 一次都没 visit。
        Assert.Empty(r.Order);
        Assert.Empty(r.Visible);
        Assert.Empty(r.Drawn);
    }

    /// <summary>可达部分照常产出，<b>环里的那一坨</b>整体丢弃，互不影响。</summary>
    /// <remarks>
    /// 结构：root ─ A ─ B ─ C（四层可达的正常链），外加 d ↔ e 互环。
    /// d 和 e 都不在 roots 桶里，所以整坨不可达；它们的存在<b>不该影响</b>正常链。
    /// 期望：Order = [root, A, B, C]，d、e 都不出现。
    /// </remarks>
    [Fact]
    public void Resolve_可达部分照常产出_环内部分丢弃()
    {
        Guid root = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        Guid d = Guid.NewGuid(), e = Guid.NewGuid();

        LayerOrderResult r = LayerOrder.Resolve(
        [
            Group(root), Group(a, parent: root), Group(b, parent: a), Group(c, parent: b),
            Group(d, parent: e), Group(e, parent: d),
        ]);

        Assert.Equal(new[] { root, a, b, c }, r.Order);
        Assert.DoesNotContain(d, r.Order);
        Assert.DoesNotContain(e, r.Order);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  四、LayerOrder.Resolve —— 深度上限 64（静默丢整棵子树）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 深度 &gt; 64 时<b>整棵子树被丢弃</b>，不抛异常、不影响兄弟。
    /// </summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:125</c> 的 <c>guard depth &lt;= 64 else { return }</c>。
    /// 手算：<c>Visit</c> 以 depth=0 处理 roots，depth=d 的调用 append 的是 g_{d+1}，
    /// 该调用的 guard 是 <c>d &lt;= 64</c>。所以 <b>g1..g65 进 Order，g66 起全部不进</b>。
    /// 70 层嵌套 → Order 恰好 65 个元素，且第 65 个是 g65（g66..g70 一个都不在）。
    /// </remarks>
    [Fact]
    public void Resolve_深度超64_整棵子树被静默丢弃()
    {
        Guid[] ids = Enumerable.Range(0, 70).Select(_ => Guid.NewGuid()).ToArray();

        LayerOrderResult r = LayerOrder.Resolve(NestedGroups(ids));

        // 手算：g_{d+1} 由 depth=d 的调用 append，guard 为 d <= 64 ⇒ d <= 64 ⇒ g1..g65。
        Assert.Equal(LayerOrder.MaxDepth + 1, r.Order.Count);
        Assert.Equal(ids[0], r.Order[0]);
        Assert.Equal(ids[64], r.Order[64]);              // g65 进来了
        Assert.DoesNotContain(ids[65], r.Order);         // g66 被丢弃
        Assert.DoesNotContain(ids[66], r.Order);
        Assert.DoesNotContain(ids[69], r.Order);
    }

    /// <summary>第 66 层挂的叶子也不进 Drawn —— 丢弃的是「子树」不是「一层」。</summary>
    /// <remarks>
    /// 这是与 <c>break</c> 版本的关键区别：<c>break</c> 只跳过 g66 自己，
    /// 它的子节点仍会被画出（但父没画，子画在空白上，视觉上错位）。
    /// 期望 Drawn 为空 —— 一个像素都不产出。
    /// </remarks>
    [Fact]
    public void Resolve_深度超64时叶子也不绘制_丢弃的是整棵子树()
    {
        Guid[] ids = Enumerable.Range(0, 70).Select(_ => Guid.NewGuid()).ToArray();
        Guid deepLeaf = Guid.NewGuid();

        List<LayerNode> nodes = [.. NestedGroups(ids)];
        nodes.Add(Leaf(deepLeaf, parent: ids[69]));   // 挂在最深的 g70 下面

        LayerOrderResult r = LayerOrder.Resolve(nodes);

        Assert.Empty(r.Drawn);
    }

    /// <summary>边界：恰好 65 个分组能进 Order，但<b>第 65 层下的叶子仍然进不去</b>。</summary>
    /// <remarks>
    /// 手算：g1..g65 共 65 个分组全部 append（最后一次 append 发生在 depth=64 的调用里，
    /// guard <c>64 &lt;= 64</c> 通过）。随后它要递归到 depth=65 去取叶子桶，
    /// 那里 guard <c>65 &lt;= 64</c> <b>失败</b> → 叶子被丢。
    /// 所以「65 层分组可见」和「65 层下的叶子可见」是<b>两回事</b>，
    /// 分组占的那一格和叶子占的那一格不共享。
    /// 期望：Order = 65 个分组，Drawn = 空。
    /// </remarks>
    [Fact]
    public void Resolve_恰好65层分组进Order_但其下的叶子仍被丢弃()
    {
        Guid[] ids = Enumerable.Range(0, 65).Select(_ => Guid.NewGuid()).ToArray();
        Guid leafUnder65 = Guid.NewGuid();

        List<LayerNode> nodes = [.. NestedGroups(ids)];
        nodes.Add(Leaf(leafUnder65, parent: ids[64]));   // 挂在 g65 下

        LayerOrderResult r = LayerOrder.Resolve(nodes);

        Assert.Equal(65, r.Order.Count);
        Assert.DoesNotContain(leafUnder65, r.Order);
        Assert.Empty(r.Drawn);
    }

    /// <summary>边界：第 64 层下的叶子<b>能</b>进来 —— 上一条的对照。</summary>
    /// <remarks>
    /// 手算：叶子由 <c>Visit(叶子桶, depth=64)</c> append，guard <c>64 &lt;= 64</c> 通过。
    /// 64 层分组 + 叶子 = 65 个节点全可见。
    /// 期望：Order 65 个元素，Drawn 恰好 [leaf]。
    /// </remarks>
    [Fact]
    public void Resolve_第64层下的叶子能进来_作为上一条的对照()
    {
        Guid[] ids = Enumerable.Range(0, 64).Select(_ => Guid.NewGuid()).ToArray();
        Guid leafUnder64 = Guid.NewGuid();

        List<LayerNode> nodes = [.. NestedGroups(ids)];
        nodes.Add(Leaf(leafUnder64, parent: ids[63]));   // 挂在 g64 下

        LayerOrderResult r = LayerOrder.Resolve(nodes);

        Assert.Equal(65, r.Order.Count);
        Assert.Equal(new[] { leafUnder64 }, r.Drawn);
    }

    /// <summary>深层截断<b>不影响</b>同文档里另一棵浅树的绘制。</summary>
    /// <remarks>
    /// 深处那棵被丢弃，浅处那棵照常。
    /// 若实现成遇到超深就整个 resolve 返回空，这条会红。
    /// 期望：Drawn 只有浅树那一片叶子。
    /// </remarks>
    [Fact]
    public void Resolve_深层截断不影响同文档的浅树()
    {
        Guid[] ids = Enumerable.Range(0, 70).Select(_ => Guid.NewGuid()).ToArray();
        Guid shallowRoot = Guid.NewGuid(), shallowLeaf = Guid.NewGuid();

        List<LayerNode> nodes = [.. NestedGroups(ids)];
        nodes.Add(Group(shallowRoot));
        nodes.Add(Leaf(shallowLeaf, parent: shallowRoot));

        LayerOrderResult r = LayerOrder.Resolve(nodes);

        Assert.Equal(new[] { shallowLeaf }, r.Drawn);
        Assert.Contains(shallowRoot, r.Visible);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  五、LayerOpacity —— 父链相乘
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>无父 → 原样返回，不做任何加工。</summary>
    /// <remarks>期望 0.5 → 0.5。</remarks>
    [Fact]
    public void Effective_无父层_原样返回自身不透明度() =>
        Assert.Equal(0.5, LayerOpacity.Effective(0.5, null, EmptyLookup()));

    /// <summary>一级父：0.5 × 0.5 = 0.25。</summary>
    /// <remarks>
    /// 手算：opacity = own = 0.5，第 1 轮 id = parent 查到父，opacity *= 0.5 → 0.25，id = null，退出。
    /// 期望 0.25。
    /// </remarks>
    [Fact]
    public void Effective_一级父_自身乘父()
    {
        Guid parent = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([Group(parent, opacity: 0.5f)]);

        Assert.Equal(0.25, LayerOpacity.Effective(0.5, parent, byId));
    }

    /// <summary>两级嵌套：0.5 × 0.5 × 1.0 = 0.25。</summary>
    /// <remarks>
    /// 手算：leaf 0.5 → × G_inner 0.5 = 0.25 → × G_outer 1.0 = 0.25。
    /// 注意 G_outer 那个 1.0 <b>确实被乘了</b>（链不因值为 1 而提前停），
    /// 只是乘了不改变数。
    /// 期望 0.25。
    /// </remarks>
    [Fact]
    public void Effective_两级嵌套_乘满整条父链()
    {
        Guid outer = Guid.NewGuid(), inner = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup(
        [
            Group(outer, opacity: 1.0f),
            Group(inner, parent: outer, opacity: 0.5f),
        ]);

        Assert.Equal(0.25, LayerOpacity.Effective(0.5, inner, byId));
    }

    /// <summary>面板读数不变，图上才是乘积 —— 这正是 pass-through 的定义。</summary>
    /// <remarks>
    /// <c>LayerGroups.swift:49-51</c> 原文：
    /// <i>"a layer at 50% in a folder at 50% shows at 25%, while the layer itself still reads 50% in the panel."</i>
    /// 期望：<c>node.Opacity</c> 仍是 0.5（面板），<c>Effective</c> 是 0.25（图）。
    /// 这条把「不透明度被就地改写」的实现挡在门外。
    /// </remarks>
    [Fact]
    public void Effective_面板读数不变_图上才是乘积()
    {
        Guid folderId = Guid.NewGuid();
        LayerNode child = Leaf(Guid.NewGuid(), parent: folderId, opacity: 0.5f);
        LayerNode parent = Group(folderId, opacity: 0.5f);

        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([parent, child]);

        Assert.Equal(0.5f, child.Opacity);                       // 面板
        Assert.Equal(0.25, LayerOpacity.Effective(child, byId)); // 图
    }

    /// <summary>父 id 悬空 → 只乘到断点为止，<b>不报错</b>。</summary>
    /// <remarks>
    /// Swift 的 <c>let node = folder(current)</c> 查不到就 <c>while</c> 退出。
    /// 手算：leaf 0.5，parent 指向一个不存在的 id → 一轮都没跑 → 返回 0.5。
    /// 期望 0.5（不是抛异常，也不是 0）。
    /// </remarks>
    [Fact]
    public void Effective_父id悬空_只乘到断点为止()
    {
        Guid ghost = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([Group(Guid.NewGuid())]);

        Assert.Equal(0.5, LayerOpacity.Effective(0.5, ghost, byId));
    }

    /// <summary>链中段悬空 → 上半段仍然乘到了，断点之后的祖先不再乘。</summary>
    /// <remarks>
    /// 结构：leaf ← G1 ← ghost ← G3。ghost 不在表里。
    /// 手算：0.5 × G1(0.5) = 0.25；下一轮查 ghost 失败，退出 → 0.25（G3 的 0.5 <b>没</b>乘上）。
    /// 期望 0.25。若实现成「悬空就整链放弃」，会得到 0.5 —— 这条能抓住。
    /// </remarks>
    [Fact]
    public void Effective_链中段悬空_断点之后的祖先不再相乘()
    {
        Guid g1 = Guid.NewGuid(), ghost = Guid.NewGuid(), g3 = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup(
        [
            Group(g1, opacity: 0.5f),
            Group(g3, opacity: 0.5f),
        ]);

        // leaf.Opacity = 0.5，parent = g1；g1 的 parent = ghost；ghost 不在表里。
        Assert.Equal(0.25, LayerOpacity.Effective(0.5, g1, byId));
        Assert.True(byId.ContainsKey(g3));   // g3 在表里但没被走到，说明确实没乘
    }

    /// <summary>🔴 查找表覆盖<b>全部图层</b>：父指向一个普通图层，它的 opacity 照样相乘。</summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:84</c>：变量名叫 <c>folders</c>，
    /// 但建法是 <c>Dictionary(uniqueKeysWithValues: layers.lazy.map { ... })</c> —— <b>所有图层</b>。
    /// 手算：leaf 0.5，parent = P，<c>P.IsGroup = false</c>，P.Opacity = 0.5 → 0.25。
    /// <para>🔴 这跟上一组「父指向非分组图层 ⇒ 子树被丢弃」<b>正好相反</b>：
    /// <b>遍历</b>只在分组上递归，<b>不透明度</b>却对任何图层都查表。
    /// 两处不对称是源码原样。实现若「顺手统一」成一种，会静默改变其中一个的结果。</para>
    /// 期望 0.25。
    /// </remarks>
    [Fact]
    public void Effective_父指向非分组图层_其opacity仍被相乘()
    {
        Guid p = Guid.NewGuid();
        LayerNode parent = Leaf(p, opacity: 0.5f);           // IsGroup = false
        LayerNode child = Leaf(Guid.NewGuid(), parent: p, opacity: 0.5f);

        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([parent, child]);

        Assert.False(parent.IsGroup);
        Assert.Equal(0.25, LayerOpacity.Effective(child, byId));
    }

    /// <summary>不透明度<b>什么都不剔除</b>：不可见图层、分组、调整层一样照算。</summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:53-63</c>：函数体里没有任何 <c>isVisible</c> / <c>maskSourceID</c> /
    /// <c>isAdjustment</c> 判断。剔除全部发生在 <c>LayerOrder</c> 那侧。
    /// 期望：一个不可见的普通层，父分组 0.5，自己 0.5 → 仍然得到 0.25（而不是被跳过）。
    /// </remarks>
    [Fact]
    public void Effective_不可见图层也照算_剔除不在这层发生()
    {
        Guid g = Guid.NewGuid();
        LayerNode folder = Group(g, opacity: 0.5f);
        LayerNode invisible = Leaf(Guid.NewGuid(), parent: g, visible: false, opacity: 0.5f);

        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([folder, invisible]);

        Assert.Equal(0.25, LayerOpacity.Effective(invisible, byId));
    }

    /// <summary>调整层同样照算 —— <c>Effective</c> 不看 <c>IsAdjustment</c>。</summary>
    /// <remarks>
    /// 期望：调整层 opacity 0.5 在 0.5 的分组里 → 0.25。
    /// 调整层「能不能被剪贴、能不能当 base」是渲染栈的事，不是父链乘法的事。
    /// </remarks>
    [Fact]
    public void Effective_调整层也照算_本层不看IsAdjustment()
    {
        Guid g = Guid.NewGuid();
        LayerNode folder = Group(g, opacity: 0.5f);
        LayerNode adjustment = new LayerNode
        {
            Id = Guid.NewGuid(), Transform = new LayerTransform(),
            ParentId = g, Opacity = 0.5f,
            Adjustment = new AdjustmentSpec { Kind = "Levels" },
        };

        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([folder, adjustment]);

        Assert.True(adjustment.IsAdjustment);
        Assert.Equal(0.25, LayerOpacity.Effective(adjustment, byId));
    }

    /// <summary>某祖先 opacity = 0 → 结果为 0。</summary>
    /// <remarks>手算：0.5 × 0 × 1 = 0。</remarks>
    [Fact]
    public void Effective_祖先opacity为0则结果为0()
    {
        Guid g = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([Group(g, opacity: 0.0f)]);

        Assert.Equal(0.0, LayerOpacity.Effective(0.5, g, byId));
    }

    /// <summary>不做 clamp：输入 &gt; 1 时原样相乘传出。</summary>
    /// <remarks>
    /// <c>LayerNode.Opacity</c> 契约要求落在 0..1，但 <c>Effective(double, ...)</c> 这个重载
    /// 收的是裸 double。<c>LayerGroups.swift:53-63</c> 里也没有任何 clamp。
    /// 期望 2.0 原样传出（证明没有偷偷 clamp 到 1）。
    /// </remarks>
    [Fact]
    public void Effective_不做clamp_大于1的输入原样传出()
    {
        Guid g = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([Group(g, opacity: 2.0f)]);

        Assert.Equal(2.0, LayerOpacity.Effective(1.0, g, byId));
    }

    /// <summary>输入是 NaN → 结果是 NaN，且<b>不抛异常</b>。</summary>
    /// <remarks>NaN 会一路传染，不该被悄悄替换成 0 或 1。</remarks>
    [Fact]
    public void Effective_输入NaN_结果也是NaN()
    {
        Guid g = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([Group(g, opacity: 0.5f)]);

        Assert.True(double.IsNaN(LayerOpacity.Effective(double.NaN, g, byId)));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  六、LayerOpacity —— 跳数上限 64，以及与遍历上限差一
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>🔴 不透明度只乘<b>64 个</b>祖先，第 65 个被静默跳过。</summary>
    /// <remarks>
    /// 依据 <c>LayerGroups.swift:57</c> 的 <c>depth &lt; 64</c>。
    /// 手算：迭代 k（k 从 1 开始）进入时 depth = k-1，条件 <c>k-1 &lt; 64</c> ⇒ k ≤ 64。
    /// 所以 a1..a64 被乘，a65 不被乘。
    /// 构造：leaf ← a1 ← a2 ← … ← a65，每个 a_i 的 opacity = 0.5f（精确值），
    /// leaf 的 own = 1.0。于是结果 = 0.5^64 = 2^-64。
    /// 若上限误写成 65，结果会是 2^-65，两者差一倍，断言能抓住。
    /// </remarks>
    [Fact]
    public void Effective_只乘64个祖先_第65个被跳过()
    {
        Guid[] ids = Enumerable.Range(0, 65).Select(_ => Guid.NewGuid()).ToArray();

        // ids[0] 最深（离叶子最近），ids[64] 是根。全部 opacity = 0.5f（精确值），leaf 的 own = 1.0。
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup(AscendingGroups(ids));

        double actual = LayerOpacity.Effective(1.0, ids[0], byId);

        Assert.Equal(Math.Pow(2, -64), actual);        // 2^-64 ≈ 5.4210108624e-20
        Assert.NotEqual(Math.Pow(2, -65), actual);     // 差一档会被这条抓住
    }

    /// <summary>🔴 遍历上限与不透明度上限<b>差一</b>，不能共用一个常量。</summary>
    /// <remarks>
    /// 两条规则来自源码里两个<b>不同</b>的判断：
    /// <list type="bullet">
    /// <item>遍历 <c>guard depth &lt;= 64 else { return }</c>（<c>:125</c>）
    /// ⇒ 65 层分组可用（第 65 次调用 depth=64，通过）。</item>
    /// <item>不透明度 <c>while ... depth &lt; 64 ...</c>（<c>:57</c>）
    /// ⇒ 只乘 64 个祖先。</item>
    /// </list>
    /// 所以「65 层分组能显示」与「65 个祖先能相乘」是<b>两条独立事实</b>，
    /// 数值上都是 64，但语义边界不同。本测试把两份文档各自的边界各测一次，
    /// 任何一处被改成 &lt;= 或改成 &lt; 都会红。
    /// </remarks>
    [Fact]
    public void 遍历上限与不透明度上限差一()
    {
        // 两个常量当前都是 64，但它们来自两处不同的源码判断。
        Assert.Equal(64, LayerOrder.MaxDepth);
        Assert.Equal(64, LayerOpacity.MaxHops);

        // ── 遍历侧：65 个分组全部进 Order（第 64 次调用，depth=64，64<=64 通过）
        Guid[] gids = Enumerable.Range(0, 65).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(65, LayerOrder.Resolve(NestedGroups(gids)).Order.Count);

        // ── 不透明度侧：65 个祖先里只有前 64 个被乘（第 65 次迭代 depth=64，64<64 失败）
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup(AscendingGroups(gids));
        Assert.Equal(Math.Pow(2, -64), LayerOpacity.Effective(1.0, gids[0], byId));
    }

    /// <summary>恰好 64 个祖先时全部相乘，没有一个被跳过。</summary>
    /// <remarks>
    /// 与上一条「65 个只乘 64 个」成对。64 个祖先 → 迭代 k=1..64 全部通过 → 0.5^64。
    /// 构造时复用遍历侧那份 gids 前 64 个。
    /// </remarks>
    [Fact]
    public void Effective_恰好64个祖先时全部相乘()
    {
        Guid[] ids = Enumerable.Range(0, 64).Select(_ => Guid.NewGuid()).ToArray();

        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup(AscendingGroups(ids));

        Assert.Equal(Math.Pow(2, -64), LayerOpacity.Effective(1.0, ids[0], byId));
    }

    /// <summary>父链成环时不会无限循环 —— 64 跳上限兜住。</summary>
    /// <remarks>
    /// 结构：a 父 b，b 父 a。迭代 k 一直成功（每轮都能查到），depth 一路涨到 64 才停。
    /// 手算：乘 64 次 0.5 → 2^-64。
    /// 若没有 depth 上限（写成 <c>while let current = id</c> 且忘了 depth），这里会死循环。
    /// </remarks>
    [Fact]
    public void Effective_父链成环_由64跳上限兜住不死循环()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup(
        [
            Group(a, parent: b, opacity: 0.5f),
            Group(b, parent: a, opacity: 0.5f),
        ]);

        Assert.Equal(Math.Pow(2, -64), LayerOpacity.Effective(1.0, a, byId));
    }

    /// <summary>自环（a 父 a）也由同一个上限兜住。</summary>
    /// <remarks>同上，乘 64 次 0.5。</remarks>
    [Fact]
    public void Effective_自环也由64跳上限兜住()
    {
        Guid a = Guid.NewGuid();
        IReadOnlyDictionary<Guid, LayerOpacityEntry> byId = LayerOpacity.BuildLookup([Group(a, parent: a, opacity: 0.5f)]);

        Assert.Equal(Math.Pow(2, -64), LayerOpacity.Effective(1.0, a, byId));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  七、LayerOpacity.BuildLookup 与 EffectiveOpacities
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>🔴 重复 id 显式抛 <see cref="ArgumentException"/>。</summary>
    /// <remarks>
    /// Mac 侧是 <c>Dictionary(uniqueKeysWithValues:)</c> 的<b>运行时陷阱</b>（crash）。
    /// 这里改成显式异常：静默取最后一个会让绘制顺序（<c>Resolve</c>，两者都会进 Order）
    /// 与不透明度（只保留一个）各走各的，产出一张说不清来源的图。
    /// </remarks>
    [Fact]
    public void BuildLookup_重复图层id_显式抛异常()
    {
        Guid dup = Guid.NewGuid();
        List<LayerNode> nodes = [Leaf(dup, opacity: 0.25f), Leaf(dup, opacity: 0.75f)];

        ArgumentException ex = Assert.Throws<ArgumentException>(() => LayerOpacity.BuildLookup(nodes));
        Assert.Contains(dup.ToString(), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>空列表建出空表，不抛异常。</summary>
    [Fact]
    public void BuildLookup_空列表_返回空表() =>
        Assert.Empty(LayerOpacity.BuildLookup(Array.Empty<LayerNode>()));

    /// <summary>单个图层、null 父层 → 走 null 检查分支。</summary>
    /// <remarks>期望 <see cref="ArgumentNullException"/>。</remarks>
    [Fact]
    public void BuildLookup_图层列表为null_抛ArgumentNullException()
    {
        ArgumentNullException ex =
            Assert.Throws<ArgumentNullException>(() => LayerOpacity.BuildLookup(null!));
        Assert.Equal("layers", ex.ParamName);
    }

    /// <summary>查找表为 null → 抛 <see cref="ArgumentNullException"/>。</summary>
    /// <remarks>期望 paramName = "byId"。</remarks>
    [Fact]
    public void Effective_查找表为null_抛ArgumentNullException()
    {
        ArgumentNullException ex =
            Assert.Throws<ArgumentNullException>(() => LayerOpacity.Effective(1.0, null, null!));
        Assert.Equal("byId", ex.ParamName);
    }

    /// <summary><c>Resolve</c> 的图层列表为 null → 抛 <see cref="ArgumentNullException"/>。</summary>
    [Fact]
    public void Resolve_图层列表为null_抛ArgumentNullException()
    {
        ArgumentNullException ex =
            Assert.Throws<ArgumentNullException>(() => LayerOrder.Resolve(null!));
        Assert.Equal("layers", ex.ParamName);
    }

    /// <summary>
    /// <c>EffectiveOpacities</c> 覆盖<b>每一个</b>图层，含分组、含不可见的。
    /// </summary>
    /// <remarks>
    /// 对应 <c>LayerGroups.swift:83-86</c> 的 <c>mapValues</c> —— 迭代的是<b>整张表</b>，
    /// 不是 drawn 列表。所以分组与隐藏层同样有条目。
    /// 手算（leaf 0.5 在 folder 0.5 里）：leaf → 0.25，folder → 0.5（0.5 × 无父 = 0.5）。
    /// 期望：2 个条目，leaf = 0.25、folder = 0.5。
    /// </remarks>
    [Fact]
    public void EffectiveOpacities_覆盖全部分组与不可见图层()
    {
        Guid g = Guid.NewGuid();
        LayerNode folder = Group(g, opacity: 0.5f);
        LayerNode leaf = Leaf(Guid.NewGuid(), parent: g, opacity: 0.5f);
        LayerNode hidden = Leaf(Guid.NewGuid(), parent: g, visible: false, opacity: 0.25f);

        IReadOnlyDictionary<Guid, double> all = LayerOpacity.EffectiveOpacities([folder, leaf, hidden]);

        Assert.Equal(3, all.Count);
        Assert.Equal(0.5, all[folder.Id]);   // 分组：0.5 × 无父 = 0.5，不含自身两次相乘
        Assert.Equal(0.25, all[leaf.Id]);
        Assert.Equal(0.125, all[hidden.Id]); // 0.25 × 0.5 = 0.125
    }

    /// <summary>分组条目<b>不含自身被相乘两次</b>。</summary>
    /// <remarks>
    /// 手算：folder 0.5，父 root 0.5 ⇒ <c>Effective(folder)</c> = 0.5 × 0.5 = 0.25。
    /// 如果实现成「先把自己加进链再往上走」，会得到 0.125。这条专抓那个错。
    /// </remarks>
    [Fact]
    public void EffectiveOpacities_分组条目不含自身两次相乘()
    {
        Guid root = Guid.NewGuid(), mid = Guid.NewGuid();
        LayerNode rootNode = Group(root, opacity: 0.5f);
        LayerNode midNode = Group(mid, parent: root, opacity: 0.5f);

        IReadOnlyDictionary<Guid, double> all = LayerOpacity.EffectiveOpacities([rootNode, midNode]);

        Assert.Equal(0.25, all[mid]);   // 0.5 × 0.5，自身只算一次
        Assert.Equal(0.5, all[root]);   // 根层：0.5
    }

    /// <summary>重复 id 也在 <c>EffectiveOpacities</c> 这一层就被挡下，不会静默少一条。</summary>
    /// <remarks>
    /// 若实现改成「先建结果表再算」，重复 id 会让结果字典少一条而<b>不抛异常</b>，
    /// 那时下游按 id 取不透明度会拿到 null。这条锁住异常必须冒到这一层。
    /// </remarks>
    [Fact]
    public void EffectiveOpacities_重复id同样抛异常()
    {
        Guid dup = Guid.NewGuid();
        List<LayerNode> nodes = [Leaf(dup), Leaf(dup)];

        Assert.Throws<ArgumentException>(() => LayerOpacity.EffectiveOpacities(nodes));
    }

    /// <summary>空列表 → 空结果。</summary>
    [Fact]
    public void EffectiveOpacities_空列表_返回空字典() =>
        Assert.Empty(LayerOpacity.EffectiveOpacities(Array.Empty<LayerNode>()));

    // ══════════════════════════════════════════════════════════════════════
    //  八、两者的配合：Resolve 的 Drawn 逐个查 EffectiveOpacities
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 合成主循环要做的两步能接上：<c>Resolve(...).Drawn</c> 里的每个 id
    /// 都能在 <c>EffectiveOpacities</c> 里查到条目。
    /// </summary>
    /// <remarks>
    /// 结构：folder(0.5) 里放 leaf(0.5)，另有一个根层兄弟 hidden。
    /// 期望：Drawn = [leaf]，其有效不透明度 = 0.25。
    /// 这条本身不是新规则，但它是<b>两个 API 的接缝</b> —— Drawn 排绘制顺序、
    /// EffectiveOpacities 供逐层取不透明度，接不上就没法进合成循环。
    /// </remarks>
    [Fact]
    public void Resolve的Drawn与EffectiveOpacities能接上()
    {
        Guid g = Guid.NewGuid(), leaf = Guid.NewGuid(), hidden = Guid.NewGuid();
        List<LayerNode> nodes =
        [
            Group(g, opacity: 0.5f),
            Leaf(leaf, parent: g, opacity: 0.5f),
            Leaf(hidden, visible: false, opacity: 0.75f),
        ];

        LayerOrderResult order = LayerOrder.Resolve(nodes);
        IReadOnlyDictionary<Guid, double> opacities = LayerOpacity.EffectiveOpacities(nodes);

        Assert.Equal(new[] { leaf }, order.Drawn);
        foreach (Guid id in order.Drawn)
        {
            Assert.True(opacities.ContainsKey(id));
            Assert.Equal(0.25, opacities[id]);
        }
    }

    /// <summary>
    /// 🔴 <c>Drawn</c> 里<b>不会出现</b>分组 —— 合成循环只对叶子层调 <c>BlendOps.Composite</c>。
    /// </summary>
    /// <remarks>
    /// 这把「分组永不作为独立 surface 参与合成」这条 pass-through 规则落到 API 形状上
    /// （源码注释：<c>never composited as a unit</c>，<c>LayerGroups.swift:50-51</c>）。
    /// 实现若把分组也塞进 Drawn（哪怕 Visible 里有），合成器就会试图对一张
    /// 「没有像素」的 surface 做混合。
    /// </remarks>
    [Fact]
    public void Drawn里不含任何分组()
    {
        Guid g1 = Guid.NewGuid(), g2 = Guid.NewGuid(), leaf = Guid.NewGuid();
        List<LayerNode> nodes = [Group(g1), Group(g2, parent: g1), Leaf(leaf, parent: g2)];
        Dictionary<Guid, LayerNode> byId = nodes.ToDictionary(n => n.Id);

        LayerOrderResult order = LayerOrder.Resolve(nodes);

        Assert.Equal(new[] { leaf }, order.Drawn);
        Assert.All(order.Drawn, id => Assert.False(byId[id].IsGroup));
        Assert.Contains(g1, order.Visible);          // 分组确实在 Visible 里
        Assert.Contains(g2, order.Visible);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  辅助实现
    // ══════════════════════════════════════════════════════════════════════

    private static IReadOnlyDictionary<Guid, LayerOpacityEntry> EmptyLookup() =>
        new Dictionary<Guid, LayerOpacityEntry>();
}