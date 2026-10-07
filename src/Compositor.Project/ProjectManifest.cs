using System.Text.Json.Serialization;
using Compositor.Core;

namespace Compositor.Project;

/// <summary>
/// <c>.comp</c> 包内的 manifest。字段与 JSON 键名逐字对应 macOS 版
/// <c>ProjectStore.swift:13-31</c> 的 <c>ProjectManifest</c>。
/// </summary>
/// <remarks>
/// ⚠️ <b>JSON 键名不能按 C# 命名习惯推。</b>Mac 的 <c>JSONEncoder</c> 只开了
/// <c>[.prettyPrinted, .sortedKeys]</c>，<b>没有</b> <c>.convertToSnakeCase</c>，
/// 所以写出去的就是 Swift 属性名本身：<c>documentID</c> 不是 <c>document_id</c>，
/// <c>activeLayerID</c> 不是 <c>activeLayerId</c>。每个字段都挂了
/// <see cref="JsonPropertyNameAttribute"/>，改任何一个都会让 Mac 版读不到该字段。
/// </remarks>
public sealed record ProjectManifest
{
    /// <summary>新保存时写入的格式版本。对应 <c>ProjectStore.swift:15</c>。</summary>
    public const int CurrentVersion = 11;

    /// <summary>读取时接受的最低版本。对应 <c>ProjectStore.swift:18</c> 的 <c>supported</c> 下界。</summary>
    public const int MinSupportedVersion = 1;

    /// <summary>包类型标识。对应 <c>ProjectStore.swift:7,20</c>。</summary>
    public const string FormatId = "com.compositor.project";

    /// <summary>包类型标识。</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = FormatId;

    /// <summary>格式版本，1–11。</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>色彩空间标识。目前只有 <c>"sRGB"</c> 合法（<c>ProjectStore.swift:201</c>）。</summary>
    [JsonPropertyName("colorSpace")]
    public string ColorSpace { get; init; } = "sRGB";

    /// <summary>每英寸像素数。v1 工程缺省为 72（<c>ProjectStore.swift:23</c>）。</summary>
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Resolution { get; init; }

    /// <summary>文档 UUID。</summary>
    [JsonPropertyName("documentID")]
    public Guid DocumentId { get; init; }

