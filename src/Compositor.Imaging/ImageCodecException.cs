namespace Compositor.Imaging;

/// <summary>导入失败的原因。与 macOS 版 <c>ImageImportError</c> 三个 case 一一对应。</summary>
public enum ImageImportError
{
    /// <summary>图片读不出来，可能已损坏或不可访问。对应 <c>.unreadable</c>。</summary>
    Unreadable,

    /// <summary>格式不在允许清单里。对应 <c>.unsupported</c>。</summary>
    Unsupported,

    /// <summary>超过文档像素预算或最长边限制。对应 <c>.tooLarge</c>。</summary>
    TooLarge,

    /// <summary>
    /// 🔴 <b>仅 Windows 版新增</b>：估算本机内存扛不住这次分配。
    /// </summary>
    /// <remarks>
    /// Mac 版<b>没有</b>对应 case —— 它要么扛得住，要么直接 OOM 崩掉，没有预判。
    /// 本移植加这一条是为了把崩溃换成可读的报错，用户体验不同是刻意的。
    /// <b>它不参与格式合法性判定</b>：同一个工程在内存够的机器上照样能打开。
    /// </remarks>
    OutOfMemory,
}

/// <summary>导出失败的原因。与 macOS 版 <c>ExportError</c> 三个 case 一一对应。</summary>
public enum ExportError
{
    /// <summary>画布超过单个表面上限。对应 <c>.tooLarge</c>。</summary>
    TooLarge,

    /// <summary>画布渲染不出来。对应 <c>.render</c>。</summary>
    Render,

    /// <summary>编码失败。对应 <c>.encode</c>。</summary>
    Encode,
}

/// <summary>图片导入/导出失败时抛出的异常。</summary>
/// <remarks>
/// 错误类别走 <see cref="Kind"/> 而不是写进 <see cref="Exception.Message"/>：
/// <c>ImageImporter</c> / <c>ImageExporter</c> 是 <c>actor</c>，错误要跨并发边界传回 UI，
/// 用枚举判定比字符串匹配稳。文案由 UI 层按 <see cref="ImagingLimits"/> 的当前常量拼。
/// </remarks>
public sealed class ImageCodecException : Exception
{
    /// <summary>构造一个带错误类别的异常。</summary>
    /// <param name="kind">错误类别。</param>
    /// <param name="innerException">可选的底层异常。</param>
    public ImageCodecException(ImageImportError kind, Exception? innerException = null)
        : base(Describe(kind), innerException)
    {
        Kind = kind;
    }

    /// <summary>构造一个带错误类别的导出异常。</summary>
    /// <param name="kind">错误类别。</param>
    /// <param name="innerException">可选的底层异常。</param>
    public ImageCodecException(ExportError kind, Exception? innerException = null)
        : base(Describe(kind), innerException)
    {
        ExportKind = kind;
    }

    /// <summary>导入错误类别；导出异常时为 <see langword="null"/>。</summary>
    public ImageImportError? Kind { get; }

    /// <summary>导出错误类别；导入异常时为 <see langword="null"/>。</summary>
    public ExportError? ExportKind { get; }

    private static string Describe(ImageImportError kind) => kind switch
    {
        ImageImportError.Unreadable => "The image could not be read. It may be damaged or unavailable.",
        ImageImportError.Unsupported => "Choose a JPEG, PNG, HEIC, TIFF, or Photoshop (PSD) file.",
        ImageImportError.TooLarge => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "This import exceeds the current {0}-megapixel document budget or {1}-pixel side limit.",
            ImagingLimits.DocumentBudgetMegapixels,
            ImagingLimits.MaxSide.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)),
        ImageImportError.OutOfMemory =>
            "This machine does not have enough free memory to open the image. Close other applications and try again.",
        _ => "The image could not be read.",
    };

    private static string Describe(ExportError kind) => kind switch
    {
        ExportError.TooLarge => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Image export supports canvases up to {0} megapixels and {1} pixels per side.",
            ImagingLimits.MaxSurfaceMegapixels,
            ImagingLimits.MaxSide.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)),
        ExportError.Render => "The canvas could not be rendered. Try a smaller canvas.",
        ExportError.Encode => "The image could not be encoded.",
        _ => "The image could not be encoded.",
    };
}