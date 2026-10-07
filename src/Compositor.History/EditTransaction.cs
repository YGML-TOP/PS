using Compositor.Core;

namespace Compositor.History;

/// <summary>
/// 编辑事务：<c>BeginEdit</c> / <c>Commit</c> / <c>Cancel</c>，支持<b>嵌套</b>。
/// </summary>
/// <remarks>
/// 🔴 对应 Mac 侧 <c>DocumentHistory.begin</c> / <c>end</c> + <c>depth</c> 计数
/// （<c>DocumentHistory.swift:24</c>、<c>:54-60</c>、<c>:62-74</c>），
/// 以及 <c>EditorSession.beginEdit/endEdit</c>（<c>EditorSession.swift:636-640</c>）。
/// <para>
/// <b>嵌套语义</b>：只有 <c>depth</c> 从 0 开始的<b>最外层</b> begin 会记下 before 快照；
/// 内层 begin/end 只增减计数。<b>最外层 end 才产生一条撤销步</b>。
/// 这就是「一次用户操作 = 一个 undo step」的实现：多个内部写操作包在一个事务里。
/// </para>
/// <para>
/// <b>Cancel</b>：Mac 侧的 <c>DocumentHistory</c> <b>没有</b> cancel —— 它只有 begin/end，
/// 因为 <c>EditorSession</c> 的编辑函数都用 <c>defer { endEdit() }</c> 且提前 <c>return</c> 守卫，
/// 失败路径靠 <c>guard</c> 直接不进入事务实现。<b>C# 侧我提供显式 cancel</b>，
/// 因为本项目要求「Cancel 必须真正撤销该次编辑的全部副作用」，
/// 而依赖调用方写对 <c>defer</c> 正是上一轮社区移植失败的一类原因。
/// </para>
/// </remarks>
public sealed class EditTransaction
{
    private readonly HistoryStack _stack;
    private readonly Func<DocumentStateKey> _readState;
    private readonly Action<DocumentMutation> _onMutation;

    private readonly List<DocumentMutation> _accumulated = new();
    private HistoryEntry? _pendingEntry;
    private int _depth;

    /// <summary>本层 <c>BeginEdit</c> 进入时的 <c>_accumulated.Count</c>，内层取消用它确定退哪一段。</summary>
    private int _revertFloor;

    /// <summary>构造事务管理器。</summary>
    /// <param name="stack">撤销栈，用于在提交时压栈。</param>
    /// <param name="readState">读取当前文档状态的委托。</param>
    /// <param name="onMutation">
    /// 每次 mutation 被应用后的观察钩子，用于通知 UI 重绘。<b>可为 <see langword="null"/></b>；
    /// 它<b>不</b>负责应用变更 —— <see cref="Execute"/> 自己调 <c>Apply()</c>。
    /// </param>
    /// <exception cref="ArgumentNullException">前两个参数为 <see langword="null"/>。</exception>
    public EditTransaction(
        HistoryStack stack,
        Func<DocumentStateKey> readState,
        Action<DocumentMutation>? onMutation = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(readState);
        _stack = stack;
        _readState = readState;
        _onMutation = onMutation ?? (static _ => { });
    }

    /// <summary>当前嵌套深度。<b>0 表示没有进行中的事务。</b></summary>
    public int Depth => _depth;

    /// <summary>是否有进行中的事务。</summary>
    public bool IsActive => _depth > 0;

    /// <summary>事务未提交时的名称，供 UI 显示。</summary>
    public string PendingName => _pendingEntry?.Name ?? string.Empty;

    /// <summary>开始一次编辑事务，可嵌套。</summary>
    /// <param name="name">撤销菜单显示的名字。空名字按 <c>"Edit"</c> 处理（对应 Mac 侧 <c>pendingName = "Edit"</c>）。</param>
    /// <remarks>
    /// 对应 <c>DocumentHistory.swift:54-60</c>：<b>只有 depth == 0 才记 before 快照</b>，
    /// 内层调用只 <c>depth += 1</c>。
    /// </remarks>
    public void BeginEdit(string name)
    {
        if (_depth == 0)
        {
            _pendingEntry = new HistoryEntry(
                string.IsNullOrEmpty(name) ? "Edit" : name,
                Array.Empty<DocumentMutation>(),
                new NoOpStore());
        }

        _depth++;
        _revertFloor = _accumulated.Count;
    }