    /// <summary>画布像素宽。</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>画布像素高。</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>当前活动图层。</summary>
    [JsonPropertyName("activeLayerID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ActiveLayerId { get; init; }

    /// <summary>
    /// 图层记录。<b>索引 0 = 最底层</b>（Mac 的 <c>manifest.layers</c> 顺序即绘制顺序，自下而上）。
    /// </summary>
    [JsonPropertyName("layers")]
    public IReadOnlyList<ProjectLayerRecord> Layers { get; init; } = Array.Empty<ProjectLayerRecord>();

    /// <summary>对齐参考线。版本 1–7 没有这个字段（<c>ProjectStore.swift:29</c>）。</summary>
    [JsonPropertyName("guides")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CanvasGuide>? Guides { get; init; }

    /// <summary>画布尺寸。<b>计算属性，不进 JSON</b>。</summary>
    [JsonIgnore]
    public DocSize Size => new(Width, Height);

    /// <summary>
    /// 把图层 UUID 转成 <c>.comp</c> 里使用的字符串形式。
    /// </summary>
    /// <returns><b>大写</b>的 RFC 4122 短横线格式，例如 <c>6F1D3C2A-0B7E-…</c>。</returns>
    /// <remarks>
    /// <b>🔴 必须大写。</b>Mac 在 <c>ProjectStore.swift:223,243</c> 用
    /// <c>"\(layer.id.uuidString).png"</c> 校验文件名，而 Swift 的 <c>UUID.uuidString</c>
    /// 按 RFC 4122 输出<b>大写</b>。.NET 的 <c>Guid.ToString()</c> 输出<b>小写</b>。
    /// 直接用 .NET 的小写形式拼文件名，会写出 Mac <b>全部拒收</b>的工程
    /// ——而且症状是"打不开"，不是任何本地报错。
    /// 写 <c>imageFile</c> / <c>maskFile</c> 时一律走本方法，不要内插 <c>Guid.ToString()</c>。
    /// </remarks>
    public static string ProjectIdString(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>图层像素文件名：<c>{ID大写}.png</c>。</summary>
    public static string ImageFileName(Guid id) => ProjectIdString(id) + ".png";

    /// <summary>蒙版文件名：<c>{ID大写}.mask.png</c>。</summary>
    public static string MaskFileName(Guid id) => ProjectIdString(id) + ".mask.png";
}

/// <summary>
/// 一个图层在 manifest 里的记录。字段与 JSON 键名逐字对应
/// <c>ProjectStore.swift:33-56</c> 的 <c>ProjectLayerRecord</c>。
/// </summary>
/// <remarks>
/// 这一串键名是 Mac 侧最容易写错的地方：<c>parentID</c>、<c>isGroup</c>、
/// <c>maskSourceID</c>、<c>maskFile</c>、<c>maskEnabled</c> 全部大写 ID。
/// </remarks>
public sealed record ProjectLayerRecord
{
    /// <summary>图层 UUID。</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>图层名。非空且 UTF-8 字节数 ≤ 16384（<c>ProjectStore.swift:242</c>）。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>是否可见。</summary>
    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; init; }

    /// <summary>图层变换。</summary>
    [JsonPropertyName("transform")]
    public LayerTransform Transform { get; init; } = new();

    /// <summary>
    /// 图层像素文件名。校验规则：非 null 时必须恰为 <c>"{id}.png"</c>
    /// （<c>ProjectStore.swift:243</c>）。分组与调整层为 null。
    /// </summary>
    [JsonPropertyName("imageFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageFile { get; init; }

    /// <summary>父分组。null = 根。</summary>
    [JsonPropertyName("parentID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentId { get; init; }

    /// <summary>是否为分组。分组不透传混合模式（<c>ProjectStore.swift:232</c>）。</summary>
    [JsonPropertyName("isGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsGroup { get; init; }

    /// <summary>不透明度，0–1。分组从 v8 起有自己的不透明度。</summary>
    [JsonPropertyName("opacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Opacity { get; init; }

    /// <summary>混合模式。<b>序列化走 <c>BlendModeStrings</c> 的字符串字面量</b>，不是枚举名。</summary>
    [JsonPropertyName("blendMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlendMode? BlendMode { get; init; }

    /// <summary>蒙版文件名。非 null 时必须恰为 <c>"{id}.mask.png"</c>（<c>ProjectStore.swift:223</c>）。</summary>
    [JsonPropertyName("maskFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaskFile { get; init; }

    /// <summary>蒙版是否启用。非 null 时要求 <see cref="MaskFile"/> 非 null（<c>ProjectStore.swift:224</c>）。</summary>
    [JsonPropertyName("maskEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MaskEnabled { get; init; }

    /// <summary>
    /// 活蒙版链的来源图层（v5 起）。也是剪贴蒙版的载体。
    /// 见 <c>docs/project-format.md</c>。
    /// </summary>
    [JsonPropertyName("maskSourceID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MaskSourceId { get; init; }

    /// <summary>调整层参数。v7 起；v9 起才有部分 kind。</summary>
    [JsonPropertyName("adjustment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerAdjustmentRecord? Adjustment { get; init; }

    /// <summary>脱离图层的蒙版在文档空间的位置。非 null 时要求 <see cref="MaskFile"/> 非 null。</summary>
    [JsonPropertyName("maskPlacement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerTransform? MaskPlacement { get; init; }

    /// <summary>
    /// 蒙版是否跟随图层。<b>null（缺省）= 跟随</b>，见 <c>ProjectStore.swift:49-50</c> 与
    /// <c>LayerMask.swift:215</c> 的 <c>layer.maskLinked ?? true</c>。
    /// </summary>
    [JsonPropertyName("maskLinked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MaskLinked { get; init; }

    /// <summary>形状图层样式，原样透传。</summary>
    [JsonPropertyName("shape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Shape { get; init; }

    /// <summary>描边与投影，原样透传。</summary>
    [JsonPropertyName("effects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Effects { get; init; }

    /// <summary>可编辑文本元数据，原样透传。</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Text { get; init; }
}

/// <summary>调整层记录。对应 <c>ProjectStore.swift:46</c> 的 <c>LayerAdjustment</c>。</summary>
public sealed record LayerAdjustmentRecord
{
    /// <summary>
    /// 调整类型字符串。必须与 manifest 一致，例如 <c>"Levels"</c>、<c>"Curves"</c>。
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>其余参数，原样透传。形状随 kind 各异，不在此处建模。</summary>
    [JsonPropertyName("settings")]
    public System.Text.Json.Nodes.JsonObject Settings { get; init; } = new();
}

/// <summary>对齐参考线。对应 <c>Document/Guides.swift:5-8</c> 的 <c>CanvasGuide</c>。</summary>
public sealed record CanvasGuide
{
    /// <summary>参考线 UUID。</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>轴向：<c>"horizontal"</c> 落在文档 Y 上，<c>"vertical"</c> 落在 X 上。</summary>
    [JsonPropertyName("axis")]
    public string Axis { get; init; } = "horizontal";

    /// <summary>位置（文档像素），须有限且 |position| ≤ 1_000_000。</summary>
    [JsonPropertyName("position")]
    public double Position { get; init; }
}