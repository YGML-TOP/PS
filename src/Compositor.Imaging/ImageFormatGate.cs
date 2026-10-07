using SkiaSharp;

namespace Compositor.Imaging;

/// <summary>Windows 版当前真正能解码的位图格式。</summary>
public enum SupportedImageFormat
{
    /// <summary>PNG。Mac 与 Windows 共有。</summary>
    Png,

    /// <summary>JPEG。Mac 与 Windows 共有。</summary>
    Jpeg,
}

/// <summary>
/// 导入格式闸门。决定一个文件「算不算可导入的位图」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要单独一层：</b>SkiaSharp 能解的格式比 Mac 版多（BMP、GIF、ICO、WebP…）。
/// 如果直接信 <c>SKCodec.EncodedFormat</c> 就放行，Windows 版会接受一批 Mac 版拒收的文件，
/// 产出一个 Mac 打不开的工程——这正是本项目最不能出的错。所以这里用<b>显式白名单</b>，
/// 宁可少收，不可多收。
/// </para>
/// <para>
/// <b>⚠️ 已知移植缺口：</b>Mac 版 <c>ImageImporter.decode</c> 允许 JPEG / PNG / HEIC / TIFF
/// 四种（外加可选的 PSD/PSB）。SkiaSharp 官方二进制<b>不含 HEIC 与 TIFF 解码器</b>。
/// 也就是说 HEIC/TIFF 目前在 Windows 上无法导入。要补齐需引入第三方解码器
/// （如 ImageSharp，或 libheif / libtiff 原生绑定），属于 YG 待拍板的 P1 项。
/// 在补齐之前，这里<b>只放行 PNG 与 JPEG</b>，并在 <see cref="MacOnlyFormats"/> 里
/// 显式列出缺口，避免缺口在代码里"看起来不存在"。
/// </para>
/// </remarks>
public static class ImageFormatGate
{
    /// <summary>Windows 版当前放行的格式。</summary>
    public static readonly SupportedImageFormat[] Supported =
    {
        SupportedImageFormat.Png,
        SupportedImageFormat.Jpeg,
    };

    /// <summary>
    /// Mac 版放行、但 Windows 版当前无法解码的格式。列在这里是为了让缺口显式，
    /// 而不是等到用户拿一张 HEIC 才发现。
    /// </summary>
    public static readonly string[] MacOnlyFormats =
    {
        "public.heic",
        "public.tiff",
        "com.adobe.photoshop-image",
        "com.adobe.photoshop-large-image",
    };

    /// <summary>
    /// 判断 Skia 报告的编码格式是否在放行清单内。
    /// </summary>
    /// <param name="format">Skia 从文件头识别出的编码格式。</param>
    /// <param name="supported">识别成功且在清单内时写入对应枚举。</param>
    /// <returns>在放行清单内返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 注意这里<b>只按编码格式判定</b>，不按扩展名。Mac 版判的是 UTType 体系
    /// （系统统一类型），本质上也是看内容而非文件名，所以方向一致。
    /// </remarks>
    public static bool TryAccept(SKEncodedImageFormat format, out SupportedImageFormat supported)
    {
        switch (format)
        {
            case SKEncodedImageFormat.Png:
                supported = SupportedImageFormat.Png;
                return true;

            case SKEncodedImageFormat.Jpeg:
                supported = SupportedImageFormat.Jpeg;
                return true;

            default:
                // 显式列出 Skia 认得、但本项目不放行的格式，避免有人后来加白名单时
                // 不知道还差 HEIC/TIFF。
                supported = default;
                return false;
        }
    }

    /// <summary>
    /// 人类可读的放行清单，用于错误文案。
    /// </summary>
    /// <returns>形如 <c>PNG、JPEG</c> 的文案。</returns>
    public static string SupportedDescription() =>
        string.Join("、", Supported.Select(static f => f switch
        {
            SupportedImageFormat.Png => "PNG",
            SupportedImageFormat.Jpeg => "JPEG",
            _ => f.ToString(),
        }));
}