using Compositor.Core;

namespace Compositor.History;

/// <summary>
/// 一个撤销步。<b>一次用户操作 = 一个 entry</b>，不是每个内部写操作一个。
/// </summary>
/// <remarks>
/// 🔴 <b>命名权与组合关系在本类型上，不在 <c>DocumentMutation</c> 上</b>（总管 2026-10-07 裁决）：
/// 「一次用户操作 = 一个 undo step」属于上层历史条目的概念，
/// 把 Label 塞进 <see cref="DocumentMutation"/> 会把两层概念混在一起。
/// 因此本类型持有 <see cref="Name"/>，并且一个 entry 可持有<b>多个</b> mutation。
/// <para>
/// ⚠️ 多 mutation 的<b>逆序</b>契约：<see cref="Apply"/> 按<b>正序</b>执行，
/// <see cref="Revert"/> 必须按<b>逆序</b>执行。若两个 mutation 触碰同一处状态，
/// 正序回放会得到与逆序回放不同的结果 —— 这是 undo 栈最常见的静默错误。
/// </para>
/// </remarks>
public sealed class HistoryEntry
{
    private readonly IReadOnlyList<DocumentMutation> _mutations;
    private readonly IHistoryStore _store;

    /// <summary>构造一条撤销步。</summary>
    /// <param name="name">撤销菜单显示的名字，对应 Mac 侧 <c>pendingName</c>。</param>
    /// <param name="mutations">本次编辑产生的变更，按<b>应用顺序</b>排列。</param>
    /// <param name="store">快照/补丁载体，同时提供容量信息与 no-op 判定。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public HistoryEntry(string name, IReadOnlyList<DocumentMutation> mutations, IHistoryStore store)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(store);
        Name = name ?? "Edit";
        _mutations = mutations;
        _store = store;
    }

    /// <summary>撤销菜单显示的名字。空栈时由 <see cref="DocumentHistory"/> 返回空串。</summary>
    public string Name { get; }

    /// <summary>本条记录的持有字节数。</summary>
    public long RetainedBytes => _store.RetainedBytes;

    /// <summary>正序执行全部变更。</summary>
    public void Apply()
    {
        foreach (DocumentMutation m in _mutations)
        {
            m.Apply();
        }
    }

    /// <summary><b>逆序</b>逆转全部变更，回到本 entry 应用之前的状态。</summary>
    public void Revert()
    {
        for (int i = _mutations.Count - 1; i >= 0; i--)
        {
            _mutations[i].Revert();
        }
    }

    /// <summary>判定本次编辑是否未产生变化。</summary>
    /// <param name="current">编辑后的文档状态。</param>
    /// <returns>未产生变化时返回 <see langword="true"/>，此时不入栈。</returns>
    public bool IsNoOp(DocumentStateKey current) => _store.IsNoOp(current);

    /// <summary>只读暴露所含变更，便于调试与断言。</summary>
    /// <remarks>对外暴露的是同一批实例，不是副本。</remarks>
    public IReadOnlyList<DocumentMutation> Mutations => _mutations;
}
