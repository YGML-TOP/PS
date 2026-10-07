using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Compositor.Core;
using Xunit;

namespace Compositor.Project.Tests;

/// <summary>
/// <c>ProjectStore</c> 的包级实测：真实夹具解包、逐字段断言、字节级往返、存储层反例。
/// </summary>
/// <remarks>
/// <para>
/// <b>与 <c>ProjectValidatorTests</c> 的分工：</b>G01–G28 那 28 条纯 manifest 守卫已经在那一份里
/// 逐条覆盖，本类<b>不重复</b>。这里只验「只有真把包落到磁盘上才会暴露」的东西：
/// 文件名大小写、资源解码、包结构、失败原子性。
/// </para>
/// <para>
/// <b>期望值来源：</b>夹具自身的 manifest 字段（已逐条读过）+
/// macOS <c>ProjectStore.swift</c> 的行为规则。<b>没有任何一条期望值是跑一遍实现再抄下来的。</b>
/// </para>
/// <para>
/// <b>⚠️ 能证明什么、不能证明什么：</b>夹具是 AI-2 自己按 <c>ai2-00</c> 构造的，
/// 所以这里证的是<b>自洽 + 规则符合</b>，<b>证不了与 Mac 版互通</b>。
/// 互通要等阶段二真实 Mac 产出的 <c>.comp</c>。
/// </para>
/// </remarks>
public sealed class ProjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "compositor-ai2-" + Guid.NewGuid().ToString("N")[..10]);

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>三份夹具共同的文档 id（已从三份 manifest 里核对过是同一个）。</summary>
    private static readonly Guid DocumentId = Guid.Parse("0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D");

    public ProjectStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CopyFixture(string name)
    {
        var dest = Path.Combine(_root, name + ".comp");
        CopyDirectory(Path.Combine(FixtureDir, name + ".comp"), dest);
        return dest;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    // ---------------------------------------------------------------- 读取

    [Theory]
    [InlineData("v1-minimal", 1, 1)]
    [InlineData("v7-structure", 7, 6)]
    [InlineData("v11-full", 11, 9)]
    public async Task LoadAsync_逐字段对上夹具(string fixture, int version, int layerCount)
    {
        var snapshot = await new ProjectStore().LoadAsync(CopyFixture(fixture));

        Assert.Equal(version, snapshot.Version);
        Assert.Equal(100, snapshot.Size.Width);
        Assert.Equal(80, snapshot.Size.Height);
        Assert.Equal("sRGB", snapshot.ColorSpace);
        Assert.Equal(DocumentId, snapshot.DocumentId);
        Assert.Equal(layerCount, snapshot.Layers.Count);
    }

    [Theory]
    [InlineData("v1-minimal", "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F")]
    [InlineData("v7-structure", "E1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")]
    [InlineData("v11-full", "E1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")]
    public async Task LoadAsync_活动图层id原样保留(string fixture, string expected)
    {
        var snapshot = await new ProjectStore().LoadAsync(CopyFixture(fixture));

        Assert.Equal(Guid.Parse(expected), snapshot.ActiveLayerId);
        Assert.NotNull(snapshot.ActiveLayerId);
        Assert.Contains(snapshot.Layers, l => l.Id == snapshot.ActiveLayerId!.Value);
    }

    [Fact]
    public async Task LoadAsync_v7的图层树结构逐条对上()
    {
        var snapshot = await new ProjectStore().LoadAsync(CopyFixture("v7-structure"));

        var byName = snapshot.Layers.ToDictionary(l => l.Name);

        // 分组自身无像素、有蒙版
        var folder = byName["Folder"];
        Assert.True(folder.IsGroup);
        Assert.Null(folder.ParentId);
        Assert.Null(folder.ImageFile);
        Assert.NotNull(folder.MaskFile);
        Assert.Equal(1.0f, folder.Opacity, 4);
        Assert.Equal(BlendMode.Normal, folder.BlendMode);

        // 子层挂在 Folder 下；不透明度缺省为 1（manifest 里没写）
        var child = byName["Child"];
        Assert.False(child.IsGroup);
        Assert.Equal(folder.Id, child.ParentId);
        Assert.Equal(1.0f, child.Opacity, 4);
        Assert.Equal(BlendMode.Normal, child.BlendMode);
        Assert.NotNull(child.ImageFile);
        Assert.NotNull(child.MaskFile);

        // 活蒙版链：Clip1 ← Base，Clip2 ← Clip1；Clip2 不透明度 0.8 + Multiply
        var clip1 = byName["Clip1"];
        var clip2 = byName["Clip2"];
        Assert.Equal(byName["Base"].Id, clip1.MaskSourceId);
        Assert.Equal(clip1.Id, clip2.MaskSourceId);
        Assert.Equal(0.8f, clip2.Opacity, 4);
        Assert.Equal(BlendMode.Multiply, clip2.BlendMode);

        // 调整层：既无像素也无蒙版
        var adjust = byName["Adjust"];
        Assert.NotNull(adjust.Adjustment);
        Assert.Equal("Levels", adjust.Adjustment!.Kind);
        Assert.Null(adjust.ImageFile);
        Assert.Null(adjust.MaskFile);
    }

    [Fact]
    public async Task LoadAsync_变换逐字段对上夹具()
    {
        var snapshot = await new ProjectStore().LoadAsync(CopyFixture("v11-full"));
        var baseLayer = snapshot.Layers.Single(l => l.Name == "Base");

        // 夹具 manifest 第 0 层：origin [0,0] / size [100,80] / rotation 0 / 两个翻转 false /
        // sampling "High quality"
        Assert.Equal(0d, baseLayer.Transform.Origin.X, 6);
        Assert.Equal(0d, baseLayer.Transform.Origin.Y, 6);
        Assert.Equal(100, baseLayer.Transform.Size.Width);
        Assert.Equal(80, baseLayer.Transform.Size.Height);
        Assert.Equal(0d, baseLayer.Transform.RotationDegrees, 6);
        Assert.False(baseLayer.Transform.FlipX);
        Assert.False(baseLayer.Transform.FlipY);
        Assert.Equal(SamplingQuality.HighQuality, baseLayer.Transform.Sampling);
    }

    // ------------------------------------------------------------ 像素资产

    [Fact]
    public async Task LoadAsync_图层像素是预乘RGBA且尺寸对上IHDR()
    {
        var snapshot = await new ProjectStore().LoadAsync(CopyFixture("v11-full"));
        var byName = snapshot.Layers.ToDictionary(l => l.Name);

        // 尺寸直接取自夹具 PNG 的 IHDR（100x80 / 60x60 / 40x40 / 80x30），
        // 不是取自本实现的解码结果。
        AssertAsset(snapshot.Images[byName["Base"].Id], 100, 80);
        AssertAsset(snapshot.Images[byName["Child"].Id], 60, 60);
        AssertAsset(snapshot.Images[byName["Shape"].Id], 40, 40);
        AssertAsset(snapshot.Images[LayerById(snapshot, "F4B2C3D4")], 80, 30);
    }

    [Fact]
    public async Task LoadAsync_蒙版一律是Gray8()
    {
        var snapshot = await new ProjectStore().LoadAsync(CopyFixture("v11-full"));
        var byName = snapshot.Layers.ToDictionary(l => l.Name);

        // 夹具里两个 .mask.png 的 IHDR 都是 colorType=0（灰度）、depth=8。
        AssertMask(snapshot.Masks[byName["Child"].Id], 50, 50);
        AssertMask(snapshot.Masks[byName["Folder"].Id], 100, 80);
    }

    [Fact]
    public async Task LoadAsync_蒙版与图层像素是两个独立字典()
    {
        // Mac 的 ProjectStore.swift:160-161 是 images / masks 两个字典；
        // 合成成一个会让「只有蒙版没有像素」的分组（v7 的 Folder）读不出来。
        var snapshot = await new ProjectStore().LoadAsync(CopyFixture("v7-structure"));
        var folder = snapshot.Layers.Single(l => l.Name == "Folder");

        Assert.Contains(folder.Id, snapshot.Masks);
        Assert.DoesNotContain(folder.Id, snapshot.Images);
    }

    // ------------------------------------------------------------ 写回与往返

    [Fact]
    public async Task SaveAsync_Load再Save_manifest字节完全一致()
    {
        // 这是真正的「字节级往返」：自洽的强判据。
        // 第一次 save 会把契约层承载不了的字段（resolution / guides）抹掉，
        // 所以不能拿它跟夹具比；能比的是「第一次 save 的产物」与
        // 「对第一次 save 的产物再 load 再 save 的结果」必须逐字节相同。
        var store = new ProjectStore();
        var first = Path.Combine(_root, "first.comp");
        var second = Path.Combine(_root, "second.comp");

        await store.SaveAsync(await store.LoadAsync(CopyFixture("v11-full")), first);

        var firstManifest = await File.ReadAllBytesAsync(Path.Combine(first, "manifest.json"));

        await store.SaveAsync(await store.LoadAsync(first), second);

        var secondManifest = await File.ReadAllBytesAsync(Path.Combine(second, "manifest.json"));

        Assert.Equal(firstManifest.Length, secondManifest.Length);
        Assert.True(
            firstManifest.AsSpan().SequenceEqual(secondManifest),
            $"manifest 字节不一致：{firstManifest.Length} vs {secondManifest.Length} 字节");
    }

    [Fact]
    public async Task SaveAsync_分组层级原样保留在manifest里()
    {
        // ⚠️ 这一条是「字节级定点往返」抓不到的。
        // 定点往返只证明「写两次结果一致」，而 manifest → snapshot 的映射一旦漏字段
        // （本轮就漏了 ParentId），两次写出来的结果会**一致地错**。
        // 必须回到 manifest 原文去断言层级本身还在。
        var store = new ProjectStore();
        var path = Path.Combine(_root, "tree.comp");
        await store.SaveAsync(await store.LoadAsync(CopyFixture("v7-structure")), path);

        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(path, "manifest.json")))!.AsObject();

        var layers = manifest["layers"]!.AsArray();
        var byName = layers.ToDictionary(
            l => l!["name"]!.GetValue<string>(),
            l => l!.AsObject());

        var folderId = byName["Folder"]["id"]!.GetValue<string>();
        var child = byName["Child"];

        // 大写 UUID，与 Swift 的 uuidString 一致
        Assert.Equal(folderId, child["parentID"]!.GetValue<string>());
        Assert.Equal(folderId, folderId.ToUpperInvariant());

        // 分组自身不带像素
        Assert.Null(byName["Folder"]["imageFile"]);

        // 活蒙版链也在
        Assert.Equal(
            byName["Base"]["id"]!.GetValue<string>(),
            byName["Clip1"]["maskSourceID"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAsync_调整层与蒙版文件名一并保留()
    {
        var store = new ProjectStore();
        var path = Path.Combine(_root, "adjust.comp");
        await store.SaveAsync(await store.LoadAsync(CopyFixture("v7-structure")), path);

        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(path, "manifest.json")))!.AsObject();

        var adjust = manifest["layers"]!.AsArray()
            .Single(l => l!["name"]!.GetValue<string>() == "Adjust")!.AsObject();

        Assert.Equal("Levels", adjust["adjustment"]!["kind"]!.GetValue<string>());
        Assert.Null(adjust["imageFile"]);
        Assert.Null(adjust["maskFile"]);

        var folder = manifest["layers"]!.AsArray()
            .Single(l => l!["name"]!.GetValue<string>() == "Folder")!.AsObject();

        var folderId = folder["id"]!.GetValue<string>();
        Assert.Equal(folderId + ".mask.png", folder["maskFile"]!.GetValue<string>());
    }

    [Fact]
    public async Task SaveAsync_manifest里不出现小写UUID()
    {
        // 🔴 互毁级回归闸门。
        // Swift 的 uuidString 大写、.NET 的 Guid.ToString() 小写；一旦本层改回默认行为，
        // 同一份 manifest 里会出现「id 小写、imageFile 大写」的自相矛盾，
        // 两个平台产出的字节也永远对不上。这里从产物文本上直接扫。
        var store = new ProjectStore();
        var path = Path.Combine(_root, "case-check.comp");
        await store.SaveAsync(await store.LoadAsync(CopyFixture("v11-full")), path);

        var text = File.ReadAllText(Path.Combine(path, "manifest.json"));

        var lower = new Regex(
            @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            RegexOptions.CultureInvariant);

        Assert.Empty(lower.Matches(text).Select(m => m.Value).Distinct());

        // 同时确认确实写了 UUID（否则上面的空断言毫无意义）
        var upper = new Regex(
            @"[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}",
            RegexOptions.CultureInvariant);

        Assert.NotEmpty(upper.Matches(text));
    }

    [Fact]
    public async Task SaveAsync_资源文件名是大写UUID()
    {
        // 🔴 互毁级：Swift 的 UUID.uuidString 是大写，.NET 的 Guid.ToString() 是小写。
        // Mac 在 ProjectStore.swift:223,243 用前者校验文件名，写成小写会被整个拒收。
        var store = new ProjectStore();
        var path = Path.Combine(_root, "case.comp");
        await store.SaveAsync(await store.LoadAsync(CopyFixture("v11-full")), path);

        var names = Directory.GetFiles(Path.Combine(path, "images"))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

        Assert.NotEmpty(names);

        foreach (var name in names)
        {
            // UUID 段（固定 36 个字符）必须是大写 —— Swift 的 UUID.uuidString 就是大写。
            // 扩展名必须是小写 .png（ProjectStore.swift:98 硬编码 UTType.png.identifier）。
            // 中间的 ".mask" 段同样是小写。
            Assert.True(name.Length >= 40, name);
            Assert.Equal(name[..36], name[..36].ToUpperInvariant());
            Assert.Equal(".png", name[^4..]);
            if (name.Contains(".mask.", StringComparison.Ordinal))
            {
                Assert.Equal(".mask", name[36..41]);
            }
            else
            {
                Assert.Equal(".png", name[36..]);
            }

            Assert.True(Guid.TryParse(name.AsSpan(0, 36), out _));
        }
    }

    [Fact]
    public async Task SaveAsync_包结构与Mac一致()
    {
        // ProjectStore.swift:112-121：根下是 manifest.json + images/。
        var store = new ProjectStore();
        var path = Path.Combine(_root, "layout.comp");
        await store.SaveAsync(await store.LoadAsync(CopyFixture("v11-full")), path);

        Assert.True(File.Exists(Path.Combine(path, "manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(path, "images")));
    }

    [Fact]
    public async Task SaveAsync_重复保存不叠加出多余目录()
    {
        var store = new ProjectStore();
        var path = Path.Combine(_root, "again.comp");

        await store.SaveAsync(await store.LoadAsync(CopyFixture("v11-full")), path);
        await store.SaveAsync(await store.LoadAsync(path), path);

        // 暂存/备份目录必须被清理干净，否则每保存一次就在旁边留一份完整工程。
        var siblings = Directory.GetDirectories(_root)
            .Select(Path.GetFileName)
            .Where(n => n!.Contains("staging", StringComparison.Ordinal)
                     || n.Contains("backup", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(siblings);
    }

    // ------------------------------------------------------------ 存储层反例

    [Fact]
    public async Task LoadAsync_包不存在时报invalid()
    {
        var ex = await Assert.ThrowsAsync<ProjectException>(
            () => new ProjectStore().LoadAsync(Path.Combine(_root, "nope.comp")));

        Assert.Equal(ProjectErrorKind.Invalid, ex.Kind);
    }

    [Fact]
    public async Task LoadAsync_声明了像素但文件缺失时报missingImage()
    {
        var path = CopyFixture("v1-minimal");
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(path);

        var image = snapshot.Images.Keys.Single();
        File.Delete(Path.Combine(path, "images", ProjectManifest.ImageFileName(image)));

        var ex = await Assert.ThrowsAsync<ProjectException>(() => store.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.MissingImage, ex.Kind);
    }

    [Fact]
    public async Task LoadAsync_资源不是PNG时报missingImage()
    {
        var path = CopyFixture("v1-minimal");
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(path);

        var image = snapshot.Images.Keys.Single();
        await File.WriteAllBytesAsync(
            Path.Combine(path, "images", ProjectManifest.ImageFileName(image)),
            "这不是 PNG，这是一段纯文本。"u8.ToArray());

        var ex = await Assert.ThrowsAsync<ProjectException>(() => store.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.MissingImage, ex.Kind);
    }

    [Fact]
    public async Task LoadAsync_资源被截断时报missingImage()
    {
        var path = CopyFixture("v1-minimal");
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(path);

        var image = snapshot.Images.Keys.Single();
        var file = Path.Combine(path, "images", ProjectManifest.ImageFileName(image));
        var bytes = await File.ReadAllBytesAsync(file);
        await File.WriteAllBytesAsync(file, bytes[..(bytes.Length / 2)]);

        var ex = await Assert.ThrowsAsync<ProjectException>(() => store.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.MissingImage, ex.Kind);
    }

    [Fact]
    public async Task LoadAsync_把RGBA当蒙版会被拒()
    {
        // 这是 Mac 的 LayerMask.swift:23-26 守卫在存储层的落点。
        // 放行的后果不是「读错」，而是「Mac 拒收」，所以必须拒。
        var path = CopyFixture("v11-full");
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(path);

        var withMask = snapshot.Layers.First(l => snapshot.Masks.ContainsKey(l.Id) && snapshot.Images.ContainsKey(l.Id));
        var maskFile = Path.Combine(path, "images", ProjectManifest.MaskFileName(withMask.Id));

        // 用一张 RGBA 图冒充蒙版：先把图层像素复制过去
        var layerPng = Path.Combine(path, "images", ProjectManifest.ImageFileName(withMask.Id));
        File.Copy(layerPng, maskFile, overwrite: true);

        var ex = await Assert.ThrowsAsync<ProjectException>(() => store.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.Invalid, ex.Kind);
    }

    [Fact]
    public async Task LoadAsync_把灰度当图层像素不会被拒()
    {
        // 反向：Mac 对**图层像素**没有任何 alpha / 颜色类型守卫
        // （ProjectStore.swift:168-190 只对 isMask 分支查 LayerMask.isValid）。
        // 曾经在这里多加过一条「图层必须有 alpha」的守卫，已删——它会让 Windows
        // 拒收 Mac 打得开的工程。
        var path = CopyFixture("v11-full");
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(path);

        var masked = snapshot.Layers.First(l => snapshot.Masks.ContainsKey(l.Id) && snapshot.Images.ContainsKey(l.Id));
        var maskFile = Path.Combine(path, "images", ProjectManifest.MaskFileName(masked.Id));
        var layerFile = Path.Combine(path, "images", ProjectManifest.ImageFileName(masked.Id));

        File.Copy(maskFile, layerFile, overwrite: true);

        var reloaded = await store.LoadAsync(path);

        // 灰度图被展开成不透明的 RGBA：宽高不变，alpha 恒 255。
        var asset = reloaded.Images[masked.Id];
        Assert.Equal(ProjectPixelFormat.PremultipliedRgba8, asset.Format);
        Assert.True(asset.Pixels.ToArray().Where((_, i) => i % 4 == 3).All(a => a == 255));
    }

    [Fact]
    public async Task LoadAsync_IHDR声明16位时拒收()
    {
        // ProjectStore.swift:179 的 (depth ?? 8) <= 8。
        // 只验「被拒收」这个结果；判定机制由 PngHeaderTests 单独证明。
        var path = CopyFixture("v1-minimal");
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(path);

        var image = snapshot.Images.Keys.Single();
        var file = Path.Combine(path, "images", ProjectManifest.ImageFileName(image));
        var bytes = await File.ReadAllBytesAsync(file);

        PatchIhdr(bytes, depth: 16, colorType: 6);
        await File.WriteAllBytesAsync(file, bytes);

        await Assert.ThrowsAsync<ProjectException>(() => store.LoadAsync(path));
    }

    [Fact]
    public async Task LoadAsync_APNG拒收()
    {
        // ProjectStore.swift:175 的 CGImageSourceGetCount(source) == 1。
        var path = CopyFixture("v1-minimal");
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(path);

        var image = snapshot.Images.Keys.Single();
        var file = Path.Combine(path, "images", ProjectManifest.ImageFileName(image));
        var bytes = await File.ReadAllBytesAsync(file);

        await File.WriteAllBytesAsync(file, WithChunkBeforeIdat(bytes, "acTL", [0, 0, 0, 1, 0, 0, 0, 1]));

        await Assert.ThrowsAsync<ProjectException>(() => store.LoadAsync(path));
    }

    // ------------------------------------------------------------ 保存侧守卫

    [Fact]
    public async Task SaveAsync_蒙版不是Gray8时拒收()
    {
        // 必须**存之前**拒。Mac 是在 ProjectStore.swift:93 存完才知道，
        // 那样会产出一个「现在能打开、发给同事打不开」的包。
        var store = new ProjectStore();
        var snapshot = await store.LoadAsync(CopyFixture("v1-minimal"));
        var id = snapshot.Images.Keys.Single();

        var broken = snapshot with
        {
            Masks = new Dictionary<Guid, IProjectAsset>
            {
                [id] = ProjectAsset.Create(4, 4, ProjectPixelFormat.PremultipliedRgba8, new byte[4 * 4 * 4]),
            },
        };

        // 快照本身与 manifest 不一致（Layers[].MaskFile 为 null），所以先补上。
        var layers = snapshot.Layers
            .Select(l => l.Id == id ? l with { MaskFile = ProjectManifest.MaskFileName(id) } : l)
            .ToArray();

        var ex = await Assert.ThrowsAsync<ProjectException>(
            () => store.SaveAsync(broken with { Layers = layers }, Path.Combine(_root, "bad-mask.comp")));

        Assert.Equal(ProjectErrorKind.Invalid, ex.Kind);
    }

    [Fact]
    public async Task SaveAsync_资产字典才是唯一真相_LayerNode里的文件名被忽略()
    {
        // 这一条替代了原先那个「声明了文件却没给像素」的用例——
        // 后者的前提在本 API 下根本不成立：保存时 manifest 的 imageFile 是
        // **从 Images 字典的键推出来的**，不是从 LayerNode.ImageFile 抄的。
        // 所以「LayerNode 说有文件、字典里却没有」这种状态永远构造不出来。
        // 真正值得钉住的是反过来的规则：清空字典 → manifest 里就不该再有 imageFile。
        var store = new ProjectStore();
        var path = Path.Combine(_root, "derived.comp");
        var snapshot = await store.LoadAsync(CopyFixture("v1-minimal"));

        await store.SaveAsync(snapshot with { Images = new Dictionary<Guid, IProjectAsset>() }, path);

        var manifest = JsonNode.Parse(
            File.ReadAllText(Path.Combine(path, "manifest.json")))!.AsObject();

        Assert.Null(manifest["layers"]![0]!["imageFile"]);
        Assert.Empty(Directory.GetFiles(Path.Combine(path, "images")));
    }

    [Fact]
    public async Task SaveAsync_BMP非ASCII按UTF8原样写出()
    {
        // 中文图层名必须直接以 UTF-8 写进 manifest，不能变成六位十六进制转义序列。
        // 这是 UnsafeRelaxedJsonEscaping 的核心用途，也是字节级往返能收敛的前提。
        var store = new ProjectStore();
        var path = Path.Combine(_root, "chinese.comp");

        var snapshot = await store.LoadAsync(CopyFixture("v1-minimal"));
        var renamed = snapshot with
        {
            Layers = snapshot.Layers
                .Select(l => l with { Name = "最上层" })
                .ToArray(),
        };

        await store.SaveAsync(renamed, path);

        var text = File.ReadAllText(Path.Combine(path, "manifest.json"));

        Assert.Contains("最上层", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_BMP外字符会被转义但读回来不变()
    {
        // ⚠️ 平台限制，如实记录而不是假装没有：
        // System.Text.Json 即使配了 UnsafeRelaxedJsonEscaping，**BMP 外字符**
        // （即需要代理对的那一类，如 U+1F324）仍然会写成 D83C/DF24 这样的转义形式。
        // 这是编码器实现的行为，不是本层的配置问题。
        //
        // 影响范围要说清楚：这只是**字节**不同，字符串值完全相同，
        // Mac 的 JSONDecoder 读回的是同一个图层名，所以不影响互通；
        // 受影响的只有「两个平台产出的 manifest 逐字节相同」这一条期望。
        var store = new ProjectStore();
        var path = Path.Combine(_root, "astral.comp");

        var snapshot = await store.LoadAsync(CopyFixture("v1-minimal"));
        const string Astral = "Title \U0001F324";
        var renamed = snapshot with
        {
            Layers = snapshot.Layers.Select(l => l with { Name = Astral }).ToArray(),
        };

        await store.SaveAsync(renamed, path);

        var text = File.ReadAllText(Path.Combine(path, "manifest.json"));
        Assert.Contains("\\uD83C\\uDF24", text, StringComparison.OrdinalIgnoreCase);

        // 语义必须无损
        var reloaded = await store.LoadAsync(path);
        Assert.Equal(Astral, reloaded.Layers[0].Name);
    }

    [Fact]
    public async Task SaveAsync_失败时原包不受影响()
    {
        var store = new ProjectStore();
        var path = Path.Combine(_root, "survivor.comp");
        var original = await store.LoadAsync(CopyFixture("v11-full"));
        await store.SaveAsync(original, path);

        var before = await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json"));

        // 造一个一定会在写盘前被拒的快照：给蒙版塞一张 RGBA。
        var masked = original.Layers.First(l => original.Masks.ContainsKey(l.Id));
        var brokenMasks = new Dictionary<Guid, IProjectAsset>(original.Masks)
        {
            [masked.Id] = ProjectAsset.Create(2, 2, ProjectPixelFormat.PremultipliedRgba8, new byte[2 * 2 * 4]),
        };

        await Assert.ThrowsAsync<ProjectException>(
            () => store.SaveAsync(original with { Masks = brokenMasks }, path));

        // 保存失败后原包必须还在、且一个字节都没变。
        // （不能写成 before.AsSpan().SequenceEqual(await ...) —— Span 跨 await 不保留。）
        var after = await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json"));

        Assert.True(File.Exists(Path.Combine(path, "manifest.json")));
        Assert.Equal(before.Length, after.Length);
        Assert.True(before.AsSpan().SequenceEqual(after));
    }

    // ------------------------------------------------------------ 有损提示

    [Fact]
    public async Task LoadAsync_契约承载不了的字段会在读取时就报警()
    {
        // v11 夹具带 2 条参考线 + resolution=72，契约 v1.2 的 ProjectSnapshot 两者都没有。
        // 必须在 load 时报，而不是等 save 覆盖完才报。
        var warnings = new List<string>();
        var previous = ProjectJson.WarningSink;
        ProjectJson.WarningSink = warnings.Add;

        try
        {
            await new ProjectStore().LoadAsync(CopyFixture("v11-full"));
        }
        finally
        {
            ProjectJson.WarningSink = previous;
        }

        Assert.Contains(warnings, w => w.Contains("参考线", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("分辨率", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_v1夹具只报分辨率_不报参考线与形状特效()
    {
        // v1 夹具带 resolution=72（契约承载不了）但没有参考线 / 形状 / 特效。
        // 断言要精确到「只报该报的」，而不是笼统地数条数——
        // 否则多报一条参考线也不会被发现。
        var warnings = new List<string>();
        var previous = ProjectJson.WarningSink;
        ProjectJson.WarningSink = warnings.Add;

        try
        {
            await new ProjectStore().LoadAsync(CopyFixture("v1-minimal"));
        }
        finally
        {
            ProjectJson.WarningSink = previous;
        }

        Assert.Contains(warnings, w => w.Contains("分辨率", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("参考线", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("形状", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ 契约形状

    [Fact]
    public void ProjectStore_实现了IProjectStore契约()
    {
        Assert.IsAssignableFrom<IProjectStore>(new ProjectStore());
    }

    // ------------------------------------------------------------ 辅助

    /// <summary>按 id 前 8 位取图层。用于那些名字含非 ASCII 的图层。</summary>
    private static Guid LayerById(ProjectSnapshot snapshot, string prefix)
        => snapshot.Layers.Single(l => l.Id.ToString("D").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Id;

    private static void AssertAsset(IProjectAsset asset, int width, int height)    {
        Assert.Equal(width, asset.Width);
        Assert.Equal(height, asset.Height);
        Assert.Equal(ProjectPixelFormat.PremultipliedRgba8, asset.Format);
        Assert.Equal(width * height * 4, asset.Pixels.Length);
    }

    private static void AssertMask(IProjectAsset asset, int width, int height)
    {
        Assert.Equal(width, asset.Width);
        Assert.Equal(height, asset.Height);
        Assert.Equal(ProjectPixelFormat.Gray8, asset.Format);
        Assert.Equal(width * height, asset.Pixels.Length);
    }

    /// <summary>
    /// 就地改写 IHDR 的位深与颜色类型，并重算该块的 CRC。
    /// </summary>
    /// <remarks>
    /// 重算 CRC 是为了让补丁后的文件在结构上仍然合法——
    /// 否则「被拒收」可能只是 CRC 校验失败，证不到位深守卫。
    /// </remarks>
    private static void PatchIhdr(byte[] png, byte depth, byte colorType)
    {
        png[24] = depth;
        png[25] = colorType;

        var crc = Crc32(png.AsSpan(12, 17)); // "IHDR" + 13 字节数据
        BitConverter.TryWriteBytes(png.AsSpan(29, 4), (uint)crc);
    }

    /// <summary>
    /// 造一个「第一个 IDAT 之前插入了 <paramref name="type"/> 块」的新 PNG 字节数组。
    /// </summary>
    /// <remarks>
    /// 返回新数组而不是就地改：<c>Span&lt;T&gt;</c> 没有 <c>Insert</c>，
    /// 而这些字节要交给 <c>WriteAllBytesAsync</c>，本来就只需要一个数组。
    /// </remarks>
    private static byte[] WithChunkBeforeIdat(byte[] png, string type, byte[] data)
    {
        var offset = 8;
        while (offset + 8 <= png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var name = Encoding.ASCII.GetString(png, offset + 4, 4);

            if (name == "IDAT")
            {
                break;
            }

            offset += length + 12;
        }

        var chunk = new byte[12 + data.Length];
        BitConverter.TryWriteBytes(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BitConverter.TryWriteBytes(chunk.AsSpan(8 + data.Length, 4), (uint)Crc32(chunk.AsSpan(4, 4 + data.Length)));

        var result = new byte[png.Length + chunk.Length];
        png.AsSpan(0, offset).CopyTo(result);
        chunk.CopyTo(result.AsSpan(offset));
        png.AsSpan(offset).CopyTo(result.AsSpan(offset + chunk.Length));
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>把 manifest JSON 反序列化为可编辑节点，供反例用例改字段。</summary>
    private static JsonNode LoadManifest(string packagePath)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(packagePath, "manifest.json")))!;

    /// <summary>断言某个 manifest 改动会被哪一类错误拒绝。</summary>
    private async Task AssertRejected(string fixture, Action<JsonObject> mutate, ProjectErrorKind expected)
    {
        var path = CopyFixture(fixture);
        var node = (JsonObject)LoadManifest(path);
        mutate(node);
        File.WriteAllText(Path.Combine(path, "manifest.json"), node.ToJsonString());

        var ex = await Assert.ThrowsAsync<ProjectException>(() => new ProjectStore().LoadAsync(path));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public Task manifest格式标识错_拒() => AssertRejected(
        "v1-minimal", m => m["format"] = "com.other.thing", ProjectErrorKind.Invalid);

    [Fact]
    public Task manifest版本超出支持范围_报version类别() => AssertRejected(
        "v11-full", m => m["version"] = 12, ProjectErrorKind.Version);

    [Fact]
    public Task manifest色彩空间非sRGB_拒() => AssertRejected(
        "v1-minimal", m => m["colorSpace"] = "Display P3", ProjectErrorKind.Invalid);

    [Fact]
    public Task 图层像素文件名改成小写UUID_拒()
    {
        // 互毁级陷阱在存储层的复现：如果哪天有人图省事改成 Guid.ToString().ToLower()，
        // 这里会立刻红。
        return AssertRejected(
            "v1-minimal",
            m =>
            {
                var layer = (JsonObject)m["layers"]![0]!;
                layer["imageFile"] = "6f1d3c2a-0b7e-4e8a-9c4d-2a1b3c4d5e6f.png";
            },
            ProjectErrorKind.Invalid);
    }

    [Fact]
    public Task 蒙版文件名不符_拒() => AssertRejected(
        "v7-structure",
        m =>
        {
            var layer = (JsonObject)m["layers"]![3]!;
            layer["maskFile"] = "D1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D.png";
        },
        ProjectErrorKind.Invalid);

    [Fact]
    public Task 变换尺寸超上限_拒_不可委托给Core的IsValid() => AssertRejected(
        "v1-minimal",
        m =>
        {
            var layer = (JsonObject)m["layers"]![0]!;
            layer["transform"] = new JsonObject
            {
                ["origin"] = new JsonArray(0, 0),
                ["size"] = new JsonArray(300_001, 80),
                ["rotation"] = 0,
                ["flipX"] = false,
                ["flipY"] = false,
                ["sampling"] = "High quality",
            };
        },
        ProjectErrorKind.Invalid);
}
