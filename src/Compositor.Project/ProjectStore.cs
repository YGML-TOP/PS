using System.Text.Json;
using Compositor.Core;
using Compositor.Imaging;

namespace Compositor.Project;

/// <summary>
/// <c>.comp</c> 包的读写。实现契约 v1.2 的 <see cref="IProjectStore"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>包形态：</b><c>.comp</c> 在两侧都是<b>目录</b>，不是 zip、也不是单文件。
/// Mac 侧 <c>ProjectStore.swift:147</c> 要求 <c>resourceValues(.isDirectoryKey).isDirectory == true</c>，
/// Windows 文件系统允许目录带任意扩展名，所以「名为 <c>foo.comp</c> 的目录」在两边都成立。
/// 写成 zip 会让 Mac 版直接拒收这个工程。
/// </para>
/// <para>
/// <b>目录结构（对照 ProjectStore.swift:112-121）：</b>
/// <code>
/// foo.comp/
///   manifest.json
///   images/
///     &lt;图层UUID大写&gt;.png
///     &lt;图层UUID大写&gt;.mask.png
///   QuickLook/          ← 读时忽略；写时本实现不产出
/// </code>
/// </para>
/// <para>
/// <b>🔴 UUID 大小写：</b>Swift 的 <c>UUID.uuidString</c> 输出<b>大写</b>，.NET 的
/// <c>Guid.ToString()</c> 输出<b>小写</b>。Mac 在 <c>ProjectStore.swift:223,243</c> 用前者校验文件名，
/// 所以这里一律走 <see cref="ProjectManifest.ImageFileName"/> / <see cref="ProjectManifest.MaskFileName"/>，
/// <b>永不</b>直接用 <c>Guid.ToString()</c>。少一个大写，Mac 就拒收整个工程。
/// </para>
/// <para>
/// <b>🔴 文件名不从快照里取：</b>保存时文件名一律由 <c>LayerNode.Id</c> 推导，忽略
/// <c>LayerNode.ImageFile</c>。Mac 的 save 会把 manifest 里的 <c>imageFile</c> 原样写出去，
/// 而它的 validator 又强制该值必须等于 <c>"&lt;id&gt;.png"</c>——也就是说 Mac 版也只能写推导值。
/// 由推导值统一产出，两侧结果必然一致。
/// </para>
/// <para>
/// <b>🔴 当前是有损的（save 会丢数据）：</b>契约 v1.2 的 <see cref="LayerNode"/> 还没有
/// <c>shape</c> / <c>effects</c> / <c>text</c> 三个字段，<see cref="ProjectSnapshot"/> 也还没有
/// <c>guides</c> / <c>resolution</c>。这五项在 Mac 的 manifest 里都存在。
/// 本实现在 <b>load 时</b>（而不是 save 时）就发出警告，让用户在覆盖保存之前就知道要丢东西。
/// 详见 <c>docs/contract-gaps.md</c>。
/// </para>
/// <para>
/// <b>缩略图：</b>Mac 的 <c>ImportedImage</c> 带一张 96px 缩略图（<c>ProjectStore.swift:184-188</c>），
/// 契约 v1.2 的 <see cref="IProjectAsset"/> 没有这个字段，所以本实现不缓存缩略图，
/// 由 UI 层按需从 <see cref="IProjectAsset.Pixels"/> 现算。这是<b>性能差异不是格式差异</b>——
/// <c>.comp</c> 包里本来就不存缩略图，Mac 每次 load 也是现算的。
/// </para>
/// </remarks>
public sealed class ProjectStore : IProjectStore
{
    /// <summary>创建工程存储。</summary>
    public ProjectStore()
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// 🔴 <b>本类不触发该事件。</b>契约把它交给「能观察到文件系统变化」的那一层；
    /// Windows 上正确的做法是 App 层持一个 <c>FileSystemWatcher</c>，
    /// 变更后比对 <c>ProjectDigest.Compute</c>，不同才在此处转发。
    /// 在存储层内部埋轮询会引入一个持续占用文件的常驻对象，
    /// 且在 <c>SaveAsync</c> 自己写包时必然误报。
    /// </remarks>
    public event Action<string>? ExternalChangeDetected;

