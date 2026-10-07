namespace Compositor.Core;

/// <summary>标记一个测试方法使用黄金样本回归比对。</summary>
/// <param name="id">样本标识。用于报告与差异图命名，须在全项目唯一。</param>
/// <remarks>
/// 本特性<b>只携带标识，不改变测试行为</b>：真正的比对由测试显式调用
/// <see cref="Golden.CompareRgba"/> 或 <see cref="Golden.CompareProject"/> 完成。
/// 之所以仍然提供，是因为验收标准要求"每个公开类型有测试、混合模式各有黄金样本"，
/// 有了 <c>[GoldenSample]</c> 才能被工具按统一格式收集与归档。
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GoldenSampleAttribute(string id) : Attribute
{
    /// <summary>样本标识，全项目唯一。</summary>
    public string Id { get; } = id;
}

/// <summary>黄金样本比对的结论。</summary>
/// <param name="Passed">是否在容差内完全一致。</param>
/// <param name="DifferingPixels">超出容差的像素个数。一个像素<b>任一通道</b>超容差即计一次。</param>
/// <param name="MaxDelta">全图最大的单通道绝对差（0–255）。</param>
/// <param name="DiffImagePath">差异图路径；通过时为空字符串。</param>
/// <remarks>
/// ⚠️ <b><see cref="MaxDelta"/> 与容差无关，且通过时也可能非 0。</b>
/// 它统计的是"全图每个字节的最大绝对差"，不设阈值。
/// 正确读法：<c>Passed == false</c> 时 <c>MaxDelta</c> 才有意义；
/// <c>Passed == true</c> 时它只说明"最大偏差恰好在容差之内"。
/// <b>不要把「MaxDelta != 0」当成告警信号</b>，那会在容差为 1 时长期误报。
/// </remarks>
public sealed record GoldenResult(bool Passed, int DifferingPixels, int MaxDelta, string DiffImagePath);

/// <summary>黄金样本回归比对框架。</summary>
/// <remarks>
/// 🔴 <b>验收红线</b>：任务书 §5 写明「golden 不通过 = 禁止合并，不接受"先合了再修"」。
/// 所以本框架<b>只报告，不修改被测像素</b>：它读一份基准、比对、写差异图，然后返回结论。
/// 任何"自动修正"的行为都会让黄金样本失去意义。
///
/// <para><b>比对的是直通（straight）RGBA。</b> 基准 PNG 按 PNG 标准是直通 alpha，
/// 而 <see cref="PixelBuffer"/> 内部是预乘（铁律 2），因此比对前会先反预乘。
/// 直接拿预乘字节去和 PNG 比，透明区域的颜色会系统性偏暗。</para>
///
/// <para><b>容差默认 1</b>：.NET 的 <c>Math.Cos/Sin/Pow</c> 与 macOS libm 不保证逐位相同，
/// 手写混合公式时落在 <c>.5</c> 边界的值理论上可能差 1。把容差定 0 会让跨平台回归永不稳定
/// （见 <c>docs/color-space.md</c> §六）。</para>
///
/// <para>⚠️ <b>差异图的落盘位置是当前工作目录</b>，不是项目根。
/// <c>dotnet test</c> 会把工作目录设成测试输出目录（<c>tests/Compositor.Core.Tests/bin/Debug/net10.0/</c>），
/// 所以实际路径形如 <c>tests/Compositor.Core.Tests/bin/Debug/net10.0/test-artifacts/golden/xxx-diff.png</c>。
/// 这类产物<b>不应提交进仓库</b>，SDK 就位后应把 <c>bin/**/test-artifacts/</c> 加进 <c>.gitignore</c>。</para>
///
/// <para>⚠️ <b>未编译验证</b>：本框架与其依赖的 <see cref="PngCodec"/> 都没有 .NET SDK 可编译。
/// SDK 就位后应先跑 <c>GoldenTests</c>，再让任何黄金样本依赖它。</para>
/// </remarks>
public static class Golden
{
    /// <summary>差异图输出目录（相对当前工作目录）。</summary>
    public const string ArtifactDirectory = "test-artifacts";

    /// <summary><c>.comp</c> 工程黄金样本目录里的<b>约定</b>基准 PNG 文件名。</summary>
    /// <remarks>
    /// ⚠️ 这是<b>本框架定的约定</b>，不是从 <c>.comp</c> 格式推导出来的 ——
    /// 格式本身不规定基准图叫什么。等 AI-2 的 <see cref="IProjectStore"/> 就位后，
    /// 这一条应当改为"由工程产出 PNG"的真实链路，而不是继续沿用文件名约定。
    /// </remarks>
    public const string ProjectGoldenFileName = "expected.png";

