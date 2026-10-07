using Compositor.Core;
using Xunit;

namespace Compositor.Project.Tests;

/// <summary>
/// <c>ProjectValidator</c> 的 guard 测试。逐条对应 macOS 版
/// <c>ProjectStore.swift:198-262</c>。
/// </summary>
/// <remarks>
/// <b>期望值纪律：</b>阈值与错误类别全部抄自 Mac 源码常量
/// （<c>maxSide</c>=30000、图层 10000、参考线 1000、名字 16384 字节、
/// 变换边长 300000、原点 ±1000000、resolution 1…9600）。
/// 测试构造的"坏数据"用例会标明它违反的是哪条 guard 的 Mac 行号。
/// </remarks>
public sealed class ProjectValidatorTests
{
    private static readonly Guid LayerId = new("11111111-2222-4333-8444-555555555555");
    private static readonly Guid DocId = new("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

    private static ProjectLayerRecord ValidLayer() => new()
    {
        Id = LayerId,
        Name = "图层 1",
        IsVisible = true,
        ImageFile = ProjectManifest.ImageFileName(LayerId),
        Transform = new LayerTransform
        {
            Origin = new DocPoint(0, 0),
            Size = new DocSize(100, 80),
            RotationDegrees = 0,
            Sampling = SamplingQuality.HighQuality,
        },
    };

    private static ProjectManifest Valid(ProjectLayerRecord? layer = null) => new()
    {
        DocumentId = DocId,
        Width = 1000,
        Height = 800,
        Layers = new[] { layer ?? ValidLayer() },
    };

    // ── G01 :199 格式标识 ─────────────────────────────────────────
    [Fact]
    public void G01_格式标识错_拒()
    {
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid() with { Format = "com.other" }));
        Assert.Equal(ProjectErrorKind.Invalid, e.Kind);
    }

    // ── G02 :200 版本范围 ─────────────────────────────────────────
    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(-1)]
    public void G02_版本越界_报version类别(int version)
    {
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid() with { Version = version }));
        Assert.Equal(ProjectErrorKind.Version, e.Kind);
        Assert.Equal(version, e.Version);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void G02_边界版本放行(int version)
    {
        var manifest = Valid() with { Version = version };
        // v1 不能带 imageFile 以外的新东西；这里只测版本本身
        if (version == 1)
        {
            manifest = manifest with { Layers = new[] { ValidLayer() with { ImageFile = null, IsVisible = true } } };
        }

        ProjectValidator.Validate(manifest);
    }

    // ── G03 :201 色彩空间 ─────────────────────────────────────────
    [Fact]
    public void G03_色彩空间非sRGB_拒()
    {
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid() with { ColorSpace = "Display P3" }));
        Assert.Equal(ProjectErrorKind.Invalid, e.Kind);
    }

    // ── G04 :202-204 resolution ───────────────────────────────────
    [Theory]
    [InlineData(0d)]
    [InlineData(9601d)]
    [InlineData(double.NaN)]
    public void G04_resolution越界或非有限_拒(double resolution)
    {
        var e = Assert.Throws<ProjectException>(
            () => ProjectValidator.Validate(Valid() with { Resolution = resolution }));
        Assert.Equal(ProjectErrorKind.Invalid, e.Kind);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(72d)]
    [InlineData(9600d)]
    public void G04_resolution合法_放行(double resolution) =>
        ProjectValidator.Validate(Valid() with { Resolution = resolution });

    // ── G05 :205 画布尺寸 ─────────────────────────────────────────
    [Theory]
    [InlineData(0, 800)]
    [InlineData(30001, 800)]
    [InlineData(1000, 0)]
    [InlineData(1000, 30001)]
    public void G05_画布越界_报tooLarge(int w, int h)
    {
        var e = Assert.Throws<ProjectException>(
            () => ProjectValidator.Validate(Valid() with { Width = w, Height = h }));
        Assert.Equal(ProjectErrorKind.TooLarge, e.Kind);
    }

    // ── G06 :206 图层数上限 ───────────────────────────────────────
    [Fact]
    public void G06_图层超一万_报tooLarge()
    {
        var layers = Enumerable.Range(0, 10_001)
            .Select(i => ValidLayer() with { Id = Guid.NewGuid(), ImageFile = null, Name = $"L{i}" })
            .ToArray();

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid() with { Layers = layers }));
        Assert.Equal(ProjectErrorKind.TooLarge, e.Kind);
    }

    // ── G08 调整层 ───────────────────────────────────────────────
    [Fact]
    public void G08_调整层出现在v6_拒()
    {
        var adjustment = new LayerAdjustmentRecord { Kind = "Levels", Settings = new() };
        var layer = ValidLayer() with { ImageFile = null, Adjustment = adjustment };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 6 }));
        Assert.Equal(ProjectErrorKind.Invalid, e.Kind);
        Assert.Contains("G08", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G08_调整层不能带像素_拒()
    {
        var adjustment = new LayerAdjustmentRecord { Kind = "Levels", Settings = new() };
        var layer = ValidLayer() with { Adjustment = adjustment };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 11 }));
        Assert.Equal(ProjectErrorKind.Invalid, e.Kind);
    }

    // ── G09 kind 白名单 ───────────────────────────────────────────
    [Theory]
    [InlineData("Levels")]
    [InlineData("Gaussian Blur")]
    [InlineData("Black & White")]
    [InlineData("Color Balance")]
    public void G09_合法kind放行(string kind)
    {
        var adjustment = new LayerAdjustmentRecord { Kind = kind, Settings = new() };
        var layer = ValidLayer() with { ImageFile = null, Adjustment = adjustment };
        ProjectValidator.Validate(Valid(layer) with { Version = 11 });
    }

    [Theory]
    [InlineData("Levels2")]
    [InlineData("GaussianBlur")]   // 漏了空格
    [InlineData("levels")]          // 大小写错
    public void G09_未知kind_拒(string kind)
    {
        var adjustment = new LayerAdjustmentRecord { Kind = kind, Settings = new() };
        var layer = ValidLayer() with { ImageFile = null, Adjustment = adjustment };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 11 }));
        Assert.Contains("G09", e.Detail!, StringComparison.Ordinal);
    }

    // ── G10 :217-218 v9 闸门 ──────────────────────────────────────
    [Theory]
    [InlineData("Gaussian Blur")]
    [InlineData("Motion Blur")]
    [InlineData("Add Noise")]
    public void G10_模糊与噪声需v9(string kind)
    {
        var adjustment = new LayerAdjustmentRecord { Kind = kind, Settings = new() };
        var layer = ValidLayer() with { ImageFile = null, Adjustment = adjustment };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 7 }));
        Assert.Contains("G10", e.Detail!, StringComparison.Ordinal);
    }

    // ── G11/G12/G13 蒙版 ─────────────────────────────────────────
    [Fact]
    public void G11_蒙版文件名不符_拒()
    {
        var layer = ValidLayer() with { MaskFile = "wrong.png" };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 11 }));
        Assert.Contains("G11", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G11_普通图层蒙版需v4()
    {
        var layer = ValidLayer() with { MaskFile = ProjectManifest.MaskFileName(LayerId) };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 3 }));
        Assert.Contains("G11", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G11_分组蒙版需v6()
    {
        var group = ValidLayer() with { IsGroup = true, ImageFile = null, MaskFile = ProjectManifest.MaskFileName(LayerId) };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(group) with { Version = 4 }));
        Assert.Contains("G11", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G12_maskEnabled脱离maskFile_拒()
    {
        var layer = ValidLayer() with { MaskEnabled = true };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 11 }));
        Assert.Contains("G12", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G13_maskPlacement脱离maskFile_拒()
    {
        var layer = ValidLayer() with { MaskPlacement = new LayerTransform { Size = new DocSize(10, 10) } };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 11 }));
        Assert.Contains("G13", e.Detail!, StringComparison.Ordinal);
    }

    // ── G14 不透明度与混合模式 ────────────────────────────────────
    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void G14a_opacity越界_拒(double opacity)
    {
        var layer = ValidLayer() with { Opacity = opacity };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 11 }));
        Assert.Contains("G14a", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G14b_v3之前只允许默认值()
    {
        var layer = ValidLayer() with { Opacity = 0.5 };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 2 }));
        Assert.Contains("G14b", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G14c_分组混合模式恒为Normal()
    {
        var group = ValidLayer() with
        {
            IsGroup = true,
            ImageFile = null,
            BlendMode = Core.BlendMode.Multiply,
            Opacity = 0.5,
        };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(group) with { Version = 11 }));
        Assert.Contains("G14c", e.Detail!, StringComparison.Ordinal);
    }

    // ── G22/G23 版本闸门 ──────────────────────────────────────────
    [Fact]
    public void G22_maskSourceID需v5()
    {
        var layer = ValidLayer() with
        {
            MaskSourceId = Guid.NewGuid(),
            MaskFile = ProjectManifest.MaskFileName(LayerId),
        };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer) with { Version = 4 }));
        // ⚠️ Mac 的顺序是 :235 LiveMaskGraph 先跑、:236 版本闸门后跑，
        // 所以带 MaskFile 的构造会先撞活蒙版校验。与 Mac 同序，不是 bug。
        Assert.Contains("live mask", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G23_v1不得有分组或父子关系()
    {
        var group = ValidLayer() with { IsGroup = true, ImageFile = null };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(group) with { Version = 1 }));
        Assert.Contains("G23", e.Detail!, StringComparison.Ordinal);
    }

    // ── G24 图层唯一性/名字/文件名 ────────────────────────────────
    [Fact]
    public void G24_图层id重复_拒()
    {
        var manifest = Valid() with { Layers = new[] { ValidLayer(), ValidLayer() } };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(manifest));
        // :234 LayerHierarchy 先于 :238 的 G24 跑，重复 id 由 hierarchy 拦下（与 Mac 同序）
        Assert.Contains("duplicate", e.Detail!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void G24_空白名_拒(string name)
    {
        var layer = ValidLayer() with { Name = name };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer)));
        Assert.Contains("G24", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G24_名字超16384字节_拒()
    {
        var layer = ValidLayer() with { Name = new string('x', 16_385) };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer)));
        Assert.Contains("G24", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G24_imageFile必须是uuid加png的形式()
    {
        var layer = ValidLayer() with { ImageFile = "other.png" };
        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer)));
        Assert.Contains("G24", e.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回归用例：Core 的 <c>LayerTransform.IsValid</c> <b>没有</b> 1…300_000 的尺寸检查
    /// （Mac 的 LayerTransform.swift:29 有）。少这三条，一份 500,000×500,000 的图层
    /// 会被 Windows 放行、Mac 拒收。
    /// </summary>
    [Theory]
    [InlineData(300_001, 10)]
    [InlineData(10, 300_001)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void G24_变换尺寸越界_拒_不可委托给Core的IsValid(int w, int h)
    {
        var layer = ValidLayer() with
        {
            Transform = ValidLayer().Transform with { Size = new DocSize(w, h) },
        };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer)));
        Assert.Contains("G24", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G24_变换尺寸边界300000放行()
    {
        var layer = ValidLayer() with
        {
            Transform = ValidLayer().Transform with { Size = new DocSize(300_000, 300_000) },
        };

        ProjectValidator.Validate(Valid(layer));
    }

    [Theory]
    [InlineData(1_000_001d, 0d)]
    [InlineData(0d, -1_000_001d)]
    [InlineData(double.PositiveInfinity, 0d)]
    public void G24_原点越界或非有限_拒(double x, double y)
    {
        var layer = ValidLayer() with
        {
            Transform = ValidLayer().Transform with { Origin = new DocPoint(x, y) },
        };

        var e = Assert.Throws<ProjectException>(() => ProjectValidator.Validate(Valid(layer)));
        Assert.Contains("G24", e.Detail!, StringComparison.Ordinal);
    }

    // ── G25 活动图层 ─────────────────────────────────────────────
    [Fact]
    public void G25_activeLayerID悬空_拒()
    {
        var e = Assert.Throws<ProjectException>(
            () => ProjectValidator.Validate(Valid() with { ActiveLayerId = Guid.NewGuid() }));
        Assert.Contains("G25", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G25_activeLayerID指向存在的图层_放行() =>
        ProjectValidator.Validate(Valid() with { ActiveLayerId = LayerId });

    // ── G26/G27/G28 参考线 ───────────────────────────────────────
    [Fact]
    public void G26_v7及以下不得有参考线()
    {
        var guide = new CanvasGuide { Id = Guid.NewGuid(), Axis = "horizontal", Position = 10 };
        var e = Assert.Throws<ProjectException>(
            () => ProjectValidator.Validate(Valid() with { Version = 7, Guides = new[] { guide } }));
        Assert.Contains("G26", e.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void G27_参考线超一千条_报tooLarge()
    {
        var guides = Enumerable.Range(0, 1001)
            .Select(i => new CanvasGuide { Id = Guid.NewGuid(), Position = i })
            .ToArray();

        var e = Assert.Throws<ProjectException>(
            () => ProjectValidator.Validate(Valid() with { Version = 11, Guides = guides }));
        Assert.Equal(ProjectErrorKind.TooLarge, e.Kind);
    }

    [Fact]
    public void G28_参考线id重复_拒()
    {
        var id = Guid.NewGuid();
        var guides = new[]
        {
            new CanvasGuide { Id = id, Position = 1 },
            new CanvasGuide { Id = id, Position = 2 },
        };

        var e = Assert.Throws<ProjectException>(
            () => ProjectValidator.Validate(Valid() with { Version = 11, Guides = guides }));
        Assert.Contains("G28", e.Detail!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_000_001d)]
    [InlineData(-1_000_001d)]
    [InlineData(double.NaN)]
    public void G28_参考线位置越界_拒(double position)
    {
        var guide = new CanvasGuide { Id = Guid.NewGuid(), Position = position };
        var e = Assert.Throws<ProjectException>(
            () => ProjectValidator.Validate(Valid() with { Version = 11, Guides = new[] { guide } }));
        Assert.Contains("G28", e.Detail!, StringComparison.Ordinal);
    }
}