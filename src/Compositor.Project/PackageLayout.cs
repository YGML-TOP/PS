using System.Globalization;
using System.Text;

namespace Compositor.Project;

/// <summary>
/// <c>.comp</c> 包的路径安全与体积预算。对应 macOS 版
/// <c>ProjectStore.swift:146-149,271-277</c> 的 <c>checkFile</c> 与
/// <c>264-269</c> 的 <c>checkSize</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>🔴 Windows 上不能照抄 Mac 的路径检查。</b>Mac 的
/// <c>file.resolvingSymlinksInPath().standardizedFileURL.path.hasPrefix(root)</c>
/// 依赖两件在 Windows 上不等价的事：<c>resolvingSymlinksInPath</c> 会解析符号链接，
/// 而 .NET 的 <c>Path.GetFullPath</c> <b>不会</b>解析 reparse point（junction / symlink）。
/// 只做 <c>GetFullPath</c> 就比较前缀，一个指向包外的 junction 能被整个绕过去。
/// </para>
/// <para>
/// 本移植取<b>更严</b>的口径：包内任何一段是 reparse point 就直接拒。
/// 这同时覆盖了符号链接逃逸与 junction 逃逸，代价是"包里放了 junction"这种罕见布局也过不了。
/// </para>
/// <para>
/// 另：Mac 的 <c>hasPrefix</c> 是大小写敏感，Windows 路径大小写不敏感，
/// 这里必须用 <see cref="StringComparison.OrdinalIgnoreCase"/>。
/// </para>
/// </remarks>
public static class PackageLayout
{
    /// <summary>manifest 的文件名。对应 <c>ProjectStore.swift:113</c>。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>资源目录名。对应 <c>ProjectStore.swift:114,166</c>。</summary>
    public const string ImagesDirectoryName = "images";

    /// <summary>Quick Look 预览目录名。对应 <c>ProjectStore.swift:117</c>。</summary>
    public const string QuickLookDirectoryName = "QuickLook";

    /// <summary>manifest 字节数上限 4 MB。对应 <c>ProjectStore.swift:149,111</c>。</summary>
    public const long MaxManifestBytes = 4L * 1024 * 1024;

