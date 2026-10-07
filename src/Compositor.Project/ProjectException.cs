namespace Compositor.Project;

/// <summary>
/// <c>.comp</c> 工程读写失败的原因。与 macOS 版 <c>ProjectStore.swift:64-75</c> 的
/// <c>ProjectError</c> 五个 case 一一对应。
/// </summary>
public enum ProjectErrorKind
{
    /// <summary>包结构损坏，或元数据不合规。对应 <c>.invalid</c>。</summary>
    Invalid,

    /// <summary>格式版本不在支持范围，携带读到的版本号。对应 <c>.version</c>。</summary>
    Version,

    /// <summary>工程内的某个图像缺失或损坏。对应 <c>.missingImage</c>。</summary>
    MissingImage,

    /// <summary>超过画布、图层、文件体积或文档像素预算上限。对应 <c>.tooLarge</c>。</summary>
    TooLarge,

    /// <summary>图像编码失败。对应 <c>.encode</c>。</summary>
    Encode,
}

/// <summary><c>.comp</c> 工程读写失败时抛出的异常。</summary>
/// <remarks>
/// 错误类别走 <see cref="Kind"/> 而不是塞进 <see cref="Exception.Message"/>：
/// 文案要引用当前的 <c>ImagingLimits</c> 常量拼出来（Mac 侧就是插值出来的），
/// 而 <see cref="Exception.Message"/> 在构造时就固化了。
/// </remarks>
public sealed class ProjectException : Exception
{
    /// <summary>构造一个工程异常。</summary>
    /// <param name="kind">错误类别。</param>
    /// <param name="version">仅 <see cref="ProjectErrorKind.Version"/> 使用：读到的版本号。</param>
    /// <param name="detail">可选的诊断细节，会附加在文案末尾，便于定位是哪条 guard 拒绝的。</param>
    /// <param name="innerException">可选的底层异常。</param>
    public ProjectException(
        ProjectErrorKind kind,
        int? version = null,
        string? detail = null,
        Exception? innerException = null)
        : base(Describe(kind, version, detail), innerException)
    {
        Kind = kind;
        Version = version;
        Detail = detail;
    }

    /// <summary>错误类别。</summary>
    public ProjectErrorKind Kind { get; }

    /// <summary>读到的 manifest 版本号；仅 <see cref="ProjectErrorKind.Version"/> 非 null。</summary>
    public int? Version { get; }

    /// <summary>诊断细节，通常标明是哪一条 guard。</summary>
    public string? Detail { get; }

    private static string Describe(ProjectErrorKind kind, int? version, string? detail)
    {
        var budgetMegapixels = Imaging.ImagingLimits.DocumentBudgetMegapixels;
        var maxSide = Imaging.ImagingLimits.MaxSide.ToString(
            "N0", System.Globalization.CultureInfo.InvariantCulture);

        var text = kind switch
        {
            ProjectErrorKind.Invalid =>
                "This is not a valid Compositor project, or its metadata is damaged.",
            ProjectErrorKind.Version =>
                $"This project uses format version {version}. This app supports versions "
                + $"{ProjectManifest.MinSupportedVersion}–{ProjectManifest.CurrentVersion}.",
            ProjectErrorKind.MissingImage =>
                "An image inside the project is missing or damaged. "
                + "The current document has not been replaced.",
            ProjectErrorKind.TooLarge =>
                $"This project exceeds the supported canvas, layer, file-size, or "
                + $"{budgetMegapixels}-megapixel document limit.",
            ProjectErrorKind.Encode =>
                "An image could not be saved. The previous project has not been replaced.",
            _ => "This is not a valid Compositor project, or its metadata is damaged.",
        };

        return detail is null ? text : $"{text} [{detail}]";
    }
}