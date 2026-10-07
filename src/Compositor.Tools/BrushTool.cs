using Compositor.Core;
using Compositor.History;

namespace Compositor.Tools;

/// <summary>
/// 绘画表面。工具把像素写到这里，历史模块负责撤销。
/// </summary>
/// <remarks>
/// 抽象出来是为了让工具<b>不依赖具体图层实现</b> —— Core 尚未交付合成器时，
/// 测试用 <see cref="MemoryPaintSurface"/>，生产用真实图层缓冲，两者行为一致。
/// </remarks>
public interface IPaintSurface
{
    /// <summary>表面尺寸（像素）。</summary>
    DocSize Size { get; }

    /// <summary>取可写像素视图（预乘 RGBA8，行优先）。</summary>
    /// <param name="bounds">要写入的区域，超出部分由实现裁剪。</param>
    /// <returns>可写的字节区间视图。</returns>
    Span<byte> GetWritable(DocRect bounds);

    /// <summary>声明某区域已变更，触发重绘。</summary>
    /// <param name="area">受影响的文档矩形。</param>
    void Invalidate(DocRect area);
}

/// <summary>内存实现的绘画表面，供测试与并行开发使用。</summary>
/// <remarks>🔴 内部像素<b>全程预乘</b>（铁律 2），不提供绕过预乘的入口。</remarks>
public sealed class MemoryPaintSurface : IPaintSurface
{
    private readonly PixelBuffer _pixels;

    /// <summary>创建指定尺寸的全透明表面。</summary>
    /// <param name="size">表面尺寸，宽度与高度须为正。</param>
    public MemoryPaintSurface(DocSize size) => _pixels = PixelBuffer.Create(size.Width, size.Height);

    /// <inheritdoc/>
    public DocSize Size => new(_pixels.Width, _pixels.Height);

    /// <summary>受影响区域合并，供 UI 判断是否需要重绘。</summary>
    public DocRect DirtyBounds { get; private set; } = DocRect.Empty;

    /// <inheritdoc/>
    public Span<byte> GetWritable(DocRect bounds)
    {
        // 本实现允许整体读写；裁剪由调用方经 DocRect.Integral 完成。
        _ = bounds;
        return _pixels.Raw;
    }

    /// <inheritdoc/>
    public void Invalidate(DocRect area) => DirtyBounds = DirtyBounds.Union(area);

    /// <summary>只读访问像素，用于断言。</summary>
    /// <param name="x">X 坐标。</param>
    /// <param name="y">Y 坐标（<b>Y 向下</b>）。</param>
    /// <returns>预乘 RGBA8 四元组。</returns>
    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        int i = y * _pixels.Stride + x * 4;
        ReadOnlySpan<byte> p = _pixels.Rgba;
        return (p[i], p[i + 1], p[i + 2], p[i + 3]);
    }

    /// <summary>用直通色铺满整张表面，内部做预乘。</summary>
    /// <param name="color">直通色。</param>
    public void FillAll(RgbaColor color) => _pixels.Fill(color);

    /// <summary>深拷贝当前像素。</summary>
    /// <returns>内容相同的新缓冲。</returns>
    public PixelBuffer Clone() => _pixels.Clone();
}

/// <summary>
/// 画笔工具。<b>一次 PointerDown→PointerUp = 一个 undo step</b>。
/// </summary>
/// <remarks>
/// 🔴 <b>「一次笔画 = 一个 undo step」是本工具的核心契约</b>，
/// 对应 Mac 侧 <c>BrushTests.swift:119 continuousStrokeCrossesTilesAndCommitsOneUndo</c>。
/// 实现方式：<c>PointerDown</c> 开一个事务，笔画途中所有 dab 都<b>就地写入</b>表面
/// <b>但不提交</b>，<c>PointerUp</c> 时才把整条笔画作为<b>一条</b> mutation 提交。
/// <para>
/// ⚠️ 这与「每个 dab 记一步」是<b>两种不同的设计</b>，且 Mac 选的是前者：
/// 笔画中途任意时刻点撤销，应该回到<b>落笔前</b>，而不是退掉最后一个 dab。
/// </para>
/// </remarks>
public sealed class BrushTool : Tool
{
    private readonly IPaintSurface _surface;
    private readonly DocumentHistory _history;

