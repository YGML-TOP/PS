using System.Text.Json.Nodes;

namespace Compositor.Core;

/// <summary>可渲染的画布。抽象出来是为了让工具层不依赖具体的合成器实现。</summary>
public interface ICanvas
{
    /// <summary>画布尺寸（文档像素）。</summary>
    DocSize Size { get; }

    /// <summary>画布内容发生变化时触发，参数是受影响的文档矩形。</summary>
    event Action<DocRect>? Invalidated;

    /// <summary>声明某块区域已失效，需要重绘。</summary>
    /// <param name="area">受影响的文档矩形。</param>
    void Invalidate(DocRect area);

    /// <summary>读取画布像素。null 表示该区域无内容。</summary>
    /// <param name="area">待读取区域，超出画布的部分由实现裁剪。</param>
    /// <returns>预乘 RGBA8 像素，或 <see langword="null"/>。</returns>
    PixelBuffer? Snapshot(DocRect area);
}

/// <summary>什么都不做的画布：记录调用不崩溃。铁律：非空实现必须由 core 提供。</summary>
/// <param name="size">对外报告的画布尺寸。</param>
/// <remarks>
/// 🔴 这是<b>并行度的前提</b>，不是占位符。AI-3~AI-6 要在真实合成器完工前
/// 用它跑自己的单元测试；它必须行为可预测、可断言。
/// <para><see cref="Snapshot"/> 恒返回 <see langword="null"/>（"该区域无内容"），
/// <see cref="Invalidate"/> 只触发事件、不做任何事。</para>
/// </remarks>
public sealed class NullCanvas(DocSize size) : ICanvas
{
    /// <summary>对外报告的画布尺寸，即构造时传入的值。</summary>
    public DocSize Size { get; } = size;

    /// <inheritdoc cref="ICanvas.Invalidated"/>
    public event Action<DocRect>? Invalidated;

    /// <inheritdoc cref="ICanvas.Invalidate"/>
    /// <remarks>只把事件发出去，不做任何实际的重绘工作。</remarks>
    public void Invalidate(DocRect area) => Invalidated?.Invoke(area);

    /// <inheritdoc cref="ICanvas.Snapshot"/>
    /// <returns>恒为 <see langword="null"/>。</returns>
    public PixelBuffer? Snapshot(DocRect area) => null;
}

/// <summary>
/// 图层树节点。🔴 v1.1 修正：v1.0 契约里 <c>IDocument.Layers</c> 引用了 LayerNode 却<b>从未定义</b>，
/// 且 LayerTransform 只承载几何+采样，导致任务书 §4.3 要求的"混合模式 / 文件夹不透明度相乘 /
/// 剪贴蒙版 / 活蒙版链"<b>一项都没有承载类型</b>——AI-1 开工即卡死。此处补齐。
/// 每个字段与 <c>.comp</c> manifest 的图层记录一一对应（见 <c>docs/project-format.md</c>）。
/// </summary>
public sealed record LayerNode
{
    /// <summary>图层 UUID。<c>.comp</c> 里的图层标识。</summary>
    public Guid Id { get; init; }

    /// <summary>图层名（UI 上显示）。</summary>
    public string Name { get; init; } = "";

    /// <summary>是否可见。false 时不参与合成。</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>几何变换：位置、尺寸、旋转、翻转、采样。<b>必填，没有默认值。</b></summary>
    /// <remarks>
    /// <para>🔴 契约缺陷 #10（待 v1.2 追认）。v1.1 把本属性写成普通可初始化属性：
    /// 既没给默认值，也没标 <c>required</c>，且 <see cref="LayerTransform"/> 是引用类型。
    /// 于是<b>漏设 <see cref="Transform"/> 的图层在合成阶段才会 NRE</b>，
    /// 崩溃点离出错点很远，定位成本高。</para>
    /// <para>加 <c>required</c> 把这个坑从运行期挪到编译期。
    /// <b>它不改变属性类型、不改变读写语义、不改变 <c>.comp</c> 序列化</b>，
    /// 只让「忘记赋变换」变成一条编译错误。所有正常构造图层的调用方（AI-2 解析 manifest）都会赋值，
    /// 因此对它们<b>零改动</b>。</para>
    /// <para>为什么不选 <c>= new()</c>：那会让漏设的图层静默拿到
    /// <c>Origin=(0,0) / Size=(0,0)</c> 的退化变换——不崩，但画出来是零尺寸图层，
    /// 属于更难查的一类 bug。<c>required</c> 没有这个中间态。</para>
    /// </remarks>
    public required LayerTransform Transform { get; init; }