    /// <summary>
    /// 读取 <c>.comp</c> 包。
    /// </summary>
    /// <param name="packagePath">包路径（目录）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>工程快照。</returns>
    /// <exception cref="ProjectException">
    /// 包不存在/不是目录、manifest 非法或版本不受支持、包内资源缺失或损坏、超出预算。
    /// </exception>
    public async Task<ProjectSnapshot> LoadAsync(string packagePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        ct.ThrowIfCancellationRequested();

        // 解码是 CPU 密集的同步工作，丢到线程池避免堵住调用方（通常是 UI 的 IO 线程）。
        return await Task.Run(() => Load(packagePath, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 写入 <c>.comp</c> 包。
    /// </summary>
    /// <param name="snapshot">待保存的快照。</param>
    /// <param name="packagePath">包路径（目录）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步任务。</returns>
    /// <exception cref="ProjectException">
    /// 快照缺图层像素、蒙版不是 Gray8、超出预算、manifest 超 4 MB，或落盘失败。
    /// </exception>
    /// <remarks>
    /// <b>失败不破坏原包。</b>实现方式是先写同级的临时目录，成功后再换名；
    /// 换名中途失败会把旧目录改回原位。
    /// 这对应 Mac 的 <c>NSFileCoordinator</c> + <c>package.write(options: .atomic)</c>（<c>ProjectStore.swift:125-131</c>）。
    /// </remarks>
    public async Task SaveAsync(ProjectSnapshot snapshot, string packagePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        ct.ThrowIfCancellationRequested();

        await Task.Run(() => Save(snapshot, packagePath, ct), ct).ConfigureAwait(false);
    }

    private static ProjectSnapshot Load(string packagePath, CancellationToken ct)
    {
        var root = Path.GetFullPath(packagePath);

        if (!Directory.Exists(root))
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: $"工程包不存在或不是目录：{root}");
        }

        // ---- manifest ----
        var manifestPath = Path.Combine(root, PackageLayout.ManifestFileName);
        PackageLayout.CheckFile(manifestPath, root, PackageLayout.MaxManifestBytes);

        var manifestBytes = File.ReadAllBytes(manifestPath);

        ProjectManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ProjectManifest>(manifestBytes, ProjectJson.Options)
                ?? throw new ProjectException(ProjectErrorKind.Invalid, detail: "manifest 反序列化为空");
        }
        catch (ProjectException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ProjectException(ProjectErrorKind.Invalid, detail: $"manifest 不是合法 JSON：{ex.Message}", innerException: ex);
        }

        // 头部检查与完整校验合并在一次调用里：ProjectValidator 覆盖 G01/G02（格式标识、版本范围）
        // 以及其余 26 条守卫，分开写只会让两条路径漂移。
        ProjectValidator.Validate(manifest);

        WarnAboutUnrepresentableFields(manifest);

        // ---- 包内资源 ----
        // 两个独立累加器，对应 Mac 的 `var pixels = 0, maskPixels = 0`（ProjectStore.swift:162）。
        // 共用一个会把「图层 + 蒙版」的合法总量误判成超预算。
        var imageBudget = new SizeBudget();
        var maskBudget = new SizeBudget();

        var images = new Dictionary<Guid, IProjectAsset>();
        var masks = new Dictionary<Guid, IProjectAsset>();

        var imagesRoot = Path.Combine(root, PackageLayout.ImagesDirectoryName);

        foreach (var layer in manifest.Layers)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var isMask in stackalloc[] { false, true })
            {
                var fileName = isMask ? layer.MaskFile : layer.ImageFile;
                if (fileName is null)
                {
                    continue;
                }

                var asset = ReadAsset(root, imagesRoot, fileName, isMask, isMask ? maskBudget : imageBudget, ct);

                if (isMask)
                {
                    masks[layer.Id] = asset;
                }
                else
                {
                    images[layer.Id] = asset;
                }
            }
        }

        return new ProjectSnapshot
        {
            Version = manifest.Version,
            Size = manifest.Size,
            ColorSpace = manifest.ColorSpace,
            DocumentId = manifest.DocumentId,
            ActiveLayerId = manifest.ActiveLayerId,
            Layers = BuildLayerNodes(manifest),
            Images = images,
            Masks = masks,
        };
    }

    private static IProjectAsset ReadAsset(
        string root,
        string imagesRoot,
        string fileName,
        bool isMask,
        SizeBudget budget,
        CancellationToken ct)
    {
        var assetPath = PackageLayout.ResolveInside(imagesRoot, fileName);

        // 缺文件要单独分一类。
        //
        // ⚠️ 这里**故意与 macOS 的分类不同**：ProjectStore.swift:271-277 的 checkFile
        // 对不存在的文件也走同一条 `values.isRegularFile == true` 判定，落到 .tooLarge。
        // 也就是说 Mac 会把「文件丢了」告诉用户「这个工程太大」——两边都拒绝，
        // 工程兼容性完全一致，但错误文案是错的。
        // 这里归到 MissingImage（"工程内的一张图丢失或损坏，当前文档未被替换"），
        // 因为这只是给 UI 的分类，不影响任何字节级行为。
        if (!File.Exists(assetPath))
        {
            throw new ProjectException(
                ProjectErrorKind.MissingImage,
                detail: $"包内缺少资源 {fileName}");
        }

        PackageLayout.CheckFile(assetPath, root, PackageLayout.MaxAssetBytes);

        var bytes = File.ReadAllBytes(assetPath);

        // 预算在解码**之前**累加：先解码再判，会让一个 500 MB 的坏文件先把内存吃穿。
        // 头解析不出来时不计入——DecodePng 紧接着就会抛 Unreadable，
        // 没有必要为一个注定失败的资产改预算状态。
        if (PngHeader.TryParse(bytes, out var header))
        {
            budget.Add(header.Width, header.Height);
        }

        try
        {
            return ProjectAsset.DecodePng(
                bytes,
                isMask ? ProjectPixelFormat.Gray8 : ProjectPixelFormat.PremultipliedRgba8);
        }
        catch (ImageCodecException ex)
        {
            throw new ProjectException(
                isMask ? ProjectErrorKind.Invalid : ProjectErrorKind.MissingImage,
                detail: $"{fileName} 无法解码（{(ex.Kind ?? (ImageImportError?)null)?.ToString() ?? ex.Message}）",
                innerException: ex);
        }
    }

    private static void Save(ProjectSnapshot snapshot, string packagePath, CancellationToken ct)
    {
        var manifest = BuildManifest(snapshot);
        ProjectValidator.Validate(manifest);

        var root = Path.GetFullPath(packagePath);
        var parent = Path.GetDirectoryName(root)
            ?? throw new ProjectException(ProjectErrorKind.Encode, detail: $"无法确定包路径的父目录：{root}");

        var token = Guid.NewGuid().ToString("N")[..8];
        var staging = Path.Combine(parent, $".{Path.GetFileName(root)}.staging-{token}");
        var backup = Path.Combine(parent, $".{Path.GetFileName(root)}.backup-{token}");

        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(staging);

        try
        {
            var imagesRoot = Path.Combine(staging, PackageLayout.ImagesDirectoryName);
            Directory.CreateDirectory(imagesRoot);

            WriteAssets(snapshot, manifest, imagesRoot, ct);

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectJson.Options);
            if (manifestBytes.LongLength > PackageLayout.MaxManifestBytes)
            {
                // 对应 ProjectStore.swift:111。上限必须在写盘前判：写完再发现超限，
                // 包已经是个半成品了。
                throw new ProjectException(
                    ProjectErrorKind.TooLarge,
                    detail: $"manifest {manifestBytes.LongLength} 字节，超过 {PackageLayout.MaxManifestBytes}");
            }

            File.WriteAllBytes(Path.Combine(staging, PackageLayout.ManifestFileName), manifestBytes);

            ct.ThrowIfCancellationRequested();
            Swap(staging, backup, root);
        }
        catch
        {
            TryDelete(staging);
            TryDelete(backup);
            throw;
        }

        TryDelete(backup);
    }

    private static void WriteAssets(
        ProjectSnapshot snapshot,
        ProjectManifest manifest,
        string imagesRoot,
        CancellationToken ct)
    {
        var imageBudget = new SizeBudget();
        var maskBudget = new SizeBudget();

        foreach (var layer in manifest.Layers)
        {
            ct.ThrowIfCancellationRequested();

            if (layer.ImageFile is not null)
            {
                var asset = RequireAsset(snapshot.Images, layer.Id, layer.ImageFile);

                if (asset.Format != ProjectPixelFormat.PremultipliedRgba8)
                {
                    throw new ProjectException(
                        ProjectErrorKind.Invalid,
                        detail: $"{layer.ImageFile} 是 {asset.Format}，图层像素必须是 PremultipliedRgba8");
                }

                imageBudget.Add(asset.Width, asset.Height);
                File.WriteAllBytes(
                    Path.Combine(imagesRoot, layer.ImageFile),
                    Encode(asset, ProjectErrorKind.Encode, layer.ImageFile));
            }

            if (layer.MaskFile is not null)
            {
                var asset = RequireAsset(snapshot.Masks, layer.Id, layer.MaskFile);

                // 写之前就拒 Gray8 以外的格式。Mac 的 save 在 ProjectStore.swift:93
                // 也会查 LayerMask.isValid，但那是**存完才知道**——一旦放行，
                // 用户会拿到一个「现在能打开、发给同事打不开」的包。
                if (asset.Format != ProjectPixelFormat.Gray8)
                {
                    throw new ProjectException(
                        ProjectErrorKind.Invalid,
                        detail: $"{layer.MaskFile} 是 {asset.Format}，蒙版必须是 Gray8（8 位灰度、无 alpha）");
                }

                maskBudget.Add(asset.Width, asset.Height);
                File.WriteAllBytes(
                    Path.Combine(imagesRoot, layer.MaskFile),
                    Encode(asset, ProjectErrorKind.Encode, layer.MaskFile));
            }
        }
    }

    private static byte[] Encode(IProjectAsset asset, ProjectErrorKind kind, string fileName)
    {
        if (asset is not ProjectAsset projectAsset)
        {
            throw new ProjectException(
                kind,
                detail: $"{fileName} 的资产类型是 {asset.GetType().Name}，需要 ProjectAsset");
        }

        try
        {
            return projectAsset.EncodePng();
        }
        catch (ImageCodecException ex)
        {
            throw new ProjectException(kind, detail: $"{fileName} 编码失败：{ex.Message}", innerException: ex);
        }
    }

    private static IProjectAsset RequireAsset(IReadOnlyDictionary<Guid, IProjectAsset> source, Guid id, string fileName)
        => source.TryGetValue(id, out var asset)
            ? asset
            : throw new ProjectException(
                ProjectErrorKind.MissingImage,
                detail: $"图层 {id} 声明了 {fileName}，但快照的资产字典里没有对应项");

    /// <summary>
    /// 把暂存目录换到正式位置。旧包先改名保留，任何一步失败都改回来。
    /// </summary>
    private static void Swap(string staging, string backup, string root)
    {
        // .comp 在两侧都必须是**目录**（ProjectStore.swift:147 要求 isDirectory）。
        // 但目标位置可能已被一个同名**文件**占着（用户之前存过 foo.comp 单文件，
        // 或者从别的工具导出了同名产物），这时也必须能被替换掉。
        // 所以目录与文件要分别走 Move/Delete，不能一律 File.Move。
        var rootIsDirectory = Directory.Exists(root);
        var rootIsFile = !rootIsDirectory && File.Exists(root);
        var hadPrevious = rootIsDirectory || rootIsFile;

        if (hadPrevious)
        {
            if (rootIsDirectory)
            {
                Directory.Move(root, backup);
            }
            else
            {
                File.Move(root, backup);
            }
        }

        try
        {
            Directory.Move(staging, root);
        }
        catch
        {
            // 换名失败 = 原包必须回到原位，否则用户会丢掉上一次保存的全部内容。
            if (hadPrevious && !Directory.Exists(root) && !File.Exists(root))
            {
                if (rootIsDirectory)
                {
                    Directory.Move(backup, root);
                }
                else
                {
                    File.Move(backup, root);
                }
            }

            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清暂存目录失败不该盖掉真正的失败原因。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IReadOnlyList<LayerNode> BuildLayerNodes(ProjectManifest manifest)
    {
        var nodes = new List<LayerNode>(manifest.Layers.Count);

        foreach (var layer in manifest.Layers)
        {
            nodes.Add(new LayerNode
            {
                Id = layer.Id,
                Name = layer.Name,
                IsVisible = layer.IsVisible,
                Transform = layer.Transform,

                // manifest 的可空字段在这里收敛成契约的非空形状。
                Opacity = (float)(layer.Opacity ?? 1.0),
                BlendMode = layer.BlendMode ?? BlendMode.Normal,
                IsGroup = layer.IsGroup ?? false,

                // 🔴 ParentId 必须在这里搬过去。漏掉它不会报错、不会崩，
                // 只会让**所有图层都变成根层**——分组结构在一次保存后彻底消失，
                // 而症状要等到用户在 Mac 上打开才发现。
                ParentId = layer.ParentId,

                ImageFile = layer.ImageFile,
                MaskFile = layer.MaskFile,
                MaskEnabled = layer.MaskEnabled ?? true,
                MaskLinked = layer.MaskLinked,
                MaskSourceId = layer.MaskSourceId,
                MaskPlacement = layer.MaskPlacement,

                Adjustment = layer.Adjustment is null
                    ? null
                    : new AdjustmentSpec
                    {
                        Kind = layer.Adjustment.Kind,
                        Settings = (System.Text.Json.Nodes.JsonObject)layer.Adjustment.Settings.DeepClone(),
                    },
            });
        }

        return nodes;
    }

    private static ProjectManifest BuildManifest(ProjectSnapshot snapshot)
    {
        var layers = new List<ProjectLayerRecord>(snapshot.Layers.Count);

        foreach (var layer in snapshot.Layers)
        {
            var hasImage = snapshot.Images.ContainsKey(layer.Id);
            var hasMask = snapshot.Masks.ContainsKey(layer.Id);

            layers.Add(new ProjectLayerRecord
            {
                Id = layer.Id,
                Name = layer.Name,
                IsVisible = layer.IsVisible,
                Transform = layer.Transform,

                // 文件名一律推导，不读 layer.ImageFile / layer.MaskFile。理由见类型注释。
                ImageFile = hasImage ? ProjectManifest.ImageFileName(layer.Id) : null,
                MaskFile = hasMask ? ProjectManifest.MaskFileName(layer.Id) : null,

                ParentId = layer.ParentId,

                // 默认值一律写成 null，让 manifest 保持 Mac 那边的最简形态。
                // 写 `false` / `1` / `"normal"` 同样合法（validator 用 `!= true` / `?? 1` 判），
                // 但那会让「Mac 写出的包」和「Windows 写出的包」字节不同，白白多一类排查。
                IsGroup = layer.IsGroup ? true : null,
                Opacity = layer.Opacity == 1.0f ? null : layer.Opacity,
                BlendMode = layer.BlendMode == BlendMode.Normal ? null : layer.BlendMode,
                MaskEnabled = layer.MaskEnabled ? null : false,
                MaskLinked = layer.MaskLinked,
                MaskSourceId = layer.MaskSourceId,
                MaskPlacement = layer.MaskPlacement,

                Adjustment = layer.Adjustment is null
                    ? null
                    : new LayerAdjustmentRecord
                    {
                        Kind = layer.Adjustment.Kind,
                        Settings = (System.Text.Json.Nodes.JsonObject)layer.Adjustment.Settings.DeepClone(),
                    },

                // shape / effects / text：契约层无对应字段，无从写回。见类型注释与 docs/contract-gaps.md。
                Shape = null,
                Effects = null,
                Text = null,
            });
        }

        return new ProjectManifest
        {
            Version = snapshot.Version,
            ColorSpace = snapshot.ColorSpace,
            DocumentId = snapshot.DocumentId,
            Width = snapshot.Size.Width,
            Height = snapshot.Size.Height,
            ActiveLayerId = snapshot.ActiveLayerId,
            Layers = layers,

            // guides / resolution 契约层同样没有承载字段。
            Guides = null,
        };
    }

    /// <summary>
    /// manifest 里存在、但契约 v1.2 承载不了的字段，在<b>读取时</b>就报警。
    /// </summary>
    /// <remarks>
    /// 为什么不在保存时报警：等到保存再报，用户已经把旧包覆盖了。
    /// 读取时报警，用户至少有机会先导出。
    /// </remarks>
    private static void WarnAboutUnrepresentableFields(ProjectManifest manifest)
    {
        if (ProjectJson.WarningSink is not { } sink)
        {
            return;
        }

        if (manifest.Guides is { Count: > 0 })
        {
            sink($"工程含 {manifest.Guides.Count} 条参考线，契约 v1.2 的 ProjectSnapshot 无对应字段，重新保存会丢失。");
        }

        if (manifest.Resolution is not null)
        {
            sink($"工程分辨率 {manifest.Resolution} ppi，契约 v1.2 的 ProjectSnapshot 无对应字段，重新保存会丢失。");
        }

        var lossyLayers = manifest.Layers
            .Where(l => l.Shape is not null || l.Effects is not null || l.Text is not null)
            .Select(l => l.Name)
            .ToArray();

        if (lossyLayers.Length > 0)
        {
            sink(
                $"工程含 {lossyLayers.Length} 个带形状/特效/文字的图层" +
                $"（{string.Join("、", lossyLayers.Take(5))}{(lossyLayers.Length > 5 ? " 等" : string.Empty)}），" +
                "契约 v1.2 的 LayerNode 无对应字段，重新保存会丢失。");
        }
    }

    /// <summary>供测试与诊断确认本实现是否真的注册了外部变更回调。</summary>
    internal bool HasExternalChangeListener => ExternalChangeDetected is not null;
}
