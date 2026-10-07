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
    /// <remarks>
    /// 🔴 <b>本成员的 <c>tx</c> 语义在 v1.1 是不完整的，已登记待澄清。</b>
    /// <c>DocumentMutation</c> 是纯抽象类（只有 <see cref="DocumentMutation.Apply"/> /
    /// <see cref="DocumentMutation.Revert"/>），没有工厂也没有记录器，
    /// 所以 <c>tx</c> 回调拿到的实例<b>无法构造、无法填充、也无法交回去</b>
    /// （<c>Action&lt;T&gt;</c> 没有返回值通道）。
    /// 报告给总管后，v1.2 以 <see cref="MutateAll"/> 补上了一条语义明确的入口。
    /// </remarks>
    void Mutate(Action<DocumentMutation> tx);

    /// <summary>
    /// 🔴 <b>契约 v1.2 新增：一次性提交多个 mutation，合并为一个 undo 步骤。</b>
    /// </summary>
    /// <param name="mutations">要依次施加的变更，顺序即施加顺序。</param>
    /// <remarks>
    /// <para><b>为什么不在 <see cref="DocumentMutation"/> 上加 Label / 分组。</b>
    /// AI-3 最初想加的是「Label」与「把多个变更包成一个」。
    /// 那被明确驳回了，理由是<b>两层概念不能混</b>：
    /// <list type="bullet">
    /// <item><see cref="DocumentMutation"/> 的语义是「<b>一个</b>可逆变更」，
    /// <see cref="DocumentMutation.Apply"/> 精确改变一件事、
    /// <see cref="DocumentMutation.Revert"/> 精确逆转它。</item>
    /// <item>「<b>一次用户操作</b>」是若干个变更的<b>组合</b>，命名（Label）与撤销栈的组织
    /// 属于<b>上层 history entry</b>，对应 Mac 的 <c>DocumentHistory.swift</c> 快照式栈。</item>
    /// </list>
    /// 把 Label 塞进 mutation 会让「一个变更」与「一次编辑」在类型上无法区分，
    /// 反过来污染 AI-3 自己要写的历史模块。分组能力因此只落在<b>本方法</b>这一个入口上。</para>
    ///
    /// <para><b>事务性契约（实现方必须遵守）</b>：
    /// 任一 <see cref="DocumentMutation.Apply"/> 抛异常时，
    /// <b>已成功施加的那些必须按逆序 <see cref="DocumentMutation.Revert"/></b>，
    /// 然后把原异常继续抛给调用方。这一条是正确性要求而非风格问题 ——
    /// 后施加的变更依赖先施加的结果，不逆序撤销会留下不一致状态，
    /// 而这种不一致通常不会立刻显形，只会在之后某个无关操作时炸开。</para>
    ///
    /// <para><b>空数组是合法的 no-op</b>，不得抛异常。</para>
    /// </remarks>
    void MutateAll(params DocumentMutation[] mutations);

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

    /// <summary>🔴 <b>契约 v1.2 新增</b>：图层像素，键为 <see cref="LayerNode.Id"/>。</summary>
    /// <remarks>
    /// 默认空字典而非 <see langword="null"/>：调用方可以直接遍历而不必判空。
    /// 这里的值只可能是 <see cref="ProjectPixelFormat.PremultipliedRgba8"/>
    /// （图层像素带 alpha，必然是 RGBA）。
    /// </remarks>
    public IReadOnlyDictionary<Guid, IProjectAsset> Images { get; init; }
        = new Dictionary<Guid, IProjectAsset>();

    /// <summary>🔴 <b>契约 v1.2 新增</b>：蒙版像素，键为 <see cref="LayerNode.Id"/>。</summary>
    /// <remarks>
    /// 默认空字典。<b>这里只接受 <see cref="ProjectPixelFormat.Gray8"/></b>——
    /// 依据 Mac 的 <c>Document/LayerMask.swift:23-26</c>：
    /// 蒙版必须是 monochrome + 8bpc + <c>alphaInfo == .none</c>（无 alpha 通道）。
    /// 这条约束正是驳回「只带 PNG 字节」那种形状的理由：
    /// PNG 字节是黑盒，Core 层无从校验，<c>save</c> 时必然出现
    /// 「写进去了但下次 <c>load</c> 拒收」的不对称。
    /// </remarks>
    public IReadOnlyDictionary<Guid, IProjectAsset> Masks { get; init; }
        = new Dictionary<Guid, IProjectAsset>();
}

/// <summary><c>.comp</c> 包内一项资产的像素格式。🔴 <b>契约 v1.2 新增</b>。</summary>
/// <remarks>
/// <b>枚举值序号参与 <c>.comp</c> 序列化</b>（0 与 1），改动会让已存的工程读不出来。
/// 新增格式只能追加到末尾，不能插入。
/// </remarks>
public enum ProjectPixelFormat
{
    /// <summary>预乘 RGBA，每像素 4 字节。铁律 2：alpha 全程预乘。</summary>
    PremultipliedRgba8 = 0,

