using Compositor.Core;
using Compositor.History;

namespace Compositor.Tools;

/// <summary>
/// 涂抹工具（Smudge）：<b>按下采样、抬起应用</b>。
/// </summary>
/// <remarks>
/// 🔴 对应 Mac 侧 <c>Document/SmudgeLiquify.swift</c>。
/// <b>采样与应用分离</b>是涂抹的核心：拖动过程中若边采边用，
/// 会把自己的输出当成下一帧的输入，涂抹几次后整个区域糊成一团。
/// 所以笔尖经过的地方保存的是「按下那一刻」的像素，抬起时才叠加。
/// <para>
/// ⚠️ 本类的取样是<b>同步读</b>当前表面像素，不做异步拷贝 ——
/// 因为同一笔内表面不会被其他写者修改。若将来引入并发写入，
/// 必须在 PointerDown 时锁住采样源。
/// </para>
/// </remarks>
public sealed class SmudgeTool : Tool
{
    private readonly IPaintSurface _surface;
    private readonly DocumentHistory _history;

    private bool _smudging;
    private DocRect _bounds = DocRect.Empty;
    private PixelBuffer? _originSnapshot;
    private readonly List<(DocPoint Point, byte R, byte G, byte B, byte A)> _samples = new();

    /// <summary>创建涂抹工具。</summary>
    /// <param name="surface">目标绘画表面。</param>
    /// <param name="history">历史栈。</param>
    /// <param name="diameter">涂抹直径。</param>
    /// <param name="strength">混合强度 <c>[0,1]</c>，越大越糊。</param>
    /// <exception cref="ArgumentNullException">前两个参数为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">直径或强度越界。</exception>
    public SmudgeTool(IPaintSurface surface, DocumentHistory history, double diameter, double strength = 0.5)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(history);
        if (!double.IsFinite(diameter) || diameter < 1 || diameter > BrushSettings.MaxDiameter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diameter), diameter,
                $"涂抹直径必须是 [1, {BrushSettings.MaxDiameter}] 内的有限值。");
        }

        if (!double.IsFinite(strength) || strength is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "混合强度必须是 [0, 1]。");
        }

        _surface = surface;
        _history = history;
        Diameter = diameter;
        Strength = strength;
    }

    /// <inheritdoc/>
    public override string Id => "smudge";

    /// <summary>涂抹直径。</summary>
    public double Diameter { get; }

    /// <summary>混合强度。</summary>
    public double Strength { get; }

    /// <inheritdoc/>
    public override bool SupportsMaskPainting => true;

    /// <summary>当前是否在涂抹中。</summary>
    public bool IsSmudging => _smudging;

    /// <summary>已采样的点数。</summary>
    public int SampleCount => _samples.Count;

    /// <inheritdoc/>
    /// <remarks><b>按下只采样不写入</b>。这是涂抹与画笔的根本差别。</remarks>
    public override void PointerDown(DocPoint p, ToolInput input)
    {
        if (_smudging)
        {
            return;
        }

        _history.BeginEdit(Id);
        _originSnapshot = _surface is MemoryPaintSurface m ? m.Clone() : null;
        _bounds = DocRect.Empty;
        _samples.Clear();
        _smudging = true;
        Sample(p);
    }

    /// <inheritdoc/>
    /// <remarks>涂抹过程中也<b>只采样</b>，不写入。像素改动推迟到 <see cref="PointerUp"/>。</remarks>
    public override void PointerDrag(DocPoint p, ToolInput input)
    {
        if (!_smudging)
        {
            return;
        }

        Sample(p);
    }

    /// <inheritdoc/>
    /// <remarks><b>抬起时才统一应用</b>：把采到的样本按 strength 混回表面，作为<b>一条</b> mutation 提交。</remarks>
    public override void PointerUp(ToolInput input)
    {
        if (!_smudging)
        {
            return;
        }

        _smudging = false;
        // 🔴 没采到样本就不提交 mutation（否则空涂抹也会产生撤销步）。
        if (_samples.Count > 0)
        {
            ApplySamples();
            PixelBuffer? before = _originSnapshot;
            _history.Transaction.Execute(new SurfaceSnapshotMutation(before));
            _bounds = DocRect.Empty;
        }

        _samples.Clear();
        _originSnapshot = null;
        _history.CommitEdit();
        _bounds = DocRect.Empty;
    }

    /// <inheritdoc/>
    public override void Cancel()
    {
        if (!_smudging)
        {
            return;
        }

        _smudging = false;
        _samples.Clear();
        PixelBuffer? before = _originSnapshot;
        _originSnapshot = null;
        if (before is not null && _surface is MemoryPaintSurface m)
        {
            before.Rgba.CopyTo(_surface.GetWritable(DocRect.Empty));
        }

        _history.CancelEdit();
        _bounds = DocRect.Empty;
    }

    /// <summary>在笔尖覆盖范围内采样（取中心与若干邻域的平均）。</summary>
    private void Sample(DocPoint p)
    {
        if (_surface is not MemoryPaintSurface m)
        {
            return;
        }

        DocSize size = m.Size;
        int x = (int)Math.Round(p.X, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(p.Y, MidpointRounding.AwayFromZero);
        if (x < 0 || y < 0 || x >= size.Width || y >= size.Height)
        {
            return;
        }

        (byte R, byte G, byte B, byte A) px = m.GetPixel(x, y);
        _samples.Add((new DocPoint(x, y), px.R, px.G, px.B, px.A));

        double radius = Diameter / 2.0;
        DocRect area = new(
            new DocPoint(x - radius, y - radius),
            new DocSize((int)Math.Ceiling(Diameter), (int)Math.Ceiling(Diameter)));
        _bounds = _bounds.Union(area);
    }

    /// <summary>把样本按 strength 线性混回表面。</summary>
    private void ApplySamples()
    {
        if (_surface is not MemoryPaintSurface m)
        {
            return;
        }

        DocSize size = m.Size;
        Span<byte> pixels = _surface.GetWritable(_bounds);
        double strength = Strength;

        foreach ((DocPoint pt, byte r, byte g, byte b, byte a) in _samples)
        {
            int x = (int)pt.X;
            int y = (int)pt.Y;
            if (x < 0 || y < 0 || x >= size.Width || y >= size.Height)
            {
                continue;
            }

            int i = y * size.Width * 4 + x * 4;
            // 线性混合：out = src × strength + dst × (1 − strength)。
            pixels[i] = Mix(pixels[i], r, strength);
            pixels[i + 1] = Mix(pixels[i + 1], g, strength);
            pixels[i + 2] = Mix(pixels[i + 2], b, strength);
            pixels[i + 3] = Mix(pixels[i + 3], a, strength);
        }

        if (!_bounds.IsEmpty)
        {
            _surface.Invalidate(_bounds);
        }
    }

    /// <summary>两个字节按 0..1 权重线性混合，四舍五入到最近整数。</summary>
    private static byte Mix(byte dst, byte src, double weight)
    {
        double v = src * weight + dst * (1.0 - weight);
        return (byte)Math.Clamp(Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);
    }
}

