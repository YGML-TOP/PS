namespace Compositor.History;

/// <summary>
/// 脏标记：<c>isModified</c> = <c>revision != savedRevision</c>。
/// </summary>
/// <remarks>
/// 🔴 对应 Mac 侧 <c>DocumentHistory.swift:20-21</c>（<c>revision</c> / <c>savedRevision</c>）
/// 与 <c>:38</c>（<c>isModified</c>）、<c>:40-44</c>（两个 <c>markSaved</c>）。
/// <para>
/// <b>两种 markSaved 不是冗余，是两个场景</b>：
/// <list type="bullet">
/// <item><see cref="MarkSaved()"/> —— 保存的是<b>此刻</b>的文档状态。</item>
/// <item><see cref="MarkSaved(Guid)"/> —— 某次保存<b>开始时</b>的 revision。
/// 保存是异步的（任务书 §5 第 4 项），保存过程中用户还能继续编辑；
/// 写盘完成时必须标记的是<b>开始保存那一刻</b>的 revision，
/// 否则保存期间的新编辑会被错误地当成「已保存」。</item>
/// </list>
/// 这正是 Mac 侧 <c>:44</c> 注释 <c>"A save of `saved` finished. Edits made while it was
/// writing leave the document modified"</c> 的含义。
/// </para>
/// </remarks>
public sealed class HistoryRevision
{
    private Guid _revision = Guid.NewGuid();
    private Guid _savedRevision;

    /// <summary>构造并把当前状态标记为「已保存」。</summary>
    public HistoryRevision() => _savedRevision = _revision;

    /// <summary>当前 revision。任何产生变化的编辑都会让它变化。</summary>
    public Guid Current => _revision;

    /// <summary>是否已修改（当前 revision ≠ 已保存 revision）。</summary>
    public bool IsModified => _revision != _savedRevision;

    /// <summary>标记「当前状态」已保存。对应 <c>DocumentHistory.swift:40</c>。</summary>
    public void MarkSaved() => _savedRevision = _revision;

    /// <summary>
    /// 标记「<paramref name="savedRevision"/> 所代表的那个状态」已保存。
    /// 对应 <c>DocumentHistory.swift:44</c>。
    /// </summary>
    /// <param name="savedRevision">保存开始时的 revision。</param>
    /// <remarks>
    /// 保存期间的新编辑<b>不会</b>被清脏 —— 它们仍然算未保存。
    /// </remarks>
    public void MarkSaved(Guid savedRevision) => _savedRevision = savedRevision;

    /// <summary>撤销 / 重做后把当前状态对齐到某个已知 revision。</summary>
    /// <remarks>
    /// 对应 <c>DocumentHistory.undo/redo</c> 里的 <c>revision = entry.before.revision</c>
    /// / <c>entry.after.revision</c>（<c>:79</c> / <c>:87</c>）。
    /// <para>
    /// 🔴 这就是 <c>HistoryTests.swift:45-46</c> 那个断言的机制：
    /// 保存后改名 → <c>isModified == true</c>；undo 回到保存时的 revision → <c>isModified == false</c>。
    /// 撤销<b>能</b>把脏标记清回干净，靠的就是这里对齐到 before 的 revision。
    /// </para>
    /// </remarks>
    /// <param name="revision">目标 revision。</param>
    public void SetCurrent(Guid revision) => _revision = revision;

    /// <summary>推进到新 revision（任何产生变化的编辑都要调）。</summary>
    /// <remarks>对应 <c>DocumentHistory.swift:69</c> 的 <c>revision = UUID()</c>。</remarks>
    /// <returns>新的 revision。</returns>
    public Guid Advance()
    {
        _revision = Guid.NewGuid();
        return _revision;
    }

    /// <summary>重置：清空历史并把当前状态标记为已保存。对应 <c>DocumentHistory.reset()</c>。</summary>
    public void Reset()
    {
        _revision = Guid.NewGuid();
        _savedRevision = _revision;
    }
}
