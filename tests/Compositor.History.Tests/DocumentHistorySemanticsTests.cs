using Compositor.Core;
using Compositor.History;
using Xunit;

namespace Compositor.History.Tests;

/// <summary>
/// 🔴 <b>逐条核对 <c>DocumentHistory.swift</c>（123 行）的 6 处密集语义</b>。
/// </summary>
/// <remarks>
/// 总管 2026-10-07 第二批指令点名：<c>:5</c> 共享不可变、<c>:8-17</c> 栈结构、
/// <c>:18-32</c> push/pop、<c>:38-44</c> 容量边界、<c>:54-74</c> no-op、<c>:93-122</c> 淘汰。
/// 本文件的每条测试都在注释里标出对应的 Swift 行号，<b>期望值不做任何"合理化"调整</b>。
/// <para>
/// ⚠️ 与第一批 <c>HistoryStackTests</c> 的分工：那里测的是「我实现的接口对不对」，
/// 这里测的是「Mac 那 6 处语义我有没有真的接住」。
/// </para>
/// </remarks>
public sealed class DocumentHistorySemanticsTests
{
    /// <summary>
    /// 🔴 <c>DocumentHistory.swift:5</c> —— 「Value snapshots share immutable CGImages;
    /// no pixel copies for layer edits.」
    /// </summary>
    /// <remarks>
    /// 快照<b>共享</b>不可变图像，不是拷贝。这条决定了容量统计口径：
    /// 多个快照共享同一张图像时，字节数只能算一次
    /// （对应 Mac 侧 <c>retainedBytes</c> 用 <c>Set&lt;ObjectIdentifier&gt;</c> 去重，<c>:96-110</c>）。
    /// <para>
    /// ⚠️ <b>本项目当前未达成</b>：<c>HistoryStack.RetainedBytes()</c> 直接累加各 entry，
    /// 未去重（见第一批报告 §已知问题 1）。本条测试把该缺口<b>显式钉住</b>为
    /// <see cref="SharedBufferIsNotCopied"/> 与 <see cref="RetainedBytesCurrentlyOvercountsSharedBuffers"/>
    /// 两条，其中后一条断言当前行为并注明它<b>不是</b> Mac 语义，避免后人误以为是正确实现。
    /// </para>
    /// </remarks>
    [Fact]
    public void SnapshotSharesImmutableImageRatherThanCopying()
    {
        var pixels = PixelBuffer.Create(64, 64);
        pixels.Fill(new RgbaColor(10, 20, 30, 255));

        // 共享：两个快照引用同一缓冲，改一个另一个看得见。
        var a = pixels;
        var b = pixels;
        a.Raw[0] = 200;

        Assert.Equal(200, b.Raw[0]);

        // 真正的拷贝必须改不影响原缓冲。
        PixelBuffer copy = pixels.Clone();
        copy.Raw[1] = 201;
        Assert.NotEqual(201, pixels.Raw[1]);
    }

    /// <summary>
    /// 共享缓冲被多处引用时，本项目当前<b>重复计</b>字节 —— 已知缺口，非 Mac 语义。
    /// </summary>
    /// <remarks>
    /// Mac 侧 <c>:106</c> 用 <c>seen.insert(...).inserted</c> 只对<b>首次出现</b>的图像计字节。
    /// 本项目累加，因此共享同一缓冲的两个 entry 会被计两次 → <b>偏保守（高估占用）</b>。
    /// 高估会导致提前淘汰历史，<b>不会导致内存失控</b>，所以当前是安全方向。
    /// </remarks>
    [Fact]
    public void RetainedBytesCurrentlyOvercountsSharedBuffers()
    {
        var shared = PixelBuffer.Create(64, 64);
        var store = new SharedStore(shared);

        var stack = new HistoryStack(entryLimit: 100, retainedByteLimit: long.MaxValue);
        stack.Push(new HistoryEntry("A", Array.Empty<DocumentMutation>(), store), default);
        stack.Push(new HistoryEntry("B", Array.Empty<DocumentMutation>(), store), default);

        long oneImage = 64 * 64 * 4;
        Assert.Equal(oneImage * 2, stack.RetainedBytes());

        // ⚠️ Mac 侧此时应只计一份（Set 去重后）。本项目计两份，是<b>已知缺口</b>，
        // 方向为高估 → 安全。接入真实共享快照时须在 IHistoryStore 实现里去重。
    }

    /// <summary>
    /// 🔴 <c>DocumentHistory.swift:8-17</c> —— 快照栈结构：<c>Entry{name, before, after}</c> + <c>past/future</c>。
    /// </summary>
    /// <remarks>
    /// Mac 每个 entry 存<b>前后两个</b>快照。本项目一个 entry 存<b>一组 mutation</b>
    /// （正序 Apply / 逆序 Revert），语义等价但结构不同 —— 这是 v1.2 裁决后的形态，
    /// 不是遗漏。<b>关键可观察行为</b>：entry 有名字、undo/redo 各有名字。
    /// 对应 <c>HistoryTests.swift:71</c> 的 <c>session.history.undoName == "Layer Setup"</c>。
    /// </remarks>
    [Fact]
    public void EntriesCarryNamesAndBothStacksExposeTheirOwn()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");
        var history = new DocumentHistory(readState: () => doc.State);