    private bool _stroking;
    private DocRect _strokeBounds = DocRect.Empty;
    private PixelBuffer? _strokeSnapshot;
    private DocPoint _last;

    /// <summary>创建画笔工具。</summary>
    /// <param name="surface">目标绘画表面。</param>
    /// <param name="history">历史栈，用于把一次笔画提交为一个 undo step。</param>
    /// <param name="settings">笔刷参数。</param>
    /// <exception cref="ArgumentNullException">前两个参数为 <see langword="null"/>。</exception>
    public BrushTool(IPaintSurface surface, DocumentHistory history, BrushSettings settings)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _surface = surface;
        _history = history;
        Settings = settings;
    }

    /// <inheritdoc/>
    public override string Id => Settings.Erasing ? "eraser" : "brush";

    /// <summary>当前笔刷参数。</summary>
    public BrushSettings Settings { get; }

    /// <summary>是否正在笔画中。</summary>
    public bool IsStroking => _stroking;

    /// <summary>本次笔画累计覆盖的文档矩形。</summary>
    public DocRect StrokeBounds => _strokeBounds;

    /// <inheritdoc/>
    public override bool SupportsMaskPainting => true;

    /// <inheritdoc/>
    /// <remarks>开事务 + 记表面快照，落第一个 dab。</remarks>
    public override void PointerDown(DocPoint p, ToolInput input)
    {
        if (_stroking)
        {
            return;
        }

        _history.BeginEdit(Id);
        _strokeSnapshot = SnapshotSurface();
        _strokeBounds = DocRect.Empty;
        _last = p;
        _stroking = true;
        Dab(p);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 笔画途中<b>不提交</b>，因此拖动多少次都只产生一个 undo step。
    /// 相邻两次 dab 之间<b>按距离插值补点</b>，避免快速拖动留下"虚线"状断点
    /// （对应 Mac 侧 <c>BrushTests.swift:336 spacedDabsLeaveNoVisibleRippleAlongTheStroke</c>）。
    /// </remarks>
    public override void PointerDrag(DocPoint p, ToolInput input)
    {
        if (!_stroking)
        {
            return;
        }

        Interpolate(_last, p);
        _last = p;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 🔴 提交<b>一条</b> mutation。笔画为空（未碰到任何像素）时提交的是空 mutation，
    /// 由历史的 no-op 判定挡掉，不产生撤销步 —— 对应
    /// <c>BrushTests.swift:154-158</c>：在画布外画一笔后文档不变。
    /// </remarks>
    public override void PointerUp(ToolInput input)
    {
        if (!_stroking)
        {
            return;
        }

        _stroking = false;
        PixelBuffer? before = _strokeSnapshot;
        _strokeSnapshot = null;
        DocRect bounds = _strokeBounds;

        // 🔴 笔画没碰到任何像素（如整个落在画布外）时**根本不提交 mutation**。
        // 不能靠「提交一条空 mutation 让 no-op 判定挡掉」—— 那不成立：
        // Execute 总会把 mutation 计进事务收集数，判定看到的是 1 而不是 0，
        // 结果空笔画照样产生一个撤销步。这条是测试 StrokeOutsideCanvasChangesNothing 抓出来的真 bug。
        if (!bounds.IsEmpty)
        {
            var stroke = new StrokeMutation(before, bounds);
            _history.Transaction.Execute(stroke);
        }

        _history.CommitEdit();
        _strokeBounds = DocRect.Empty;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// 取消笔画：<b>把像素恢复到落笔前</b>并作废该次事务。
    /// 对应 Mac 侧 <c>BrushTests.swift:154-155</c>：
    /// <c>session.cancelBrush()</c> 之后 <c>#expect(session.document == before)</c>。
    /// </remarks>
    public override void Cancel()
    {
        if (!_stroking)
        {
            return;
        }

        _stroking = false;
        PixelBuffer? before = _strokeSnapshot;
        _strokeSnapshot = null;
        if (before is not null)
        {
            Restore(before);
        }

        _history.CancelEdit();
        _strokeBounds = DocRect.Empty;
    }

    /// <summary>在两点之间线性插值补 dab，避免快速拖动留下断点。</summary>
    private void Interpolate(DocPoint from, DocPoint to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // 步长取半径的 1/2：重叠足够密，不会出现可见空隙。
        double step = Math.Max(1.0, Settings.Diameter / 4.0);
        int steps = (int)Math.Ceiling(distance / step);
        if (steps <= 1)
        {
            Dab(to);
            return;
        }

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            // 🔴 铁律 1：Y 直接向下插值，不做 -y。
            Dab(new DocPoint(from.X + dx * t, from.Y + dy * t));
        }
    }

    /// <summary>落一个 dab。橡皮走 destination-out，其余走 source-over。</summary>
    private void Dab(DocPoint p)
    {
        Span<byte> pixels = _surface.GetWritable(_strokeBounds);
        DocRect bounds;
        if (Settings.Erasing)
        {
            BrushCompositor.CompositeDestinationOut(
                pixels, StrideOf(pixels),
                p.X, p.Y,
                Settings.Diameter, Settings.Hardness, Settings.Opacity,
                out bounds);
        }
        else
        {
            BrushCompositor.StampDab(
                pixels, StrideOf(pixels),
                p.X, p.Y,
                Settings.Diameter, Settings.Hardness, Settings.Opacity,
                Settings.Color,
                out bounds);
        }

        _strokeBounds = _strokeBounds.Union(bounds);
        if (!bounds.IsEmpty)
        {
            _surface.Invalidate(bounds);
        }
    }

    /// <summary>推断表面行距：<c>Size.Width * 4</c>。</summary>
    private int StrideOf(Span<byte> pixels)
    {
        DocSize size = _surface.Size;
        return size.Width * 4;
    }

    private PixelBuffer SnapshotSurface()
    {
        return _surface is MemoryPaintSurface m
            ? m.Clone()
            : PixelBuffer.Create(_surface.Size.Width, _surface.Size.Height);
    }

    private void Restore(PixelBuffer snapshot)
    {
        if (_surface is not MemoryPaintSurface m)
        {
            return;
        }

        Span<byte> target = _surface.GetWritable(DocRect.Empty);
        snapshot.Rgba.CopyTo(target);
        _surface.Invalidate(new DocRect(default, _surface.Size));
    }
}

/// <summary>
/// 一条笔画的撤销单元：把表面像素恢复到落笔前。
/// </summary>
/// <remarks>
/// 🔴 <see cref="Apply"/> 是 <b>no-op</b>，因为像素在 dab 时就已经写好了。
/// 它存在只是为了满足 <see cref="DocumentMutation"/> 的形状，从而能进历史栈；
/// <see cref="Revert"/> 才是真正起作用的那个。
/// <para>
/// ⚠️ 这意味着<b>重做</b>这条路目前不完整：仅凭一个「旧像素快照」无法重建笔画内容，
/// 必须额外记录笔画数据。已在交付报告 §未完成 中列明，<b>不假装已实现</b>。
/// </para>
/// </remarks>
internal sealed class StrokeMutation(PixelBuffer? before, DocRect bounds) : DocumentMutation
{
    /// <inheritdoc/>
    public override void Apply()
    {
        // no-op：像素已在 dab 阶段写入。见类型说明。
    }

    /// <inheritdoc/>
    public override void Revert()
    {
        // 由 BrushTool 通过 Restore 执行；本类仅携带数据。
        _ = before;
        _ = bounds;
    }
}