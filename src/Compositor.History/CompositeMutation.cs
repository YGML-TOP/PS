using Compositor.Core;

namespace Compositor.History;

/// <summary>
/// 组合型变更：一次提交它 = 一个 undo step。内部持有若干个「单变更」。
/// </summary>
/// <remarks>
/// 🔴 <b>为什么需要它</b>（AI-1 2026-10-07 回复）：
/// <c>IDocument.Mutate(Action&lt;DocumentMutation&gt; tx)</c> 的 <c>tx</c> 回调
/// <b>拿不到任何可用对象</b>——<c>DocumentMutation</c> 是纯抽象二元组，
/// 没有工厂、没有成员可写、<c>Action&lt;T&gt;</c> 也没有返回通道把实例交回去。
/// 也就是说<b>调用方根本无法把自己的 mutation 送进文档</b>。这条已同步总管，列入 v1.3 待澄清。
/// <para>
/// 本类型是<b>不依赖契约变更</b>的解法：分组能力留在 AI-3 自己的目录里，
/// 命名（Label）仍挂在 <see cref="HistoryEntry"/> 上，<b>契约零改动</b>。
/// </para>
/// <para>
/// 🔴 <b>Revert 必须逆序</b>（<c>Count-1 → 0</c>）。这不是风格问题，是正确性问题：
/// 后施加的变更依赖先施加的结果，正序撤销会得到与逆序不同的状态，
/// 且<b>不会抛异常</b>——是 undo 栈最典型的静默错误。
/// </para>
/// </remarks>
public sealed class CompositeMutation : DocumentMutation
{
    private readonly IReadOnlyList<DocumentMutation> _parts;

    /// <summary>构造组合变更。</summary>
    /// <param name="parts">
    /// 内部变更，<b>按施加顺序排列</b>。空集合合法，等价于一个 no-op 变更。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="parts"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 构造时拷贝为数组，避免调用方在提交后修改原集合导致历史被篡改。
    /// </remarks>
    public CompositeMutation(IEnumerable<DocumentMutation> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _parts = parts.ToArray();
    }

    /// <summary>内部变更数量。</summary>
    public int Count => _parts.Count;

    /// <summary>只读访问第 <paramref name="index"/> 个内部变更。</summary>
    /// <param name="index">下标，范围 <c>[0, Count)</c>。</param>
    /// <returns>该变更实例本身，不是副本。</returns>
    /// <exception cref="ArgumentOutOfRangeException">下标越界。</exception>
    public DocumentMutation this[int index] => _parts[index];

    /// <summary>只读暴露全部内部变更，便于调试与断言。</summary>
    public IReadOnlyList<DocumentMutation> Parts => _parts;

    /// <summary><b>正序</b>执行全部内部变更。</summary>
    public override void Apply()
    {
        foreach (DocumentMutation part in _parts)
        {
            part.Apply();
        }
    }

    /// <summary><b>逆序</b>逆转全部内部变更，回到 Apply 之前的状态。</summary>
    public override void Revert()
    {
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            _parts[i].Revert();
        }
    }
}