    /// <summary>提交事务。最外层提交时才产生一条撤销步。</summary>
    /// <remarks>
    /// 对应 <c>DocumentHistory.swift:62-74</c>。关键行为：
    /// <list type="number">
    /// <item>无进行中事务时<b>静默返回</b>（<c>guard depth &gt; 0 else { return }</c>）。</item>
    /// <item>内层提交只减计数，不入栈。</item>
    /// <item><b>no-op 不入栈</b>：<c>guard before.document != document else { return }</c>（<c>:68</c>）。
    /// 这是 Mac 侧硬证据 —— 缩放、导航、空白改名都不产生撤销步，且<b>不清空 redo 分支</b>。</item>
    /// </list>
    /// </remarks>
    public void Commit()
    {
        if (_depth == 0)
        {
            return;
        }

        _depth--;
        if (_depth != 0 || _pendingEntry is null)
        {
            return;
        }

        HistoryEntry entry = new(
            _pendingEntry.Name,
            _accumulated.ToArray(),
            new NoOpStore(_accumulated.Count == 0));
        _pendingEntry = null;
        _accumulated.Clear();

        DocumentStateKey current = _readState();
        if (entry.IsNoOp(current))
        {
            // no-op：不入栈，也不清空 redo 分支（future 只在 Push 里清）。
            return;
        }

        _stack.Push(entry, current);
    }

    /// <summary>
    /// 取消事务并<b>真正撤销该次编辑的全部副作用</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>逆序</b> Revert 收集到的每个 mutation，即使它们已经被 <see cref="Execute"/> 应用过。
    /// 只退到最外层 begin 的状态；嵌套取消一层后深度仍 &gt; 0 时视为内层提前结束，
    /// 由调用方继续 <see cref="Commit"/> 或 <see cref="Cancel"/> 到 0 深度。
    /// </remarks>
    public void Cancel()
    {
        if (_depth == 0)
        {
            return;
        }

        _depth--;
        if (_depth != 0)
        {
            // 🔴 内层取消：把本层收集到的 mutation 逆序退掉，但**不清空** _accumulated
            // —— 外层事务仍在进行，那些更早的 mutation 还要能被外层的 Cancel 退掉，
            // 也要能被外层的 Commit 入栈。
            RevertAccumulated(fromStart: false);
            return;
        }

        HistoryEntry? entry = _pendingEntry;
        _pendingEntry = null;
        entry?.Revert();
        RevertAccumulated(fromStart: true);
    }

    /// <summary>把收集到的 mutation 逆序撤销。</summary>
    /// <param name="fromStart">从 0 开始逆序退，否则只退最外层事务内层 begin 之后新增的那一段。</param>
    /// <remarks>
    /// 🔴 <b>逆序</b>是硬要求：与 <see cref="HistoryEntry.Revert"/> 同一理由。
    /// 正序回退两个触碰同一状态的 mutation，结果与逆序不同，且不会报错 —— 静默错误。
    /// </remarks>
    private void RevertAccumulated(bool fromStart)
    {
        for (int i = _accumulated.Count - 1; i >= (fromStart ? 0 : _revertFloor); i--)
        {
            _accumulated[i].Revert();
        }

        if (fromStart)
        {
            _accumulated.Clear();
        }
        else
        {
            _accumulated.RemoveRange(_revertFloor, _accumulated.Count - _revertFloor);
        }
    }

    /// <summary>
    /// 在事务内执行一次写操作：<b>立即 Apply</b>，并把 mutation 收集起来供提交 / 取消使用。
    /// </summary>
    /// <param name="mutation">要执行的变更。</param>
    /// <remarks>
    /// 🔴 <b>本方法自己调用 <c>mutation.Apply()</c></b>，不把应用工作外包出去。
    /// 早先的写法是「收集后交给一个可注入的委托执行」，结果 <c>DocumentHistory</c> 传入空实现，
    /// mutation 从未被真正应用 —— <b>编译通过、测试全红</b>。撤销栈的正确性不能依赖调用方记得接线。
    /// <para>必须在 <see cref="BeginEdit"/> 之后调用。事务外调用时直接应用但不收集，
    /// 这样该操作<b>不可撤销</b> —— 与 Mac 侧「所有写操作都在 beginEdit/endEdit 之间」的约定一致。</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> 为 <see langword="null"/>。</exception>
    public void Execute(DocumentMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.Apply();
        _onMutation(mutation);

        if (_depth > 0)
        {
            _accumulated.Add(mutation);
        }
    }
}

/// <summary>
/// <see cref="IHistoryStore"/> 的空实现载体：事务尚未产生真实快照时占位。
/// </summary>
/// <remarks>
/// 真实文档快照由上层（依赖 AI-1 对 <c>IDocument</c> 多 mutation 提交的裁决）注入；
/// 在那之前，no-op 判定退化为「事务收集了至少一个 mutation」。
/// </remarks>
internal sealed class NoOpStore : IHistoryStore
{
    private readonly bool _noOp;

    /// <summary>构造占位载体。</summary>
    /// <param name="noOp">恒返回该值作为 <see cref="IsNoOp"/> 结果。</param>
    public NoOpStore(bool noOp = false) => _noOp = noOp;

    /// <inheritdoc/>
    public long RetainedBytes => 0;

    /// <inheritdoc/>
    public bool IsNoOp(DocumentStateKey current) => _noOp;
}