    /// <summary>与基准 PNG 逐像素比对。容差为每通道允许的最大绝对差。
    /// 失败时把差异图写到 <c>test-artifacts/</c> 供人工判读。</summary>
    /// <param name="goldenPath">基准 PNG 的路径。</param>
    /// <param name="actual">被测像素，内部为预乘 RGBA8，比对前自动反预乘。</param>
    /// <param name="tolerance">每通道允许的最大绝对差，默认 1。</param>
    /// <returns>比对结论；不通过时 <see cref="GoldenResult.DiffImagePath"/> 指向差异图。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="goldenPath"/> 或 <paramref name="actual"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> 为负。</exception>
    /// <exception cref="FileNotFoundException">基准 PNG 不存在。</exception>
    /// <exception cref="InvalidDataException">基准文件不是合法 PNG。</exception>
    public static GoldenResult CompareRgba(string goldenPath, PixelBuffer actual, int tolerance = 1)
    {
        ArgumentNullException.ThrowIfNull(goldenPath);
        ArgumentNullException.ThrowIfNull(actual);
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "容差不能为负。");
        }

        if (!File.Exists(goldenPath))
        {
            throw new FileNotFoundException($"找不到黄金样本基准文件：{goldenPath}", goldenPath);
        }

        (int gw, int gh, byte[] golden) = PngCodec.Decode(File.ReadAllBytes(goldenPath));
        PixelBuffer straight = actual.ToStraightRgba();

        // 尺寸不一致直接判失败：此时"逐像素比对"无从谈起，
        // 且 MaxDelta 取 255 让报告一眼看出是尺寸问题而非精度问题。
        if (gw != straight.Width || gh != straight.Height)
        {
            // 画成整片洋红而不是原图：尺寸不符时逐像素差异本来就无从计算，
            // 输出 actual 原图会让人误以为"没差异"。
            int n = straight.Width * straight.Height;
            var sizeDiff = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                sizeDiff[i * 4] = 255;
                sizeDiff[i * 4 + 1] = 0;
                sizeDiff[i * 4 + 2] = 255;
                sizeDiff[i * 4 + 3] = 255;
            }

            string dimDiff = DiffPath(goldenPath, $"size-{gw}x{gh}-vs-{straight.Width}x{straight.Height}");
            WritePng(dimDiff, straight.Width, straight.Height, sizeDiff);
            return new GoldenResult(false, n, 255, dimDiff);
        }

        int differing = 0;
        int maxDelta = 0;
        int total = straight.Width * straight.Height;
        var diffImage = new byte[total * 4];

        for (int i = 0; i < total * 4; i++)
        {
            byte g = golden[i];
            byte a = straight.Rgba[i];
            int delta = Math.Abs(g - a);
            if (delta > maxDelta)
            {
                maxDelta = delta;
            }

            diffImage[i] = a;
        }

        // 逐像素统计超容差个数：容差是"每通道"，所以一个像素只要任一通道超了就整像素计一次。
        for (int p = 0; p < total; p++)
        {
            bool bad = false;
            for (int ch = 0; ch < 4; ch++)
            {
                if (Math.Abs(golden[p * 4 + ch] - diffImage[p * 4 + ch]) > tolerance)
                {
                    bad = true;
                    break;
                }
            }

            if (!bad)
            {
                continue;
            }

            differing++;
            // 差异像素染成洋红，其 alpha 拉满，保证人眼在灰底上一眼可见
            diffImage[p * 4] = 255;
            diffImage[p * 4 + 1] = 0;
            diffImage[p * 4 + 2] = 255;
            diffImage[p * 4 + 3] = 255;
        }

        if (differing == 0)
        {
            return new GoldenResult(true, 0, maxDelta, string.Empty);
        }

        string diffPath = DiffPath(goldenPath, "diff");
        WritePng(diffPath, straight.Width, straight.Height, diffImage);
        return new GoldenResult(false, differing, maxDelta, diffPath);
    }

    /// <summary>运行由 <c>.comp</c> 工程文件产出 PNG 的比对。</summary>
    /// <param name="fixtureDir">黄金样本目录。</param>
    /// <param name="actual">被测像素。</param>
    /// <param name="tolerance">每通道允许的最大绝对差，默认 1。</param>
    /// <returns>比对结论。</returns>
    /// <remarks>
    /// ⚠️ <b>波次 1 尚未实现真正的 <c>.comp</c> 渲染链路</b>：那要等 AI-2 的
    /// <see cref="IProjectStore"/> 就位。本方法目前退化为「读
    /// <see cref="ProjectGoldenFileName"/> 做像素比对」，
    /// 即只验证<b>像素</b>，不验证<b>工程解析 → 渲染</b>这条链路。
    /// 链路打通后应改为：加载 <c>.comp</c> → 合成 → 比对。
    /// <para>该限制已写入 <c>文档\AI1\ai1-02</c> 的「未完成」一节。</para>
    /// </remarks>
    public static GoldenResult CompareProject(string fixtureDir, PixelBuffer actual, int tolerance = 1)
    {
        ArgumentNullException.ThrowIfNull(fixtureDir);
        return CompareRgba(Path.Combine(fixtureDir, ProjectGoldenFileName), actual, tolerance);
    }

    /// <summary>
    /// 生成差异图路径。
    /// </summary>
    /// <remarks>
    /// <b>为什么带路径哈希</b>：文件名若只用基准图的文件名，
    /// <c>fixtures/a/wand.png</c> 与 <c>fixtures/b/wand.png</c> 会写到同一个
    /// <c>wand-diff.png</c>，后一次覆盖前一次 —— 失败的证据被另一个失败覆盖掉，
    /// 是最难查的一类问题。哈希取自<b>完整路径</b>，因此同名不同目录不会撞。
    /// </remarks>
    private static string DiffPath(string goldenPath, string suffix)
    {
        string stem = Path.GetFileNameWithoutExtension(goldenPath);
        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(goldenPath))))[..8];
        return Path.Combine(ArtifactDirectory, "golden", $"{Sanitize(stem)}-{suffix}-{hash}.png");
    }

    private static void WritePng(string path, int width, int height, byte[] rgba)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, PngCodec.Encode(width, height, rgba));
    }

    /// <summary>把样本标识里的非法文件名字符换成下划线，避免拼路径时出错。</summary>
    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }

        return new string(buffer);
    }
}
