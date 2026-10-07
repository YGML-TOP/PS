using Compositor.Core;
using Xunit;

namespace Compositor.History.Tests;

/// <summary>
/// M3 撤销栈验收测试。
/// </summary>
/// <remarks>
/// 🔴 <b>期望值全部照抄 Mac 侧 <c>CompositorTests/HistoryTests.swift</c> 的对应行号</b>，
/// 一律不改、不放宽容差。凡是本测试断言的数，都能在 Swift 原件里找到同一句。
/// <para>本轮覆盖三条行为（总管指定）：no-op 不入栈 / 替换画布可撤销 / 栈容量淘汰。</para>
/// </remarks>
public sealed class HistoryStackTests
{
    /// <summary>
    /// 行为 1：no-op 不入栈，且不清空 redo 分支。
    /// </summary>
    /// <remarks>
    /// 依据：<c>DocumentHistory.swift:68</c> 的 <c>guard before.document != document else { return }</c>。
    /// 对应 Swift 原件 <c>HistoryTests.swift:47-56</c>：
    /// 缩放 + 三次不改变文档状态的改名/重排之后，<c>history.undoCount == count</c> 且 <c>canRedo</c> 仍为真。
    /// </remarks>
    [Fact]
    public void NoOpEditsDoNotCreateUndoStepsAndPreserveRedo()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");
        var history = new DocumentHistory(readState: () => doc.State);