    // v3 / v8

    /// <summary>不透明度，<b>有限且落在 0..1</b>。与 <see cref="Transform"/> 的几何无关。</summary>
    public float Opacity { get; init; } = 1.0f;

    /// <summary>混合模式。序列化必须经 <see cref="BlendModeStrings"/>。</summary>
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;

    // v2 分组；分组不透传：其 Opacity 会乘进每个后代，BlendMode 恒为 Normal

    /// <summary>是否是分组（文件夹）。</summary>
    public bool IsGroup { get; init; }

    /// <summary>父分组 id。<see langword="null"/> = 根层。</summary>
    public Guid? ParentId { get; init; }

    // 普通像素层有；分组与调整层为 null

    /// <summary>该图层像素在 <c>.comp</c> 包内的相对路径。<see langword="null"/> = 无栅格内容。</summary>
    public string? ImageFile { get; init; }

    // v4/v5/v6 蒙版

    /// <summary>栅格蒙版文件名，约定为 <c>"&lt;图层UUID&gt;.mask.png"</c>。</summary>
    public string? MaskFile { get; init; }

    /// <summary>蒙版是否参与合成。</summary>
    public bool MaskEnabled { get; init; } = true;

    /// <summary>蒙版是否与图层链接。<b>⚠ 语义待 AI-2 以 ai2-00 手册确认</b>，本字段暂不解释其取值含义。</summary>
    public bool? MaskLinked { get; init; }

    /// <summary>蒙版来源图层 id。v5 活蒙版链 = 剪贴蒙版。链长 &gt; 256 需拒绝。</summary>
    public Guid? MaskSourceId { get; init; }

    /// <summary>蒙版的独立变换。为 <see langword="null"/> 时跟随 <see cref="Transform"/>。</summary>
    public LayerTransform? MaskPlacement { get; init; }

    // v7/v9 调整层：kind 字符串必须与 manifest 一致（AI-2 依赖它写出）

    /// <summary>调整层参数。非 <see langword="null"/> 表示这是调整层而非像素层。</summary>
    public AdjustmentSpec? Adjustment { get; init; }

    /// <summary>是否为调整层。等价于 <c>Adjustment is not null</c>。</summary>
    public bool IsAdjustment => Adjustment is not null;
}

/// <summary>调整层参数。🔴 v1.0 契约完全没有这个类型，但 manifest v7/v9 定义了它。</summary>
public sealed record AdjustmentSpec
{
    /// <summary>
    /// 调整层种类，例如 <c>"Levels"</c> / <c>"Curves"</c> / <c>"Hue/Saturation"</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>此字符串必须与 <c>.comp</c> manifest 逐字一致</b>，AI-2 依赖它写出、AI-5 依赖它读入。
    /// 本类型<b>刻意不提供枚举</b>：12 种调整层由 AI-5 在波次 3 定义，
    /// 契约层先行固化未知字符串也不做校验。
    /// </remarks>
    public string Kind { get; init; } = "";

    /// <summary>调整参数本体。形态由 <see cref="Kind"/> 决定，本类型不解释。</summary>
    public JsonObject Settings { get; init; } = new();
}

/// <summary>可编辑的文档。</summary>
public interface IDocument
{
    /// <summary>图层列表，<b>自下而上</b>（index 0 是最底层）。</summary>
    IReadOnlyList<LayerNode> Layers { get; }

    /// <summary>该文档的画布。</summary>
    ICanvas Canvas { get; }

    /// <summary>文档尺寸。</summary>
    DocSize Size { get; }

    /// <summary>文档状态发生变化时触发，参数是受影响的文档矩形。</summary>
    event Action<DocRect>? Changed;

    /// <summary>一切写操作的唯一入口。保证所有写操作可撤销（AI-3 依赖此约定）。</summary>
    /// <param name="tx">要执行的事务。</param>
    void Mutate(Action<DocumentMutation> tx);

    /// <summary>当前选区。无选区时为 <see langword="null"/>。</summary>
    SelectionMask? Selection { get; }
}

