using Compositor.Core;
using Xunit;

namespace Compositor.Project.Tests;

/// <summary>
/// 图层树与活蒙版链的结构校验。逐条对应
/// <c>Document/LayerGroups.swift:30-46</c> 与 <c>Document/LiveLayerMask.swift:4-19</c>。
/// </summary>
public sealed class LayerGraphValidatorTests
{
    private static ProjectLayerRecord Layer(string name, bool? group = null, Guid? parent = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsVisible = true,
        IsGroup = group,
        ParentId = parent,
        ImageFile = group == true ? null : $"{Guid.NewGuid()}.png",
        Transform = new LayerTransform { Size = new DocSize(10, 10) },
    };

    [Fact]
    public void 图层id重复_拒()
    {
        var id = Guid.NewGuid();
        var a = Layer("A") with { Id = id };
        var b = Layer("B") with { Id = id };

        Assert.Throws<ProjectException>(() => LayerHierarchyValidator.Validate(new[] { a, b }));
    }

    [Fact]
    public void 分组带像素_拒()
    {
        var group = Layer("组", group: true) with { ImageFile = "x.png" };
        Assert.Throws<ProjectException>(() => LayerHierarchyValidator.Validate(new[] { group }));
    }

    [Fact]
    public void 父子成环_拒()
    {
        var g1 = Layer("组1", group: true);
        var g2 = Layer("组2", group: true, parent: g1.Id);
        var g1Closed = g1 with { ParentId = g2.Id };

        var e = Assert.Throws<ProjectException>(
            () => LayerHierarchyValidator.Validate(new[] { g1Closed, g2 }));
        Assert.Contains("cycle", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 父不存在_拒()
    {
        var orphan = Layer("孤儿", parent: Guid.NewGuid());
        Assert.Throws<ProjectException>(() => LayerHierarchyValidator.Validate(new[] { orphan }));
    }

    [Fact]
    public void 父不是分组_拒()
    {
        var parent = Layer("普通层");
        var child = Layer("子", parent: parent.Id);

        var e = Assert.Throws<ProjectException>(
            () => LayerHierarchyValidator.Validate(new[] { parent, child }));
        Assert.Contains("bad parent", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 正常三层嵌套_放行()
    {
        var root = Layer("根组", group: true);
        var mid = Layer("中组", group: true, parent: root.Id);
        var leaf = Layer("叶", parent: mid.Id);

        LayerHierarchyValidator.Validate(new[] { root, mid, leaf });
    }

    [Fact]
    public void 嵌套超64层_拒()
    {
        var layers = new System.Collections.Generic.List<ProjectLayerRecord>();
        Guid? parent = null;

        for (var i = 0; i < 66; i++)
        {
            var group = Layer($"组{i}", group: true, parent: parent);
            layers.Add(group);
            parent = group.Id;
        }

        var leaf = Layer("叶", parent: parent);
        layers.Add(leaf);

        var e = Assert.Throws<ProjectException>(() => LayerHierarchyValidator.Validate(layers));
        // :44 的 isGroup 分支先触发，报 group too deep；两条都是同一道 64 层闸门
        Assert.True(e.Detail!.Contains("deep", StringComparison.Ordinal), e.Detail);
    }

    // ── 活蒙版链 ─────────────────────────────────────────────────

    [Fact]
    public void 活蒙版id重复_拒()
    {
        var id = Guid.NewGuid();
        var a = Layer("A") with { Id = id };
        var b = Layer("B") with { Id = id };

        Assert.Throws<ProjectException>(() => LiveMaskValidator.Validate(new[] { a, b }));
    }

    [Fact]
    public void 活蒙版链成环_拒()
    {
        var a = Layer("A");
        var b = Layer("B");
        var aClosed = a with { MaskSourceId = b.Id };
        var bClosed = b with { MaskSourceId = a.Id };

        var e = Assert.Throws<ProjectException>(() => LiveMaskValidator.Validate(new[] { aClosed, bClosed }));
        Assert.Contains("cycle", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 活蒙版来源不存在_拒()
    {
        var a = Layer("A") with { MaskSourceId = Guid.NewGuid() };
        var e = Assert.Throws<ProjectException>(() => LiveMaskValidator.Validate(new[] { a }));
        // :14 的「来源必须存在」先于遍历到它触发（与 Mac 同序）
        Assert.Contains("bad source", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 活蒙版来源是分组_拒()
    {
        var source = Layer("组", group: true);
        var a = Layer("A") with { MaskSourceId = source.Id };

        var e = Assert.Throws<ProjectException>(() => LiveMaskValidator.Validate(new[] { source, a }));
        Assert.Contains("bad source", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 活蒙版来源是调整层_拒()
    {
        var source = Layer("调整") with
        {
            ImageFile = null,
            Adjustment = new LayerAdjustmentRecord { Kind = "Levels", Settings = new() },
        };

        var a = Layer("A") with { MaskSourceId = source.Id };

        var e = Assert.Throws<ProjectException>(() => LiveMaskValidator.Validate(new[] { source, a }));
        Assert.Contains("bad source", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 活蒙版链长超256_拒()
    {
        var layers = new System.Collections.Generic.List<ProjectLayerRecord>();
        Guid? previous = null;

        // 前 260 个串成一条链（每个的来源是上一个）
        for (var i = 0; i < 260; i++)
        {
            layers.Add(Layer($"L{i}") with { MaskSourceId = previous });
            previous = layers[^1].Id;
        }

        var e = Assert.Throws<ProjectException>(() => LiveMaskValidator.Validate(layers));
        Assert.Contains("cycle or length", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 活蒙版正常单层来源_放行()
    {
        var source = Layer("源");
        var a = Layer("A") with { MaskSourceId = source.Id };

        LiveMaskValidator.Validate(new[] { source, a });
    }
}

/// <summary>
/// 路径安全、像素预算与摘要。
/// </summary>
public sealed class PackageLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "compositor-tests", Guid.NewGuid().ToString("N"));

    public PackageLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不应让测试失败。
        }
    }

    [Fact]
    public void 路径逃出包根_拒()
    {
        var e = Assert.Throws<ProjectException>(
            () => PackageLayout.ResolveInside(_root, "../evil.png"));
        Assert.Equal(ProjectErrorKind.Invalid, e.Kind);
        Assert.Contains("escapes", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void 多级回退逃出包根_拒()
    {
        Assert.Throws<ProjectException>(
            () => PackageLayout.ResolveInside(_root, "images/../../evil.png"));
    }

    [Fact]
    public void 包名前缀混淆_不能被当成在包内()
    {
        // 经典漏洞：pkg 与 pkg-evil 的字符串前缀是相交的。
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var file = Path.Combine(sibling, "x.png");
            File.WriteAllText(file, "x");

            var e = Assert.Throws<ProjectException>(() => PackageLayout.CheckFile(file, _root, 1024));
            Assert.Contains("escapes", e.Detail!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void 包内正常路径_放行()
    {
        var images = Path.Combine(_root, "images");
        Directory.CreateDirectory(images);
        var file = Path.Combine(images, "a.png");
        File.WriteAllText(file, "hello");

        PackageLayout.CheckFile(file, _root, 1024);
    }

    [Fact]
    public void 超过体积上限_报tooLarge()
    {
        var file = Path.Combine(_root, "big.bin");
        File.WriteAllText(file, new string('x', 2048));

        var e = Assert.Throws<ProjectException>(() => PackageLayout.CheckFile(file, _root, 1024));
        Assert.Equal(ProjectErrorKind.TooLarge, e.Kind);
    }

    [Fact]
    public void 目录冒充文件_拒()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir.png"));
        var e = Assert.Throws<ProjectException>(
            () => PackageLayout.CheckFile(Path.Combine(_root, "dir.png"), _root, 1024));
        Assert.Contains("not a regular file", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows路径大小写不敏感_不应误判为逃逸()
    {
        var images = Path.Combine(_root, "Images");
        Directory.CreateDirectory(images);

        // 用不同大小写指向同一个目录，必须仍然判定为包内。
        var resolved = PackageLayout.ResolveInside(_root, "IMAGES/./a.PNG");
        Assert.StartsWith(images, resolved, StringComparison.OrdinalIgnoreCase);
    }

    // ── SizeBudget ───────────────────────────────────────────────

    [Fact]
    public void 预算累加_超过上限报tooLarge()
    {
        var budget = new SizeBudget(limit: 1000);
        budget.Add(30, 30); // 900

        var e = Assert.Throws<ProjectException>(() => budget.Add(30, 30));
        Assert.Equal(ProjectErrorKind.TooLarge, e.Kind);
        Assert.Equal(900, budget.Used);
    }

    [Fact]
    public void 预算_宽高越界报tooLarge()
    {
        var budget = new SizeBudget();
        Assert.Throws<ProjectException>(() => budget.Add(0, 10));
        Assert.Throws<ProjectException>(() => budget.Add(30_001, 10));
    }

    [Fact]
    public void 预算_八百万像素上限恒定()
    {
        // Mac 的 checkSize 用的是 documentPixelBudget；YG 2026-10-07 裁决固定 800 MP。
        Assert.Equal(800_000_000, Imaging.ImagingLimits.DocumentPixelBudget);
        Assert.Equal(800_000_000, new SizeBudget().Limit);
    }

    // ── ProjectDigest ────────────────────────────────────────────

    [Fact]
    public void 摘要_相同内容相同结果()
    {
        var a = BuildPackage(out _);
        var b = BuildPackage(out _);

        Assert.Equal(
            ProjectDigest.ToHex(ProjectDigest.Compute(a)),
            ProjectDigest.ToHex(ProjectDigest.Compute(b)));
    }

    [Fact]
    public void 摘要_资源内容变了大小没变则相同()
    {
        // 这是设计意图（ProjectDigest.swift:8-10）：只取名字与大小，
        // 避免每次保存都逐张哈希把保存卡住。像素变了通常也会改大小。
        var root = BuildPackage(out var images);
        var before = ProjectDigest.ToHex(ProjectDigest.Compute(root));

        var target = Path.Combine(images, "a.png");
        var bytes = File.ReadAllBytes(target);
        bytes[0] ^= 0xFF;  // 同长度改内容
        File.WriteAllBytes(target, bytes);

        Assert.Equal(before, ProjectDigest.ToHex(ProjectDigest.Compute(root)));
    }

    [Fact]
    public void 摘要_manifest变了则不同()
    {
        var root = BuildPackage(out _);
        var before = ProjectDigest.ToHex(ProjectDigest.Compute(root));

        var manifest = Path.Combine(root, "manifest.json");
        File.AppendAllText(manifest, "\n");

        Assert.NotEqual(before, ProjectDigest.ToHex(ProjectDigest.Compute(root)));
    }

    [Fact]
    public void 摘要_资源顺序不影响结果()
    {
        // Mac 的 contentsOfDirectory 顺序不定，所以排序是必需的（ProjectDigest.swift:22）。
        var root = BuildPackage(out var images);
        var before = ProjectDigest.ToHex(ProjectDigest.Compute(root));

        var shuffled = new[]
        {
            Path.Combine(images, "c.png"),
            Path.Combine(images, "a.png"),
            Path.Combine(images, "b.png"),
        };

        foreach (var f in shuffled)
        {
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }

        // 删了 c/a/b 会改摘要，这里只验证"同样内容但磁盘枚举顺序不同"仍一致：
        // 重新建一个内容相同、创建顺序不同的包。
        var other = BuildPackage(out _, creationOrderReversed: true);
        Assert.Equal(before, ProjectDigest.ToHex(ProjectDigest.Compute(other)));
    }

    private string BuildPackage(out string imagesDir, bool creationOrderReversed = false)
    {
        imagesDir = Path.Combine(_root, Guid.NewGuid().ToString("N"), "images");
        Directory.CreateDirectory(imagesDir);

        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(imagesDir)!, "manifest.json"),
            "{\"format\":\"com.compositor.project\",\"version\":11}");

        var names = creationOrderReversed
            ? new[] { "c.png", "b.png", "a.png" }
            : new[] { "a.png", "b.png", "c.png" };

        foreach (var name in names)
        {
            // 内容与顺序无关地固定：a=1字节, b=2字节, c=3字节
            var length = name[0] - 'a' + 1;
            File.WriteAllBytes(Path.Combine(imagesDir, name), new byte[length]);
        }

        return Path.GetDirectoryName(imagesDir)!;
    }
}