namespace Compositor.History;

/// <summary>
/// 撤销/重做的内容载体。快照策略与补丁策略都经由本类型进入历史栈。
/// </summary>
/// <remarks>
/// 🔴 <b>快照 vs 补丁的分工（已由总管 2026-10-07 批准）</b>
/// <list type="bullet">
/// <item><b>结构改动</b>（图层增删/改名/可见性/重排/移动/替换画布）走 <b>补丁</b>：
/// 只改图层列表，补丁占用与改动量成正比，而整文档快照要克隆整个图层列表。</item>
/// <item><b>大块像素重算</b>（模糊笔 / 涂抹 / 形状栅格化）走 <b>按矩形裁的快照</b>：
/// 逐像素记差分在脏区一大时就爆内存；整画布快照又太浪费，所以/// 只裁受影响的 <c>DocRect</c>。</item>
/// <item><b>无变化的操作不入栈</b>：缩放、导航、把名字改成空白后再改回原值 —— 见
/// <see cref="DocumentHistory"/> 的 no-op 判定。</item>
/// </list>
/// <para>
/// 该分工<b>不是我发明的</b>：Mac 侧 <c>DocumentHistory.swift:5</c> 注释明写
/// "Value snapshots share immutable CGImages; no pixel copies for layer edits"，
/// 即结构编辑靠值快照共享不可变图像、像素编辑靠图像整体替换，本类是它在 C# 契约下的对应物。
/// </para>
/// </remarks>
public interface IHistoryStore
{
    /// <summary>本条记录占用的大致字节数，供深度淘汰策略使用。</summary>
    /// <remarks>
    /// 对应 Mac 侧 <c>retainedBytes</c>（<c>DocumentHistory.swift:93-114</c>）。
    /// Mac 侧按 <c>bytesPerRow * height</c> 统计且对共享图像去重；
    /// 本接口只要求返回「可比的数量」，去重策略由实现决定。
    /// </remarks>
    long RetainedBytes { get; }

    /// <summary>
    /// 是否与当前文档状态相同。相同则不入栈（no-op）。
    /// </summary>
    /// <remarks>
    /// 对应 <c>DocumentHistory.swift:68</c> 的 <c>guard before.document != document else { return }</c>。
    /// 🔴 这条是 Mac 侧的硬证据：<b>no-op 编辑不产生撤销步，也不清空 redo 分支</b>。
    /// </remarks>
    /// <param name="current">当前文档状态标识。实现自行定义其相等语义。</param>
    /// <returns>返回 <see langword="true"/> 表示本次编辑没有产生任何变化。</returns>
    bool IsNoOp(DocumentStateKey current);
}

/// <summary>
/// 文档状态的<b>值语义标识</b>，供 <see cref="IHistoryStore.IsNoOp"/> 与快照比对使用。
/// </summary>
/// <remarks>
/// 对应 Mac 侧 <c>DocumentHistory.Snapshot</c>（<c>DocumentHistory.swift:8-12</c>）里的
/// <c>document</c> + <c>activeLayerID</c> 两个字段。Mac 侧直接比对整个值类型 <c>CanvasDocument?</c>；
/// C# 侧 <c>IDocument</c> 只有只读的 <c>Layers</c>，没有值类型语义，
/// 因此用「文档标识 + 活动图层 + 内容指纹」三元组表达同一件事。
/// </remarks>
/// <param name="DocumentId">文档标识。文档被替换（新建 / 替换画布）时变化。</param>
/// <param name="ActiveLayerId">当前活动图层 id，可为 <see langword="null"/>。</param>
/// <param name="ContentFingerprint">内容指纹。实现自行定义，通常是结构 + 像素的混合摘要。</param>
public readonly record struct DocumentStateKey(Guid DocumentId, Guid? ActiveLayerId, long ContentFingerprint);