/// <summary><c>.comp</c> 工程读写。🔴 v1.0 契约在 §4.2 点名要写 Null 实现，却<b>连接口都没声明</b>。
/// AI-2 实现此接口的正式版，Null 版由 AI-1 提供。</summary>
public interface IProjectStore
{
    /// <summary>读取工程。</summary>
    /// <param name="packagePath"><c>.comp</c> 包路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>工程快照。</returns>
    Task<ProjectSnapshot> LoadAsync(string packagePath, CancellationToken ct = default);

    /// <summary>写入工程。</summary>
    /// <param name="snapshot">待保存的快照。</param>
    /// <param name="packagePath"><c>.comp</c> 包路径。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    Task SaveAsync(ProjectSnapshot snapshot, string packagePath, CancellationToken ct = default);

    /// <summary>外部改动（AI/脚本写文件）时触发。AI-6 的 <c>.comp</c> 热重载依赖它。</summary>
    event Action<string>? ExternalChangeDetected;
}

/// <summary>工程的内存快照。与 <c>.comp</c> manifest 一一对应。</summary>
public sealed record ProjectSnapshot
{
    /// <summary>manifest 版本号。当前格式为 11。</summary>
    public int Version { get; init; } = 11;

    /// <summary>文档尺寸。</summary>
    public DocSize Size { get; init; }

    /// <summary>
    /// 色彩空间标记。
    /// </summary>
    /// <remarks>
    /// 🔴 本字段是 manifest 的<b>字符串</b>，与 <see cref="ColorSpace"/> 枚举<b>不是同一回事</b>，
    /// 也不参与枚举序列化。判定结论见 <c>docs/color-space.md</c>：像素管线为编码 sRGB。
    /// </remarks>
    public string ColorSpace { get; init; } = "sRGB";

    /// <summary>图层列表，自下而上。</summary>
    public IReadOnlyList<LayerNode> Layers { get; init; } = Array.Empty<LayerNode>();

    /// <summary>文档 id。</summary>
    public Guid DocumentId { get; init; }

    /// <summary>当前活动图层 id。</summary>
    public Guid? ActiveLayerId { get; init; }
}

/// <summary>什么都不做的工程存储：记录调用不崩溃。波次 3 的五个功能 AI 靠它并行开工。</summary>
/// <remarks>
/// 🔴 这是<b>并行度的前提</b>，不是占位符：
/// <list type="bullet">
/// <item><see cref="LoadAsync"/> 恒返回一个<b>已完成</b>的默认快照（不阻塞、不抛）。</item>
/// <item><see cref="SaveAsync"/> 恒立即完成（<b>不写盘</b>）。</item>
/// <item><see cref="ExternalChangeDetected"/> <b>永不触发</b>——没有文件监视器可言。</item>
/// </list>
/// 下游若在测试里保存后指望文件存在，那是对 Null 实现的误解，应改用真实实现。
/// </remarks>
public sealed class NullProjectStore : IProjectStore
{
    /// <inheritdoc cref="IProjectStore.ExternalChangeDetected"/>
    /// <remarks>Null 实现没有文件监视器，此事件<b>永不触发</b>。</remarks>
#pragma warning disable CS0067 // 事件已实现但从不触发——这是 Null 实现的定义行为，不是遗漏。
    public event Action<string>? ExternalChangeDetected;
#pragma warning restore CS0067

    /// <inheritdoc cref="IProjectStore.LoadAsync"/>
    /// <returns>恒为 <see cref="ProjectSnapshot"/> 的默认值（版本 11、尺寸 0×0、无图层）。</returns>
    public Task<ProjectSnapshot> LoadAsync(string p, CancellationToken ct = default)
        => Task.FromResult(new ProjectSnapshot());

    /// <inheritdoc cref="IProjectStore.SaveAsync"/>
    /// <remarks>立即完成，<b>不产生任何磁盘写入</b>。</remarks>
    public Task SaveAsync(ProjectSnapshot s, string p, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// 一次可撤销的文档变更。
/// </summary>
/// <remarks>
/// 🔴 任务书 §3.1 要求"所有写操作可撤销"，AI-3 的撤销栈依赖本类型。
/// <see cref="Apply"/> 必须能安全重放，<see cref="Revert"/> 必须精确逆转它 ——
/// 两者都不保证幂等，实现方不得依赖重复调用安全。
/// </remarks>
public abstract class DocumentMutation
{
    /// <summary>执行变更。</summary>
    public abstract void Apply();

    /// <summary>逆转变更，撤销到执行 <see cref="Apply"/> 之前的状态。</summary>
    public abstract void Revert();
}