    /// <summary>单个资源文件字节数上限 512 MB。对应 <c>ProjectStore.swift:167</c>。</summary>
    public const long MaxAssetBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 把包内相对路径解析成绝对路径，并确认它没有逃出包根。
    /// </summary>
    /// <param name="packageRoot">包根目录。</param>
    /// <param name="relativePath">包内相对路径，例如 <c>images/&lt;uuid&gt;.png</c>。</param>
    /// <returns>规范化后的绝对路径。</returns>
    /// <exception cref="ProjectException">
    /// 路径逃出包根、含 reparse point、或含非法路径字符。
    /// </exception>
    public static string ResolveInside(string packageRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageRoot);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));

        // 相对路径里若带盘符或根，直接拒——包内文件名不可能是绝对路径。
        if (Path.IsPathRooted(relativePath))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "path: rooted relative path");
        }

        string combined;
        try
        {
            // GetFullPath 会消解 "." 与 ".."，但不消解 reparse point（见类型注释）。
            combined = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "path: malformed", innerException: e);
        }

        if (!IsInside(root, combined))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "path: escapes package");
        }

        if (ContainsReparsePoint(root, combined))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "path: reparse point");
        }

        return combined;
    }

    /// <summary>
    /// 确认一个已存在的文件是普通文件、不超过体积上限。
    /// </summary>
    /// <param name="filePath">文件的绝对路径。</param>
    /// <param name="packageRoot">包根目录。</param>
    /// <param name="maximumBytes">体积上限。</param>
    /// <exception cref="ProjectException">
    /// 逃出包根或含 reparse point（<see cref="ProjectErrorKind.Invalid"/>）、
    /// 不是普通文件、或超过上限（<see cref="ProjectErrorKind.TooLarge"/>）。
    /// </exception>
    public static void CheckFile(string filePath, string packageRoot, long maximumBytes)
    {
        var resolved = ResolveInside(packageRoot, Path.GetRelativePath(Path.GetFullPath(packageRoot), filePath));

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(resolved);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "file: missing", innerException: e);
        }

        // Mac 的 isSymbolicLink != true 在这里对应 ReparsePoint 检查（ResolveInside 已查过，
        // 这里再查一次是为了挡住检查与打开之间被换掉的竞态）。
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "file: reparse point");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "file: not a regular file");
        }

        long size;
        try
        {
            size = new FileInfo(resolved).Length;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: "file: unreadable", innerException: e);
        }

        if (size > maximumBytes)
        {
            throw new ProjectException(ProjectErrorKind.TooLarge, detail: "file: too large");
        }
    }

    /// <summary>
    /// 判断 <paramref name="candidate"/> 是否位于 <paramref name="root"/> 之内。
    /// </summary>
    /// <param name="root">包根绝对路径。</param>
    /// <param name="candidate">待判定的绝对路径。</param>
    /// <returns>在包内返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 必须比较完整目录段，不能只做前缀字符串比较：
    /// <c>C:\pkg</c> 与 <c>C:\pkg-evil</c> 的字符串前缀是相交的。
    /// </remarks>
    public static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (normalizedCandidate.Length <= normalizedRoot.Length)
        {
            return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 根之后必须紧跟分隔符，避免 pkg / pkg-evil 混淆。
        var separator = Path.DirectorySeparatorChar;
        return normalizedCandidate[normalizedRoot.Length] == separator
            || normalizedRoot.EndsWith(separator);
    }

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        var current = candidate;

        // 只查根到目标之间的每一段，不查根本身（根可能是用户的任意目录）。
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 查不到属性时保守放行给下一步，让 CheckFile 再判一次。
                }
            }

            current = parent;
        }

        return false;
    }
}

/// <summary>
/// 文档像素预算累加器。对应 macOS 版 <c>ProjectStore.swift:264-269</c> 的 <c>checkSize</c>。
/// </summary>
/// <remarks>
/// <b>⚠️ 图层像素与蒙版像素是两个独立的累加器</b>（Mac 的 <c>pixels</c> 与 <c>maskPixels</c>，
/// 见 <c>ProjectStore.swift:94-95</c>），各自吃满一份 <c>documentPixelBudget</c>，
/// 不是两者共享一份。用一个累加器会让"图层占满预算 + 蒙版很小"的正常工程被拒。
/// </remarks>
public sealed class SizeBudget
{
    private long _used;

    /// <summary>已计入的像素数。</summary>
    public long Used => _used;

    /// <summary>本次预算上限（像素）。</summary>
    public int Limit { get; }

    /// <summary>创建一个预算累加器。</summary>
    /// <param name="limit">上限，通常取 <c>ImagingLimits.DocumentPixelBudget</c>。</param>
    public SizeBudget(int limit = Imaging.ImagingLimits.DocumentPixelBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        Limit = limit;
    }

    /// <summary>
    /// 计入一张图的像素并校验。对应 <c>ProjectStore.swift:264-269</c>。
    /// </summary>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <exception cref="ProjectException">
    /// 宽高越界，或累计超出 <see cref="Limit"/>。
    /// </exception>
    public void Add(int width, int height)
    {
        var max = Imaging.ImagingLimits.MaxSide;
        if (width < 1 || width > max || height < 1 || height > max)
        {
            throw new ProjectException(ProjectErrorKind.TooLarge, detail: "size: side");
        }

        // 用 long：30000 × 30000 = 9e8 虽然 int 装得下，但这里写 long 可以免疫
        // maxSide 日后放宽导致的溢出。
        var pixels = (long)width * height;

        if (pixels > Limit - _used)
        {
            throw new ProjectException(ProjectErrorKind.TooLarge, detail: "size: budget");
        }

        _used += pixels;
    }
}