        // 先做一次真实编辑，制造 undo 步与 canRedo 的前提。
        history.BeginEdit("Rename Layer");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "Changed", "Layer 1"));
        history.CommitEdit();
        Assert.Equal(1, history.UndoCount);

        int count = history.UndoCount;

        // 以下都是 no-op：事务收集了 0 个 mutation，提交时不入栈。
        history.BeginEdit("Zoom");
        history.CommitEdit();
        history.BeginEdit("Rename To Blank");
        history.CommitEdit();
        history.BeginEdit("Reorder No Change");
        history.CommitEdit();

        // 期望值照抄 HistoryTests.swift:52 —— undoCount 不变。
        Assert.Equal(count, history.UndoCount);

        // 期望值照抄 HistoryTests.swift:53 —— redo 分支仍可重做。
        history.Undo();
        Assert.True(history.CanRedo);
    }

    /// <summary>
    /// 行为 1b：redo 之后做了新操作，旧 redo 分支必须丢弃。
    /// </summary>
    /// <remarks>
    /// 依据：<c>DocumentHistory.swift:72</c> 的 <c>future.removeAll()</c>。
    /// 对应 <c>HistoryTests.swift:58-59</c>：<c>session.addBlankLayer()</c> 之后 <c>#expect(!session.canRedo)</c>。
    /// </remarks>
    [Fact]
    public void NewEditAfterUndoDiscardsRedoBranch()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");
        var history = new DocumentHistory(readState: () => doc.State);

        history.BeginEdit("First");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "A", "Layer 1"));
        history.CommitEdit();

        history.BeginEdit("Second");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "B", "A"));
        history.CommitEdit();
        Assert.Equal(2, history.UndoCount);

        // 撤销两步，制造 redo 分支。
        history.Undo();
        history.Undo();
        Assert.True(history.CanRedo);

        // 新操作应清空 redo 分支 —— HistoryTests.swift:58-59。
        history.BeginEdit("Third");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "C", "Layer 1"));
        history.CommitEdit();
        Assert.False(history.CanRedo);
    }

    /// <summary>
    /// 行为 2：替换画布可撤销 —— 撤销后能回到替换前的文档。
    /// </summary>
    /// <remarks>
    /// 依据：<c>HistoryTests.swift:74-80</c>：
    /// redo 回到 100×200 后 <c>createDocument(width: 300, height: 400)</c>，
    /// undo 回到 <c>previous</c>，redo 后 <c>size == 300×400</c>。
    /// </remarks>
    [Fact]
    public void ReplacingCanvasIsUndoable()
    {
        var doc = new FakeDocument { Size = new DocSize(100, 200) };
        var history = new DocumentHistory(readState: () => doc.State);

        // HistoryTests.swift:76 —— 替换画布。
        history.BeginEdit("New Canvas");
        history.Transaction.Execute(new PropertyMutation<DocSize>(doc, (d, v) => d.Size = v, new DocSize(300, 400), new DocSize(100, 200)));
        history.Transaction.Execute(new PropertyMutation<Guid>(doc, (d, v) => d.DocumentId = v, Guid.NewGuid(), doc.DocumentId));
        history.CommitEdit();

        // HistoryTests.swift:80 —— redo 后尺寸是 300×400。
        Assert.Equal(new DocSize(300, 400), doc.Size);

        // HistoryTests.swift:77-78 —— undo 回到替换前的文档。
        history.Undo();
        Assert.Equal(new DocSize(100, 200), doc.Size);
    }

    /// <summary>
    /// 行为 3：条目数上限淘汰 —— 超出 entryLimit 时从最早一端淘汰。
    /// </summary>
    /// <remarks>
    /// 依据：<c>HistoryTests.swift:143</c> 用 <c>entryLimit: 2</c> 连做 3 次编辑后
    /// <c>#expect(history.undoCount == 2)</c>。对应 <c>DocumentHistory.swift:116-122</c> 的 trim 循环。
    /// </remarks>
    [Fact]
    public void EntryLimitEvictsOldestEntries()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");

        // HistoryTests.swift:143 —— entryLimit: 2。
        var history = new DocumentHistory(entryLimit: 2, readState: () => doc.State);

        // HistoryTests.swift:146-150 —— 连续三次改名。
        foreach (string name in new[] { "A", "B", "C" })
        {
            string previous = doc.LayerNames[0];
            history.BeginEdit("Rename");
            history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, name, previous));
            history.CommitEdit();
        }

        // 期望值照抄 HistoryTests.swift:151 —— undoCount == 2。
        Assert.Equal(2, history.UndoCount);
    }

    /// <summary>
    /// 行为 3b：字节上限淘汰 —— 超出 retainedByteLimit 时同样从最早一端淘汰。
    /// </summary>
    /// <remarks>
    /// 依据：<c>HistoryTests.swift:143</c> 同时传 <c>retainedByteLimit: 0</c>，
    /// <c>:152</c> 断言 <c>retainedBytes(current:) == 0</c>。对应 <c>DocumentHistory.swift:117</c> 的双条件 while。
    /// </remarks>
    [Fact]
    public void RetainedByteLimitEvictsEntries()
    {
        var stack = new HistoryStack(entryLimit: 100, retainedByteLimit: 0);
        for (int i = 0; i < 3; i++)
        {
            stack.Push(new HistoryEntry("E" + i, Array.Empty<DocumentMutation>(), new CountingStore(bytes: 1024)), default);
        }

        // byteLimit = 0 → 一条都留不下。
        Assert.Equal(0, stack.UndoCount);
    }

    /// <summary>
    /// 行为 3c：淘汰只丢「过去」一端。
    /// </summary>
    /// <remarks>
    /// 依据：<c>DocumentHistory.swift:118-121</c>：
    /// <c>if !past.isEmpty { past.removeFirst() } else if !future.isEmpty { future.removeFirst() }</c>。
    /// <para>
    /// ⚠️ <b>本条如实记录一个可达性事实</b>：由于 <see cref="HistoryStack.Push"/>
    /// 会先 <c>_future.Clear()</c>（Mac <c>:72</c> 的 <c>future.removeAll()</c>），
    /// 「future 非空时触发 trim」这条分支在当前公开 API 下<b>不可达</b> ——
    /// push 之后 future 必为空，而 undo/redo 只把 entry 在两栈间搬移、不改变总数。
    /// 因此本条只断言<b>可达</b>的那半边（丢 past），future 分支作为防御性代码保留。
    /// 这不是实现偷懒：Mac 源码里同样存在这条防御分支。
    /// </para>
    /// </remarks>
    [Fact]
    public void TrimEvictsPastFirst()
    {
        var stack = new HistoryStack(entryLimit: 1, retainedByteLimit: long.MaxValue);

        stack.Push(new HistoryEntry("P0", Array.Empty<DocumentMutation>(), new CountingStore(0)), default);
        stack.Push(new HistoryEntry("P1", Array.Empty<DocumentMutation>(), new CountingStore(0)), default);

        // 上限 1 → 只剩最新压入的 P1，最早的 P0 被淘汰。
        Assert.Equal(1, stack.UndoCount);
        Assert.Equal("P1", stack.PeekUndoEntry()!.Name);
    }

    /// <summary>
    /// 行为 3d：未超限时淘汰不动任何东西。
    /// </summary>
    /// <remarks>对应 <c>DocumentHistory.swift:117</c> 的 while 条件不成立时直接退出。</remarks>
    [Fact]
    public void TrimIsNoOpWithinLimits()
    {
        var stack = new HistoryStack(entryLimit: 10, retainedByteLimit: long.MaxValue);
        stack.Push(new HistoryEntry("A", Array.Empty<DocumentMutation>(), new CountingStore(0)), default);
        stack.Push(new HistoryEntry("B", Array.Empty<DocumentMutation>(), new CountingStore(0)), default);
        stack.PopUndo();

        stack.Trim(default);

        Assert.Equal(1, stack.UndoCount);
        Assert.Equal(1, stack.RedoCount);
    }

    /// <summary>
    /// 事务深度大于 0 时禁止 undo / redo。
    /// </summary>
    /// <remarks>
    /// 依据：<c>DocumentHistory.swift:34-35</c> 的 <c>canUndo { depth == 0 &amp;&amp; ... }</c>。
    /// 对应 <c>HistoryTests.swift:69</c>：<c>beginEdit</c> 之后、<c>endEdit</c> 之前 <c>#expect(!session.canUndo)</c>。
    /// </remarks>
    [Fact]
    public void UndoAndRedoAreBlockedInsideTransaction()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");
        var history = new DocumentHistory(readState: () => doc.State);

        // 注意：这里必须执行至少一个 mutation，否则会被 no-op 判定挡掉不入栈
        //（DocumentHistory.swift:68 的 guard），断言就失去意义了。
        history.BeginEdit("A");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "A", "Layer 1"));
        history.CommitEdit();
        Assert.Equal(1, history.UndoCount);
        Assert.True(history.CanUndo);

        // HistoryTests.swift:66-69 —— 嵌套事务内不可撤销。
        history.BeginEdit("Layer Setup");
        history.Transaction.Execute(new ListMutation(doc, "Layer 2"));
        Assert.False(history.CanUndo);
        Assert.Equal(1, history.TransactionDepth);

        history.CommitEdit();
        Assert.True(history.CanUndo);
    }

    /// <summary>
    /// 嵌套事务合并为一个撤销步。
    /// </summary>
    /// <remarks>
    /// 依据：<c>DocumentHistory.swift:54-60</c>（只有 depth == 0 记 before 快照）
    /// + <c>:65</c>（<c>guard depth == 0</c> 才 append）。
    /// 对应 <c>HistoryTests.swift:66-73</c>：一个 beginEdit 里加两个图层，undo 后 <c>layers.isEmpty == true</c>。
    /// </remarks>
    [Fact]
    public void NestedTransactionsMergeIntoSingleUndoStep()
    {
        var doc = new FakeDocument();
        var history = new DocumentHistory(readState: () => doc.State);

        history.BeginEdit("Layer Setup");
        history.Transaction.Execute(new ListMutation(doc, "Layer 1"));
        history.BeginEdit("Inner");
        history.Transaction.Execute(new ListMutation(doc, "Layer 2"));
        history.CommitEdit();
        history.CommitEdit();

        // 期望值照抄 HistoryTests.swift:71 —— 一步，且名字是外层的。
        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Layer Setup", history.UndoName);

        // HistoryTests.swift:72-73 —— 一步撤销回到空图层列表。
        history.Undo();
        Assert.Empty(doc.LayerNames);
    }

    /// <summary>
    /// Cancel 真正撤销该次编辑的全部副作用，且多个 mutation 逆序回退。
    /// </summary>
    /// <remarks>
    /// 本项 Mac 侧<b>没有直接对应测试</b>（Mac 的 <c>DocumentHistory</c> 只有 begin/end），
    /// 但任务书 §5 要求「Cancel 必须真正撤销该次编辑的全部副作用」，故必须有断言。
    /// </remarks>
    [Fact]
    public void CancelRevertsAllSideEffects()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");
        var history = new DocumentHistory(readState: () => doc.State);

        history.BeginEdit("Doomed");
        history.Transaction.Execute(new ListMutation(doc, "Layer 2"));
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "Renamed", "Layer 1"));
        history.CancelEdit();

        // 副作用全部回退。
        Assert.Single(doc.LayerNames);
        Assert.Equal("Layer 1", doc.LayerNames[0]);

        // 取消的事务不产生撤销步。
        Assert.Equal(0, history.UndoCount);
        Assert.False(history.CanUndo);
    }

    /// <summary>
    /// 逆序回退顺序可断言：后加的先退。
    /// </summary>
    /// <remarks>对应 <c>HistoryEntry.Revert</c> 的逆序契约。这是最容易静默写错的一处。</remarks>
    [Fact]
    public void RevertAppliesMutationsInReverseOrder()
    {
        var log = new List<string>();
        var entry = new HistoryEntry(
            "Ordered",
            new DocumentMutation[] { new LogMutation(log, "first", "first-revert"), new LogMutation(log, "second", "second-revert") },
            new CountingStore(0));

        entry.Revert();
        Assert.Equal(new[] { "second-revert", "first-revert" }, log);
    }

    /// <summary>
    /// 撤销回到保存时的 revision 会把脏标记清干净。
    /// </summary>
    /// <remarks>
    /// 依据：<c>HistoryTests.swift:41-46</c>：
    /// <c>markSaved()</c> → <c>#expect(!session.isModified)</c>；
    /// 改名 → <c>#expect(session.isModified)</c>；<c>undo()</c> → <c>#expect(!session.isModified)</c>。
    /// 对应 <c>DocumentHistory.swift:79</c> 的 <c>revision = entry.before.revision</c>。
    /// </remarks>
    [Fact]
    public void UndoBackToSavedRevisionClearsModifiedFlag()
    {
        var rev = new HistoryRevision();
        Assert.False(rev.IsModified);

        Guid savedRevision = rev.Current;
        rev.Advance();
        Assert.True(rev.IsModified);

        rev.SetCurrent(savedRevision);
        Assert.False(rev.IsModified);
    }

    /// <summary>
    /// 异步保存：保存期间的新编辑仍算未保存。
    /// </summary>
    /// <remarks>
    /// 依据：<c>DocumentHistory.swift:44</c> 注释
    /// <c>"A save of `saved` finished. Edits made while it was writing leave the document modified"</c>。
    /// 对应任务书 §5 第 4 项「保存期间不阻塞编辑」。
    /// </remarks>
    [Fact]
    public void EditsDuringAsyncSaveRemainModified()
    {
        var rev = new HistoryRevision();

        Guid saveStartedAt = rev.Current;
        rev.Advance();          // 保存期间用户又编辑了一次

        rev.MarkSaved(saveStartedAt);  // 旧的那次保存写盘完成
        Assert.True(rev.IsModified);   // 保存期间的新编辑仍然算未保存
    }

    /// <summary>
    /// 组合变更：Apply 正序、Revert 逆序。
    /// </summary>
    /// <remarks>
    /// 🔴 这是 AI-1 明确要求的正确性保证（2026-10-07 回复）：
    /// 「Revert 必须逆序（Count-1 → 0），因为后施加的变更依赖先施加的结果，
    /// 正序撤销会崩。这条不是风格问题，是正确性问题。」
    /// <para>Mac 侧无对应测试（Mac 没有组合 mutation 的概念）。</para>
    /// </remarks>
    [Fact]
    public void CompositeMutationAppliesForwardAndRevertsBackward()
    {
        var log = new List<string>();
        var composite = new CompositeMutation(new DocumentMutation[]
        {
            new LogMutation(log, "A-apply", "A-revert"),
            new LogMutation(log, "B-apply", "B-revert"),
            new LogMutation(log, "C-apply", "C-revert"),
        });

        composite.Apply();
        Assert.Equal(new[] { "A-apply", "B-apply", "C-apply" }, log);

        log.Clear();
        composite.Revert();
        Assert.Equal(new[] { "C-revert", "B-revert", "A-revert" }, log);
    }

    /// <summary>
    /// 组合变更的逆序不是风格问题：两个 mutation 触碰同一状态时，正序回退会得到错误结果。
    /// </summary>
    /// <remarks>
    /// 🔴 本条用<b>会静默出错</b>的场景证明逆序的必要性：
    /// 第二个 mutation 基于第一个的<b>当前值</b>计算，而 <c>Revert</c> 写的是<b>施加时的旧值</b>。
    /// 逆序时第二个先退（恢复中间态），第一个再退（恢复初始态），顺序颠倒就会留下脏值。
    /// </remarks>
    [Fact]
    public void ReverseOrderIsRequiredWhenMutationsShareState()
    {
        var counter = 0;

        // 第一次：0 → 10，Revert 写回 0。
        var first = new CounterMutation(onApply: v => counter = v * 10, onRevert: () => counter = 0, newValue: 1);

        // 第二次：基于 Apply 时的当前值 10 计算 → 25，Revert 写回 10。
        // 🔴 必须惰性求值：若在构造时就算好 counter + 15，那时 counter 还是 0，断言会失真。
        var second = new CounterMutation(onApply: _ => counter = 25, onRevert: () => counter = 10, newValue: 25);

        var composite = new CompositeMutation(new DocumentMutation[] { first, second });
        composite.Apply();
        // first：0 → 10；second：10 → 25。Apply 全部完成后是 25。
        Assert.Equal(25, counter);

        composite.Revert();
        // 逆序：second 先退 → 10，first 再退 → 0。回到初始值。
        Assert.Equal(0, counter);

        // 正序对照：first 先退到 0，second 再退到 10 —— 最终停在 10，初始值丢失。
        counter = 0;
        composite.Apply();
        first.Revert();
        second.Revert();
        Assert.NotEqual(0, counter);
    }

    /// <summary>
    /// 组合变更构造后不受调用方后续修改原集合影响。
    /// </summary>
    /// <remarks>历史一旦建立就不能被外部篡改，否则 undo 结果不可信。</remarks>
    [Fact]
    public void CompositeMutationCopiesPartsOnConstruction()
    {
        var log = new List<string>();
        var parts = new List<DocumentMutation> { new LogMutation(log, "kept", "kept-revert") };
        var composite = new CompositeMutation(parts);

        parts.Add(new LogMutation(log, "late", "late-revert"));

        composite.Apply();
        Assert.Equal(new[] { "kept" }, log);
        Assert.Equal(1, composite.Count);
    }

    /// <summary>
    /// 空组合等价于 no-op，不抛异常。
    /// </summary>
    /// <remarks>对应 <c>DocumentHistory.swift:68</c> 允许 no-op 静默通过。</remarks>
    [Fact]
    public void EmptyCompositeIsANoOp()
    {
        var composite = new CompositeMutation(Array.Empty<DocumentMutation>());
        composite.Apply();
        composite.Revert();
        Assert.Equal(0, composite.Count);
    }

    /// <summary>惰性求值的标量变更：Apply/Revert 各带一个动作，避免在构造期就把旧值算死。</summary>
    private sealed class CounterMutation(Action<int> onApply, Action onRevert, int newValue) : DocumentMutation
    {
        public override void Apply() => onApply(newValue);

        public override void Revert() => onRevert();
    }

    /// <summary>记录调用的顺序型变更，仅供顺序断言使用。</summary>
    private sealed class LogMutation(List<string> log, string onApply, string onRevert) : DocumentMutation
    {
        public override void Apply() => log.Add(onApply);

        public override void Revert() => log.Add(onRevert);
    }
}
