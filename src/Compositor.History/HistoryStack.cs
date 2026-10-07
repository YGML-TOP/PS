namespace Compositor.History;

/// <summary>
/// 撤销/重做双栈 + 容量淘汰。<b>只负责「历史栈本身能不能操作」，不负责「此刻是否允许操作」</b>。
/// </summary>
/// <remarks>
/// 🔴 <b>边界（总管 2026-10-07 裁决）</b>：导入中 / 对话框打开时的禁用策略属 UI 层，归 AI-6。
/// <b>不判断 <c>isImporting</c> 之类状态。UI 门控建在本类之上</b>，
/// 对应 Mac 侧 <c>EditorSession.canUseHistory</c>（<c>EditorSession.swift:605-608</c>）那一长串条件。
/// </remarks>
public sealed class HistoryStack
{
    private readonly List<HistoryEntry> _past = new();
    private readonly List<HistoryEntry> _future = new();

    /// <summary>构造历史栈。</summary>
    /// <param name="entryLimit">
    /// 条目上限。超出时从<b>最早</b>的一端淘汰。对应 Mac 侧 <c>entryLimit</c>（默认 100）。
    /// </param>
    /// <param name="retainedByteLimit">
    /// 仅由历史持有的字节上限。对应 Mac 侧 <c>retainedByteLimit</c>（默认 256 MB）。
    /// 传入负值按 0 处理（对应 <c>max(0, ...)</c>）。
    /// </param>
    public HistoryStack(int entryLimit = 100, long retainedByteLimit = 256L * 1024 * 1024)
    {
        EntryLimit = Math.Max(0, entryLimit);
        RetainedByteLimit = Math.Max(0, retainedByteLimit);
    }

    /// <summary>条目上限。</summary>
    public int EntryLimit { get; }

    /// <summary>仅由历史持有的字节上限。</summary>
    public long RetainedByteLimit { get; }

    /// <summary>是否还有可撤销的条目。</summary>
    /// <remarks>
    /// 对应 <c>DocumentHistory.swift:34</c>：<c>canUndo { depth == 0 &amp;&amp; !past.isEmpty }</c>。
    /// 🔴 <b><c>depth == 0</c> 这一半不能省</b> —— 事务未提交时不允许撤销到半途状态。
    /// </remarks>
    public bool HasUndoable => _past.Count > 0;

    /// <summary>是否还有可重做的条目。</summary>
    /// <remarks>对应 <c>DocumentHistory.swift:35</c>，同样带 <c>depth == 0</c> 条件。</remarks>
    public bool HasRedoable => _future.Count > 0;

    /// <summary>可撤销条目数。对应 <c>history.undoCount</c>。</summary>
    public int UndoCount => _past.Count;

    /// <summary>可重做条目数。</summary>
    public int RedoCount => _future.Count;

    /// <summary>压栈一条新的撤销步，<b>并丢弃整条 redo 分支</b>。</summary>
    /// <remarks>
    /// 对应 <c>DocumentHistory.swift:72</c> 的 <c>future.removeAll()</c>。
    /// 🔴 undo 之后做了新操作，旧 redo 分支必须丢弃 —— 这是所有编辑器的统一行为。
    /// </remarks>
    /// <param name="entry">要压入的撤销步。</param>
    /// <param name="current">编辑后的文档状态，用于容量淘汰时估算「当前文档仍在持有的部分」。</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> 为 <see langword="null"/>。</exception>
    public void Push(HistoryEntry entry, DocumentStateKey current)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _past.Add(entry);
        _future.Clear();
        Trim(current);
    }

    /// <summary>弹栈：取走最靠后的撤销步，放进 redo 分支。</summary>
    /// <returns>被取走的撤销步；无可撤销时返回 <see langword="null"/>。</returns>
    public HistoryEntry? PopUndo()
    {
        if (_past.Count == 0)
        {
            return null;
        }

        HistoryEntry entry = _past[^1];
        _past.RemoveAt(_past.Count - 1);
        _future.Add(entry);
        return entry;
    }

    /// <summary>弹栈：取走最靠前的重做步，放回撤销分支。</summary>
    /// <returns>被取走的重做步；无可重做时返回 <see langword="null"/>。</returns>
    public HistoryEntry? PopRedo()
    {
        if (_future.Count == 0)
        {
            return null;
        }

        HistoryEntry entry = _future[^1];
        _future.RemoveAt(_future.Count - 1);
        _past.Add(entry);
        return entry;
    }

    /// <summary>窥视最靠后的撤销步，<b>不移除</b>。供 UI 读取菜单名。</summary>
    /// <returns>最新的撤销步；无可撤销时返回 <see langword="null"/>。</returns>
    public HistoryEntry? PeekUndoEntry() => _past.Count > 0 ? _past[^1] : null;

    /// <summary>窥视最靠前的重做步，<b>不移除</b>。供 UI 读取菜单名。</summary>
    /// <returns>最新的重做步；无可重做时返回 <see langword="null"/>。</returns>
    public HistoryEntry? PeekRedoEntry() => _future.Count > 0 ? _future[^1] : null;

    /// <summary>清空两个分支。对应 <c>DocumentHistory.reset()</c>（切换 / 打开工程时调用）。</summary>
    public void Clear()
    {
        _past.Clear();
        _future.Clear();
    }

    /// <summary>当前两个分支合计持有的字节数。</summary>
    /// <remarks>
    /// 对应 <c>DocumentHistory.retainedBytes</c>。⚠️ Mac 侧那份实现对<b>共享图像去重</b>
    /// （<c>Set&lt;ObjectIdentifier&gt;</c>，<c>:96-110</c>），因为多个快照常共享同一张不可变图像。
    /// 本实现直接累加各 entry 的 <see cref="HistoryEntry.RetainedBytes"/>；
    /// 若调用方需要精确的「去重后」口径，应在 <see cref="IHistoryStore.RetainedBytes"/> 里自行去重。
    /// </remarks>
    public long RetainedBytes()
    {
        long total = 0;
        foreach (HistoryEntry e in _past)
        {
            total += e.RetainedBytes;
        }

        foreach (HistoryEntry e in _future)
        {
            total += e.RetainedBytes;
        }

        return total;
    }

    /// <summary>容量淘汰：<b>先丢过去最早的一端，再丢未来最早的一端</b>。</summary>
    /// <param name="current">当前文档状态，供实现判断哪些字节只被历史持有。</param>
    /// <remarks>
    /// 逐字对应 <c>DocumentHistory.swift:116-122</c>：
    /// <c>while past.count + future.count &gt; entryLimit || retainedBytes &gt; retainedByteLimit</c>
    /// 循环体里 <c>if !past.isEmpty { past.removeFirst() } else if !future.isEmpty { future.removeFirst() }</c>。
    /// 🔴 淘汰<b>过去优先</b>于淘汰未来：过去是用户真正用过的操作，未来只是重做机会。
    /// </remarks>
    public void Trim(DocumentStateKey current)
    {
        while (_past.Count + _future.Count > EntryLimit || RetainedBytes() > RetainedByteLimit)
        {
            if (_past.Count > 0)
            {
                _past.RemoveAt(0);
            }
            else if (_future.Count > 0)
            {
                _future.RemoveAt(0);
            }
            else
            {
                // EntryLimit == 0 且 RetainedByteLimit >= 0 时，两个分支都空才会走到这里。
                break;
            }
        }
    }
}