    /// <summary>
    /// 8 位灰度，每像素 1 字节，<b>无 alpha 通道</b>。仅蒙版可用。
    /// </summary>
    /// <remarks>
    /// 依据 Mac 的 <c>Document/LayerMask.swift:23-26</c>（monochrome + 8bpc + <c>alphaInfo == .none</c>）。
    /// 🔴 与铁律 2 的关系：铁律 2 说的是<b>蒙版不含 alpha</b>，
    /// 不是「蒙版可以是 RGBA」—— 单通道灰度正是铁律 2 的直接产物。
    /// </remarks>
    Gray8 = 1,
}

/// <summary><c>.comp</c> 包内一项资产（图层像素或蒙版）。纯字节，<b>Core 不引入任何图像库</b>。</summary>
/// <remarks>
/// 🔴 <b>契约 v1.2 新增。</b>驳回 AI-2 建议的 <c>byte[] PngBytes</c> 形状，理由有二：
/// <list type="number">
/// <item><b>load 边界上无意义</b>。Mac 的 <c>load</c>（<c>ProjectStore.swift:168-191</c>）返回的是
/// <b>解码后</b>的图像；契约层若只带 PNG 字节，调用方拿到结果还要再解一次码，
/// 等于把「读文件」和「解码」绕一圈。</item>
/// <item><b>蒙版格式约束丢失</b>。见 <see cref="ProjectPixelFormat.Gray8"/> 的说明。</item>
/// </list>
/// <para><b>本接口只有形状，没有提供实现</b>——契约层只定义「一项资产长什么样」，
/// 谁来构造（解码路径、测试替身）由各层自行决定。</para>
/// </remarks>
public interface IProjectAsset
{
    /// <summary>资产宽度，单位像素。</summary>
    int Width { get; }

    /// <summary>资产高度，单位像素。</summary>
    int Height { get; }

    /// <summary>逐像素格式，决定 <see cref="Pixels"/> 的字节步长与含义。</summary>
    ProjectPixelFormat Format { get; }

    /// <summary>
    /// 逐像素数据。长度 = <c>Width * Height * (Format == Gray8 ? 1 : 4)</c>，行优先，无对齐填充。
    /// </summary>
    ReadOnlyMemory<byte> Pixels { get; }
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

/// <summary>什么都不做的文档：记录调用不崩溃。波次 3 的功能 AI 靠它并行开工。</summary>
/// <remarks>
/// 🔴 这是<b>并行度的前提</b>，不是占位符，与 <see cref="NullCanvas"/> / <see cref="NullProjectStore"/> 同理：
/// <list type="bullet">
/// <item><see cref="Layers"/> 恒为空列表。</item>
/// <item><see cref="Selection"/> 恒为 <see langword="null"/>（"没有选区"）。</item>
/// <item><see cref="Changed"/> <b>永不触发</b>——没有状态可改。</item>
/// <item><see cref="Mutate"/> 与 <see cref="MutateAll"/> 都是<b>空实现</b>，
/// <b>不施加任何 mutation</b>。这一点必须写清楚：Null 文档的「不崩溃」不等于「会执行」，
/// 下游若在测试里提交 mutation 后指望文档状态改变，那是对 Null 实现的误解。</item>
/// </list>
/// </remarks>
public sealed class NullDocument : IDocument
{
    /// <summary>用一个给定尺寸创建 Null 文档。</summary>
    /// <param name="size">文档尺寸。</param>
    public NullDocument(DocSize size)
        : this(size, new NullCanvas(size))
    {
    }

    /// <summary>用一个给定尺寸与给定画布创建 Null 文档。</summary>
    /// <param name="size">文档尺寸。</param>
    /// <param name="canvas">对外暴露的画布。</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvas"/> 为 <see langword="null"/>。</exception>
    public NullDocument(DocSize size, ICanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Size = size;
        Canvas = canvas;
    }

    /// <inheritdoc cref="IDocument.Size"/>
    public DocSize Size { get; }

    /// <inheritdoc cref="IDocument.Canvas"/>
    public ICanvas Canvas { get; }

    /// <inheritdoc cref="IDocument.Layers"/>
    /// <returns>恒为空列表。</returns>
    public IReadOnlyList<LayerNode> Layers { get; } = Array.Empty<LayerNode>();

    /// <inheritdoc cref="IDocument.Selection"/>
    /// <returns>恒为 <see langword="null"/>。</returns>
    public SelectionMask? Selection => null;

    /// <inheritdoc cref="IDocument.Changed"/>
    /// <remarks>Null 实现没有状态可改，此事件<b>永不触发</b>。</remarks>
#pragma warning disable CS0067 // 事件已实现但从不触发——这是 Null 实现的定义行为，不是遗漏。
    public event Action<DocRect>? Changed;
#pragma warning restore CS0067

    /// <inheritdoc cref="IDocument.Mutate"/>
    /// <remarks>空实现：<b>什么都不做</b>，也不调用 <paramref name="tx"/>。</remarks>
    public void Mutate(Action<DocumentMutation> tx)
    {
        // 刻意不调用 tx：Null 文档没有可施加变更的对象，
        // 让回调跑一遍反而会制造「操作生效了」的错觉。
    }

    /// <inheritdoc cref="IDocument.MutateAll"/>
    /// <remarks>空实现：<b>不施加任何 mutation</b>，空数组亦然（不抛）。</remarks>
    public void MutateAll(params DocumentMutation[] mutations)
    {
        // 与 Mutate 同理：连 Apply 都不调用。
    }
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
