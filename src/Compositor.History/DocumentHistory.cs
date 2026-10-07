using Compositor.Core;

namespace Compositor.History;

/// <summary>
/// M3 撤销栈的对外门面。把 <see cref="HistoryStack"/>、<see cref="EditTransaction"/>、
/// <see cref="HistoryRevision"/> 组合成单一入口。
/// </summary>
/// <remarks>
/// 🔴 <b>职责边界</b>：本类只回答「历史栈本身能不能操作」。
/// 「此刻是否允许操作」（导入中 / 对话框打开 / 笔刷进行中）属 UI 层，归 AI-6，
/// 对应 Mac 侧 <c>EditorSession.canUseHistory</c>（<c>EditorSession.swift:605-608</c>）。
/// 本类<b>不</b>持有 <c>isImporting</c> 之类状态。
/// </remarks>
public sealed class DocumentHistory
{
    private readonly HistoryStack _stack;
    private readonly HistoryRevision _revision;
    private readonly EditTransaction _transaction;

    /// <summary>构造历史门面。</summary>
    /// <param name="entryLimit">条目上限，默认 100（照抄 Mac 侧 <c>DocumentHistory.swift:28</c>）。</param>
    /// <param name="retainedByteLimit">字节上限，默认 256 MB（同上）。</param>
    /// <param name="readState">读取当前文档状态的委托。</param>
    /// <exception cref="ArgumentNullException"><paramref name="readState"/> 为 <see langword="null"/>。</exception>
    public DocumentHistory(
        int entryLimit = 100,
        long retainedByteLimit = 256L * 1024 * 1024,
        Func<DocumentStateKey>? readState = null)
    {
        Func<DocumentStateKey> read = readState ?? (static () => default);
        _stack = new HistoryStack(entryLimit, retainedByteLimit);
        _revision = new HistoryRevision();
        _transaction = new EditTransaction(_stack, read, null);
    }

    /// <summary>是否可撤销。<b>仅代表栈内状态</b>，不含 UI 门控。</summary>
    public bool CanUndo => _transaction.Depth == 0 && _stack.HasUndoable;

    /// <summary>是否可重做。<b>仅代表栈内状态</b>，不含 UI 门控。</summary>
    public bool CanRedo => _transaction.Depth == 0 && _stack.HasRedoable;

    /// <summary>撤销菜单显示的名字；无内容时为空串。</summary>
    public string UndoName => _stack.HasUndoable ? PeekUndo().Name : string.Empty;

    /// <summary>重做菜单显示的名字；无内容时为空串。</summary>
    public string RedoName => _stack.HasRedoable ? PeekRedo().Name : string.Empty;

    /// <summary>是否已修改（未被保存）。</summary>
    public bool IsModified => _revision.IsModified;

    /// <summary>可撤销条目数。</summary>
    public int UndoCount => _stack.UndoCount;

    /// <summary>可重做条目数。</summary>
    public int RedoCount => _stack.RedoCount;

    /// <summary>事务深度，0 = 无进行中的事务。</summary>
    public int TransactionDepth => _transaction.Depth;

    /// <summary>底层双栈。</summary>
    public HistoryStack Stack => _stack;

    /// <summary>底层脏标记。</summary>
    public HistoryRevision Revision => _revision;

    /// <summary>底层事务管理器。</summary>
    public EditTransaction Transaction => _transaction;

    /// <summary>仅由历史持有的字节数。</summary>
    public long RetainedBytes => _stack.RetainedBytes();

    /// <summary>开始一次编辑事务，可嵌套。对应 <c>EditorSession.beginEdit</c>。</summary>
    /// <param name="name">撤销菜单显示的名字。</param>
    public void BeginEdit(string name) => _transaction.BeginEdit(name);

    /// <summary>提交事务。对应 <c>EditorSession.endEdit</c>。</summary>
    public void CommitEdit() => _transaction.Commit();

    /// <summary>取消事务并撤销其全部副作用。</summary>
    public void CancelEdit() => _transaction.Cancel();

    /// <summary>撤销一步。<b>调用方负责把返回的 entry 应用回文档</b>。</summary>
    /// <returns>被应用撤销的 entry；不可撤销时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 对应 <c>DocumentHistory.undo()</c> + <c>EditorSession.restore(_:)</c>
    /// （<c>DocumentHistory.swift:76-82</c>、<c>EditorSession.swift:612-617</c>）。
    /// 本方法只负责<b>栈的搬移与 revision 对齐</b>；文档状态的还原由调用方触发，
    /// 因为本项目不提供整文档替换入口（等 AI-1 对多 mutation 提交的裁决）。
    /// </remarks>
    public HistoryEntry? Undo()
    {
        if (_transaction.Depth != 0)
        {
            return null;
        }

        HistoryEntry? entry = _stack.PopUndo();
        if (entry is null)
        {
            return null;
        }

        entry.Revert();
        return entry;
    }

    /// <summary>重做一步。<b>调用方负责把返回的 entry 应用回文档</b>。</summary>
    /// <returns>被重做的 entry；不可重做时返回 <see langword="null"/>。</returns>
    /// <remarks>对应 <c>DocumentHistory.redo()</c>（<c>:84-90</c>）。</remarks>
    public HistoryEntry? Redo()
    {
        if (_transaction.Depth != 0)
        {
            return null;
        }

        HistoryEntry? entry = _stack.PopRedo();
        if (entry is null)
        {
            return null;
        }

        entry.Apply();
        return entry;
    }

    /// <summary>标记当前状态已保存。</summary>
    public void MarkSaved() => _revision.MarkSaved();

    /// <summary>标记某次异步保存所对应的状态已保存。</summary>
    /// <param name="savedRevision">保存开始时的 revision。</param>
    public void MarkSaved(Guid savedRevision) => _revision.MarkSaved(savedRevision);

    /// <summary>清空历史并重置脏标记。对应 <c>DocumentHistory.reset()</c>（打开 / 新建工程时）。</summary>
    public void Reset()
    {
        _stack.Clear();
        _revision.Reset();
    }

    private HistoryEntry PeekUndo()
    {
        HistoryEntry? e = _stack.PeekUndoEntry();
        return e ?? throw new InvalidOperationException("HasUndoable 为 true 时 Peek 必非空。");
    }

    private HistoryEntry PeekRedo()
    {
        HistoryEntry? e = _stack.PeekRedoEntry();
        return e ?? throw new InvalidOperationException("HasRedoable 为 true 时 Peek 必非空。");
    }
}