/// <summary>
/// 工程包摘要。逐字对照 macOS 版 <c>IO/ProjectDigest.swift:17-31</c>。
/// </summary>
/// <remarks>
/// <para>
/// 计算方式：SHA256(manifest 字节 + 每个资源的名称 UTF-8 + 8 字节大小)，
/// 资源按名称排序。
/// </para>
/// <para>
/// <b>不读资源内容</b>（Mac 侧 <c>ProjectDigest.swift:8-10</c> 明说：每次保存与打开都取一次新鲜摘要，
/// 对大工程逐张哈希会把保存卡住数秒）。像素变了通常也会改大小，靠名字 + 大小足以覆盖。
/// </para>
/// <para>
/// <b>⚠️ 大小的字节序：</b>Mac 侧用 <c>withUnsafeBytes(of: UInt64)</c>，那是<b>本机字节序</b>。
/// x86-64 的 macOS 与 Windows 上恰好都是小端，所以本移植显式写小端并在此标注：
/// 这是"与 Mac 实现在当前两个平台上碰巧一致"，不是"规范要求小端"。
/// </para>
/// <para>
/// <b>用途只在本地变更检测</b>（Mac 侧 <c>:4-6</c>：被同步客户端碰过、被改了权限、
/// 同样字节又存了一遍，摘要不变，因此不算变更）。摘要<b>不入库、也不跨机比对</b>，
/// 所以 manifest 字节的键序差异不会造成互通问题。
/// </para>
/// </remarks>
public static class ProjectDigest
{
    /// <summary>计算包的摘要。</summary>
    /// <param name="packageRoot">包根目录。</param>
    /// <returns>32 字节 SHA256 摘要。</returns>
    /// <exception cref="ProjectException">manifest 缺失或体积超限。</exception>
    public static byte[] Compute(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageRoot);

        var manifestPath = Path.Combine(packageRoot, PackageLayout.ManifestFileName);
        PackageLayout.CheckFile(manifestPath, packageRoot, PackageLayout.MaxManifestBytes);

        using var sha = System.Security.Cryptography.SHA256.Create();

        // 1) manifest 字节，逐字节计入
        var manifest = File.ReadAllBytes(manifestPath);
        sha.TransformBlock(manifest, 0, manifest.Length, null, 0);

        // 2) 资源：按名称排序，只取名字与大小。
        //    Mac 的 contentsOfDirectory(atPath:) 返回的是文件名（含子目录名），
        //    这里用 GetFiles + 剔掉子目录来对齐这个语义。
        var imagesDir = Path.Combine(packageRoot, PackageLayout.ImagesDirectoryName);
        var names = Directory.Exists(imagesDir)
            ? Directory.GetFiles(imagesDir)
                .Where(static f => (File.GetAttributes(f) & FileAttributes.Directory) == 0)
                .Select(static f => Path.GetFileName(f))
                .OrderBy(static n => n, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

        // stackalloc 必须提到循环外（CA2014）：资源多时在循环里每次迭代都占一份栈。
        Span<byte> sizeBytes = stackalloc byte[8];

        foreach (var name in names)
        {
            var filePath = Path.Combine(imagesDir, name);
            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var nameBytes = Encoding.UTF8.GetBytes(name);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);

            var size = new FileInfo(filePath).Length;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(sizeBytes, (ulong)size);
            sha.TransformBlock(sizeBytes.ToArray(), 0, 8, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash ?? throw new ProjectException(ProjectErrorKind.Invalid, detail: "digest: no hash");
    }

    /// <summary>把摘要转成小写十六进制，便于日志与比对。</summary>
    /// <param name="digest">摘要字节。</param>
    /// <returns>64 位小写十六进制字符串。</returns>
    public static string ToHex(byte[] digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return Convert.ToHexString(digest).ToLower(CultureInfo.InvariantCulture);
    }
}