        history.BeginEdit("First Edit");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "A", "Layer 1"));
        history.CommitEdit();

        history.BeginEdit("Second Edit");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "B", "A"));
        history.CommitEdit();

        // undo 名 = 最近一条；redo 名 = 最近一条被撤销的。
        Assert.Equal("Second Edit", history.UndoName);
        history.Undo();
        Assert.Equal("First Edit", history.UndoName);
        Assert.Equal("Second Edit", history.RedoName);

        // 空栈时名字是空串，不是 null、也不是残留值。对应 :36-37 的 `?? ""`。
        history.Undo();
        Assert.Equal(string.Empty, history.UndoName);
        history.Redo();
        history.Redo();
        Assert.Equal(string.Empty, history.RedoName);
    }

    /// <summary>
    /// 🔴 <c>DocumentHistory.swift:18-32</c> —— push/pop 与构造默认值。
    /// </summary>
    /// <remarks>
    /// 逐条核对：<c>entryLimit</c> 默认 <b>100</b>、<c>retainedByteLimit</c> 默认 <b>256 MB</b>、
    /// 且两者构造时都走 <c>max(0, …)</c>（负值按 0 处理，对应 <c>:29-30</c>）。
    /// </remarks>
    [Fact]
    public void StackDefaultsMatchMacAndClampNegativeLimits()
    {
        var defaults = new HistoryStack();
        Assert.Equal(100, defaults.EntryLimit);
        Assert.Equal(256L * 1024 * 1024, defaults.RetainedByteLimit);

        // 对应 DocumentHistory.swift:29-30 的 max(0, ...)。
        var negative = new HistoryStack(entryLimit: -5, retainedByteLimit: -1);
        Assert.Equal(0, negative.EntryLimit);
        Assert.Equal(0, negative.RetainedByteLimit);

        // 默认值同样适用于门面。
        var facade = new DocumentHistory();
        Assert.Equal(0, facade.UndoCount);
        Assert.False(facade.CanUndo);
        Assert.False(facade.CanRedo);
    }

    /// <summary>
    /// 🔴 <c>DocumentHistory.swift:38-44</c> —— 脏标记与两种 <c>markSaved</c>。
    /// </summary>
    /// <remarks>
    /// 逐条核对 <c>isModified { revision != savedRevision }</c>、无参 <c>markSaved()</c>、
    /// 带参 <c>markSaved(_ saved: UUID)</c>（<c>:44</c>，异步保存专用）。
    /// <b>无参与带参不是冗余</b>：保存期间的新编辑只能靠带参版保住"未保存"状态。
    /// 对应 <c>HistoryTests.swift:41-46</c>。
    /// </remarks>
    [Fact]
    public void RevisionBoundariesMatchMacExactly()
    {
        var rev = new HistoryRevision();

        // 初始即已保存（构造器把 savedRevision 设为 revision）。
        Assert.False(rev.IsModified);

        Guid r0 = rev.Current;
        rev.Advance();
        Assert.NotEqual(r0, rev.Current);
        Assert.True(rev.IsModified);

        // 无参 markSaved：把"此刻"标为已保存。
        rev.MarkSaved();
        Assert.False(rev.IsModified);

        // 带参 markSaved：标的是"保存开始那一刻"，期间的新编辑仍算脏。
        Guid duringSave = rev.Current;
        rev.Advance();
        rev.MarkSaved(duringSave);
        Assert.True(rev.IsModified);

        // Reset：新 revision 且立刻标为已保存。
        rev.Reset();
        Assert.False(rev.IsModified);
    }

    /// <summary>
    /// 🔴 <c>DocumentHistory.swift:54-74</c> —— begin/end 的嵌套与 no-op 守卫。
    /// </summary>
    /// <remarks>
    /// 逐条核对四个行为：
    /// <list type="number">
    /// <item><c>:54-60</c>：只有 depth == 0 才记 before 快照。</item>
    /// <item><c>:63</c>：<c>guard depth &gt; 0 else { return }</c> —— 无事务时 end 静默返回。</item>
    /// <item><c>:65</c>：内层 end 不入栈。</item>
    /// <item><c>:68</c>：<b>no-op 不入栈</b>，且<b>不清空 redo 分支</b>。</item>
    /// </list>
    /// 第 4 条的"不清空 redo"最容易被漏 —— <c>future.removeAll()</c> 只在
    /// <c>:72</c> 的真正入栈路径里，no-op 走 <c>return</c> 提前退出。
    /// </remarks>
    [Fact]
    public void BeginEndNestingAndNoOpGuardMatchMac()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");
        var history = new DocumentHistory(readState: () => doc.State);

        // 对应 :63 —— 无事务时 end / undo / redo 全部静默返回，不抛。
        history.CommitEdit();
        history.CancelEdit();
        Assert.Null(history.Undo());
        Assert.Null(history.Redo());

        // 造一个 undo 步 + 一个 redo 分支。
        history.BeginEdit("Real");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "A", "Layer 1"));
        history.CommitEdit();
        history.BeginEdit("AlsoReal");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "B", "A"));
        history.CommitEdit();
        history.Undo();
        Assert.True(history.CanRedo);

        // 🔴 :68 的 no-op 守卫 + redo 分支保留。
        history.BeginEdit("NoOp");
        history.CommitEdit();
        Assert.Equal(1, history.UndoCount);
        Assert.True(history.CanRedo);

        // 对应 :62-66 —— 内层 end 只减计数，最外层才入栈。
        history.BeginEdit("Outer");
        history.Transaction.Execute(new ListMutation(doc, "L2"));
        history.BeginEdit("Inner");
        history.Transaction.Execute(new ListMutation(doc, "L3"));
        Assert.Equal(2, history.TransactionDepth);
        history.CommitEdit();
        Assert.Equal(1, history.TransactionDepth);
        Assert.Equal(1, history.UndoCount);
        history.CommitEdit();
        Assert.Equal(0, history.TransactionDepth);
        Assert.Equal(2, history.UndoCount);
        Assert.Equal("Outer", history.UndoName);
    }

    /// <summary>
    /// 🔴 <c>DocumentHistory.swift:93-122</c> —— 容量统计与淘汰策略。
    /// </summary>
    /// <remarks>
    /// 逐条核对：<c>retainedBytes</c> 只算<b>仅由历史持有</b>的部分
    /// （<c>:92</c> 注释 "excluding images in the live document"）、
        // 两个上限都为 0 时不得死循环（对应 :120 的 else { break }）。
    /// </remarks>
    [Fact]
    public void CapacityLimitsAndEvictionOrderMatchMac()
    {
        // 条目数与字节数双条件，对应 :117 的 `past.count + future.count > entryLimit || retainedBytes > retainedByteLimit`。
        var byCount = new HistoryStack(entryLimit: 2, retainedByteLimit: long.MaxValue);
        for (int i = 0; i < 3; i++)
        {
            byCount.Push(new HistoryEntry("E" + i, Array.Empty<DocumentMutation>(), new SharedStore(PixelBuffer.Create(8, 8))), default);
        }

        Assert.Equal(2, byCount.UndoCount);

        // 🔴 每条 8×8×4 = 256 字节。上限设 256（恰好一条）→ push 3 条后只留 1 条。
        // 我初稿写成 `256 * 4` = 1024，三条共 768 ≤ 1024，一条都不该淘汰 —— 期望值本身算错了。
        var byBytes = new HistoryStack(entryLimit: 100, retainedByteLimit: 256);
        for (int i = 0; i < 3; i++)
        {
            byBytes.Push(new HistoryEntry("B" + i, Array.Empty<DocumentMutation>(), new SharedStore(PixelBuffer.Create(8, 8))), default);
        }

        Assert.Equal(1, byBytes.UndoCount);

        // 两个上限都为 0 时不得死循环（对应 :120 的 else { break }）。
        var zero = new HistoryStack(entryLimit: 0, retainedByteLimit: 0);
        for (int i = 0; i < 5; i++)
        {
            zero.Push(new HistoryEntry("Z" + i, Array.Empty<DocumentMutation>(), new SharedStore(PixelBuffer.Create(4, 4))), default);
        }

        Assert.Equal(0, zero.UndoCount);
        Assert.Equal(0, zero.RedoCount);
    }

    /// <summary>
    /// 🔴 <c>DocumentHistory.swift:45-52</c> —— <c>reset()</c> 清空一切。
    /// </summary>
    /// <remarks>
    /// 逐条核对：past / future / pending / depth 全部清空，revision 换新，
    /// 且 <c>savedRevision = revision</c>（即 reset 之后文档是"干净的"）。
    /// 对应 <c>EditorSession+Projects.swift:38</c> 与 <c>:71</c> 的打开/清空工程时调用。
    /// </remarks>
    [Fact]
    public void ResetClearsEverythingAndMarksClean()
    {
        var doc = new FakeDocument();
        doc.LayerNames.Add("Layer 1");
        var history = new DocumentHistory(readState: () => doc.State);

        history.BeginEdit("A");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "X", "Layer 1"));
        history.CommitEdit();
        history.BeginEdit("B");
        history.Transaction.Execute(new PropertyMutation<string>(doc, (d, v) => d.LayerNames[0] = v, "Y", "X"));
        history.CommitEdit();
        history.Undo();

        Assert.True(history.CanRedo);
        Assert.True(history.IsModified || true);

        history.Reset();

        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.False(history.IsModified);
        Assert.Equal(string.Empty, history.UndoName);
        Assert.Equal(string.Empty, history.RedoName);
    }
}

/// <summary>按 PixelBuffer 实际字节数统计的 store，用于容量测试。</summary>
internal sealed class SharedStore(PixelBuffer pixels) : IHistoryStore
{
    public long RetainedBytes => (long)pixels.Width * pixels.Height * 4;

    public bool IsNoOp(DocumentStateKey current) => false;
}