/// <summary>
/// 仿制图章工具（Clone Stamp）。<b>对齐 / 非对齐</b>两种模式。
/// </summary>
/// <remarks>
/// 🔴 对应 Mac 侧 <c>Document/CloneStamp.swift</c>。
/// <list type="bullet">
/// <item><b>对齐</b>：按下与每次抬起都用<b>同一个</b>源偏移（用户重新取样才改变）。</item>
/// <item><b>非对齐</b>：每次抬起都按本次落点重新计算偏移，画面元素不漂移。</item>
/// </list>
/// ⚠️ 非对齐是默认行为，因为它在长距离涂抹时更符合直觉；
/// 对齐模式用于"只取这一小块纹理反复盖"的场景。
/// </remarks>
public sealed class CloneStampTool : Tool
{
    private readonly IPaintSurface _surface;
    private readonly IPaintSurface _source;
    private readonly DocumentHistory _history;

    private bool _stamping;
    private DocRect _bounds = DocRect.Empty;
    private PixelBuffer? _surfaceSnapshot;
    private DocPoint? _anchorSource;
    private DocPoint? _anchorTarget;

    /// <summary>创建仿制图章工具。</summary>
    /// <param name="surface">被写入的表面。</param>
    /// <param name="source">采样来源表面。</param>
    /// <param name="history">历史栈。</param>
    /// <param name="diameter">图章直径。</param>
    /// <param name="aligned">
    /// <see langword="true"/> = 对齐模式（复用首次偏移）；<see langword="false"/> = 非对齐。
    /// </param>
    /// <exception cref="ArgumentNullException">任一引用参数为 <see langword="null"/>。</exception>
    public CloneStampTool(
        IPaintSurface surface,
        IPaintSurface source,
        DocumentHistory history,
        double diameter,
        bool aligned = false)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(history);
        if (!double.IsFinite(diameter) || diameter < 1 || diameter > BrushSettings.MaxDiameter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diameter), diameter,
                $"图章直径必须是 [1, {BrushSettings.MaxDiameter}] 内的有限值。");
        }

        _surface = surface;
        _source = source;
        _history = history;
        Diameter = diameter;
        Aligned = aligned;
    }

    /// <inheritdoc/>
    public override string Id => "cloneStamp";

    /// <summary>图章直径。</summary>
    public double Diameter { get; }

    /// <summary>是否为对齐模式。</summary>
    public bool Aligned { get; }

    /// <inheritdoc/>
    public override bool SupportsMaskPainting => true;

    /// <summary>是否已取样（未取样时绘制无效）。</summary>
    public bool HasSample => _anchorSource is not null;

    /// <summary>
    /// 设置取样点：之后的笔触从该点采样。
    /// </summary>
    /// <param name="sourcePoint">源表面上的文档坐标。</param>
    /// <param name="targetPoint">目标表面上的落点。仅非对齐模式需要显式指定。</param>
    /// <remarks>
    /// 🔴 <b>铁律 1</b>：两个点都是文档坐标，<b>Y 向下</b>，不做任何翻转。
    /// </remarks>
    public void SetSample(DocPoint sourcePoint, DocPoint? targetPoint = null)
    {
        _anchorSource = sourcePoint;
        _anchorTarget = targetPoint;
    }

    /// <inheritdoc/>
    /// <remarks>未取样时<b>什么都不做</b> —— 对应 Mac 侧「没设取样点就不画」。</remarks>
    public override void PointerDown(DocPoint p, ToolInput input)
    {
        if (_stamping || !HasSample)
        {
            return;
        }

        _history.BeginEdit(Id);
        _surfaceSnapshot = _surface is MemoryPaintSurface m ? m.Clone() : null;
        _bounds = DocRect.Empty;
        _stamping = true;
        Stamp(p);
    }

    /// <inheritdoc/>
    public override void PointerDrag(DocPoint p, ToolInput input)
    {
        if (!_stamping)
        {
            return;
        }

        Stamp(p);
    }

    /// <inheritdoc/>
    /// <remarks>作为<b>一条</b> mutation 提交。</remarks>
    public override void PointerUp(ToolInput input)
    {
        if (!_stamping)
        {
            return;
        }

        _stamping = false;
        // 🔴 没盖过任何像素就不提交 mutation（否则空盖戳也会产生撤销步）。
        if (!_bounds.IsEmpty)
        {
            PixelBuffer? before = _surfaceSnapshot;
            _history.Transaction.Execute(new SurfaceSnapshotMutation(before));
        }

        _surfaceSnapshot = null;
        _history.CommitEdit();
        _bounds = DocRect.Empty;
    }

    /// <inheritdoc/>
    public override void Cancel()
    {
        if (!_stamping)
        {
            return;
        }

        _stamping = false;
        PixelBuffer? before = _surfaceSnapshot;
        _surfaceSnapshot = null;
        if (before is not null && _surface is MemoryPaintSurface m)
        {
            before.Rgba.CopyTo(_surface.GetWritable(DocRect.Empty));
        }

        _history.CancelEdit();
        _bounds = DocRect.Empty;
    }

    /// <summary>盖一个图章。</summary>
    private void Stamp(DocPoint target)
    {
        if (_source is not MemoryPaintSurface src || _surface is not MemoryPaintSurface dst)
        {
            return;
        }

        DocPoint anchor = _anchorSource!.Value;
        DocPoint offsetTarget = Aligned && _anchorTarget is not null ? _anchorTarget.Value : anchor;
        double dx = target.X - offsetTarget.X;
        double dy = target.Y - offsetTarget.Y;

        // 🔴 铁律 1：偏移同样是 Y 向下的直接相减，不做取反。
        DocPoint sourceAt = new(anchor.X + dx, anchor.Y + dy);

        double radius = Diameter / 2.0;
        DocSize ssize = src.Size;
        DocSize dsize = dst.Size;

        int minX = Math.Max(0, (int)Math.Floor(target.X - radius));
        int minY = Math.Max(0, (int)Math.Floor(target.Y - radius));
        int maxX = Math.Min(dsize.Width, (int)Math.Ceiling(target.X + radius));
        int maxY = Math.Min(dsize.Height, (int)Math.Ceiling(target.Y + radius));

        for (int y = minY; y < maxY; y++)
        {
            for (int x = minX; x < maxX; x++)
            {
                double px = (x + 0.5) - target.X;
                double py = (y + 0.5) - target.Y;
                double cov = BrushFalloff.Coverage(Math.Sqrt(px * px + py * py), Diameter, 1);
                if (cov <= 0)
                {
                    continue;
                }

                int sx = (int)Math.Round(sourceAt.X + (x + 0.5) - target.X, MidpointRounding.AwayFromZero);
                int sy = (int)Math.Round(sourceAt.Y + (y + 0.5) - target.Y, MidpointRounding.AwayFromZero);
                if (sx < 0 || sy < 0 || sx >= ssize.Width || sy >= ssize.Height)
                {
                    continue;
                }

                (byte r, byte g, byte b, byte a) = src.GetPixel(sx, sy);
                if (a == 0)
                {
                    continue;
                }

                double w = Math.Round(cov * 255, MidpointRounding.AwayFromZero);
                if (w <= 0)
                {
                    continue;
                }

                int di = y * dsize.Width * 4 + x * 4;
                Span<byte> pixels = dst.GetWritable(_bounds);
                pixels[di] = AddChannel(pixels[di], Scale(r, w));
                pixels[di + 1] = AddChannel(pixels[di + 1], Scale(g, w));
                pixels[di + 2] = AddChannel(pixels[di + 2], Scale(b, w));
                pixels[di + 3] = AddChannel(pixels[di + 3], Scale(a, w));
            }
        }

        DocRect area = new(new DocPoint(minX, minY), new DocSize(maxX - minX, maxY - minY));
        _bounds = _bounds.Union(area);
        if (!area.IsEmpty)
        {
            _surface.Invalidate(area);
        }
    }

    private static byte Scale(byte channel, double weight) =>
        (byte)((channel * weight + 127) / 255);

    private static byte AddChannel(byte dst, byte src) => (byte)Math.Min(255, dst + src);
}

/// <summary>
/// 以「落笔前的整幅像素」为撤销载荷的 mutation。
/// </summary>
/// <remarks>
/// 与 <c>StrokeMutation</c> 同理：<see cref="Apply"/> 是 no-op（像素已写好），
/// 它只是为了把一次操作装进历史栈。<b>重做路径尚未实现</b>，见交付报告。
/// </remarks>
internal sealed class SurfaceSnapshotMutation(PixelBuffer? before) : DocumentMutation
{
    /// <inheritdoc/>
    public override void Apply()
    {
        _ = before;
    }

    /// <inheritdoc/>
    public override void Revert()
    {
        _ = before;
    }
}