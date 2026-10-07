using System.Text.Json;
using Compositor.Core;
using Xunit;

namespace Compositor.Project.Tests;

/// <summary>
/// manifest 序列化测试。<b>三条序列化铁律全部在这里钉死。</b>
/// </summary>
/// <remarks>
/// 这三条错任何一条，Mac 版都读不出正确的工程，而症状往往不是"报错"而是
/// <b>静默降级</b>——混合模式全落到 Normal、采样设置整层失效、字段读不到变默认值。
/// 静默降级是最难排查的一类互 bug，所以必须有逐字断言。
/// </remarks>
public sealed class SerializationTests
{
    private static ProjectLayerRecord SampleLayer() => new()
    {
        Id = new Guid("11111111-2222-4333-8444-555555555555"),
        Name = "背景",
        IsVisible = true,
        ImageFile = "11111111-2222-4333-8444-555555555555.png",
        Transform = new LayerTransform
        {
            Origin = new DocPoint(0d, 0d),
            Size = new DocSize(100, 80),
            RotationDegrees = 0d,
            Sampling = SamplingQuality.HighQuality,
        },
    };

    private static ProjectManifest SampleManifest() => new()
    {
        DocumentId = new Guid("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
        Width = 100,
        Height = 80,
        Layers = new[] { SampleLayer() },
    };

    [Fact]
    public void 铁律1_BlendMode写成字符串字面量_带空格与括号()
    {
        // 契约 v1.1 修正记录 #1/#3：enum LayerBlendMode: String，字面量带空格与括号。
        var manifest = SampleManifest() with
        {
            Layers = new[]
            {
                SampleLayer() with { BlendMode = Core.BlendMode.LinearDodge },
                SampleLayer() with { Id = Guid.NewGuid(), BlendMode = Core.BlendMode.ColorBurn },
                SampleLayer() with { Id = Guid.NewGuid(), BlendMode = Core.BlendMode.VividLight },
            },
        };

        var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);

        Assert.Contains("\"Linear Dodge (Add)\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Color Burn\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Vivid Light\"", json, StringComparison.Ordinal);

        // 绝不能是裸枚举名 —— 写了 Mac 版会把这些图层全部落到 Normal。
        Assert.DoesNotContain("\"LinearDodge\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ColorBurn\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SamplingQuality.Nearest, "Nearest")]
    [InlineData(SamplingQuality.Smooth, "Smooth")]
    [InlineData(SamplingQuality.HighQuality, "High quality")]
    public void 铁律2_Sampling写成三档字符串字面量(SamplingQuality quality, string expected)
    {
        var manifest = SampleManifest() with
        {
            Layers = new[] { SampleLayer() with { Transform = SampleLayer().Transform with { Sampling = quality } } },
        };

        var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);
        Assert.Contains($"\"sampling\": \"{expected}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 铁律3_新保存写version11()
    {
        var json = JsonSerializer.Serialize(SampleManifest(), ProjectJson.Options);
        Assert.Contains("\"version\": 11", json, StringComparison.Ordinal);
        Assert.Equal(11, ProjectManifest.CurrentVersion);
    }

    [Fact]
    public void DocPoint与DocSize写成数组_不是对象()
    {
        // Swift 的 CGPoint/CGSize 是 Codable，Foundation 按无键容器写成数组。
        var manifest = SampleManifest() with
        {
            Layers = new[]
            {
                SampleLayer() with
                {
                    Transform = new LayerTransform
                    {
                        Origin = new DocPoint(12.5, -3d),
                        Size = new DocSize(64, 32),
                        Sampling = SamplingQuality.Nearest,
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);

        using var doc = JsonDocument.Parse(json);
        var transform = doc.RootElement.GetProperty("layers")[0].GetProperty("transform");
        Assert.Equal(JsonValueKind.Array, transform.GetProperty("origin").ValueKind);
        Assert.Equal(JsonValueKind.Array, transform.GetProperty("size").ValueKind);
        Assert.Equal(12.5, transform.GetProperty("origin")[0].GetDouble());
        Assert.Equal(64, transform.GetProperty("size")[0].GetInt32());

        // 对象式会写成 {"x":..,"y":..} / {"width":..,"height":..}，Mac 版读不到。
        // 注意只能在 transform 对象内部查：manifest 顶层本来就有 width/height 字段。
        foreach (var key in new[] { "x", "y", "width", "height" })
        {
            Assert.False(
                transform.TryGetProperty(key, out _),
                $"transform 里出现了 {key}，说明 origin/size 被写成了对象而不是数组");
        }

        // 顶层 width/height 仍须是标量，不能被序列化成数组
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("width").ValueKind);
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("height").ValueKind);
    }

    [Fact]
    public void 变换键名是rotation_不是rotationDegrees()
    {
        // Mac 的属性名是 rotation（Document/LayerTransform.swift:21），
        // Core 的契约类型把它叫 RotationDegrees。这个转换器是唯一的桥。
        var json = JsonSerializer.Serialize(SampleManifest(), ProjectJson.Options);

        Assert.Contains("\"rotation\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RotationDegrees", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"rotationDegrees\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 图层键名保持大写ID_不能按CSharp命名习惯推()
    {
        var manifest = SampleManifest() with
        {
            Layers = new[]
            {
                SampleLayer() with
                {
                    ParentId = new Guid("99999999-2222-4333-8444-555555555555"),
                    MaskFile = "11111111-2222-4333-8444-555555555555.mask.png",
                    MaskSourceId = new Guid("88888888-2222-4333-8444-555555555555"),
                },
            },
        };

        var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);

        foreach (var key in new[] { "parentID", "maskFile", "maskSourceID", "imageFile", "isVisible" })
        {
            Assert.Contains($"\"{key}\"", json, StringComparison.Ordinal);
        }

        // 这些是错的写法，Mac 版读不到。
        foreach (var wrong in new[] { "parentId", "maskSourceId", "image_file", "parent_id" })
        {
            Assert.DoesNotContain($"\"{wrong}\"", json, StringComparison.Ordinal);
        }

        Assert.Contains("\"documentID\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"documentId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺省的maskLinked不写出去_写出null会让Mac多一个键()
    {
        // Mac 的 maskLinked 是 Bool? = nil，JSONEncoder 省略 nil。
        var json = JsonSerializer.Serialize(SampleManifest(), ProjectJson.Options);
        Assert.DoesNotContain("maskLinked", json, StringComparison.Ordinal);

        var unlinked = SampleManifest() with
        {
            Layers = new[]
            {
                SampleLayer() with { MaskLinked = false, MaskFile = "11111111-2222-4333-8444-555555555555.mask.png" },
            },
        };
        Assert.Contains("\"maskLinked\": false", JsonSerializer.Serialize(unlinked, ProjectJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void 图层数组索引0是最底层()
    {
        // Mac 的 manifest.layers 顺序即绘制顺序，自下而上。
        // 顺序错了整个工程上下颠倒，且不会报错。
        var bottom = SampleLayer() with { Id = new Guid("00000000-0000-4000-8000-000000000001"), Name = "最底" };
        var top = SampleLayer() with { Id = new Guid("00000000-0000-4000-8000-000000000002"), Name = "最上" };

        var json = JsonSerializer.Serialize(SampleManifest() with { Layers = new[] { bottom, top } }, ProjectJson.Options);

        var bottomAt = json.IndexOf("最底", StringComparison.Ordinal);
        var topAt = json.IndexOf("最上", StringComparison.Ordinal);

        Assert.True(bottomAt >= 0 && topAt > bottomAt,
            $"索引 0 必须是最底层，但'最底'在 {bottomAt}、'最上'在 {topAt}");
    }
}

/// <summary>
/// <see cref="DocSizeConverter"/> 的小数取整行为。
/// </summary>
/// <remarks>
/// 契约：反序列化时若 <c>width</c>/<c>height</c> 带小数，<b>按 <c>Math.Ceiling</c> 取整并记警告，
/// 不许抛异常</b>（YG 2026-10-07 裁决）。理由是不能因为 Mac 端偶发一个浮点就打不开用户的工程。
/// </remarks>
public sealed class DocSizeFractionalTests
{
    [Fact]
    public void 小数按Ceiling取整_且不抛异常()
    {
        // 直接手写带小数的 JSON，不做「先序列化再字符串替换」——
        // 那种写法依赖缩进与换行符，WriteIndented 一改就静默失效（本次就踩过一次）。
        var json = "{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\","
            + "\"documentID\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"width\":10,\"height\":10,"
            + "\"layers\":[{\"id\":\"11111111-2222-4333-8444-555555555555\",\"name\":\"L\",\"isVisible\":true,"
            + "\"transform\":{\"origin\":[0,0],\"size\":[64.5,32.1],\"rotation\":0,\"flipX\":false,\"flipY\":false,"
            + "\"sampling\":\"High quality\"}}]}";

        var warnings = new List<string>();
        var previous = ProjectJson.WarningSink;
        ProjectJson.WarningSink = warnings.Add;
        try
        {
            var roundTripped = JsonSerializer.Deserialize<ProjectManifest>(json, ProjectJson.Options);

            Assert.Equal(65, roundTripped!.Layers[0].Transform.Size.Width);   // 64.5 → 65
            Assert.Equal(33, roundTripped.Layers[0].Transform.Size.Height);   // 32.1 → 33
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.Contains("Math.Ceiling", w, StringComparison.Ordinal));
        }
        finally
        {
            ProjectJson.WarningSink = previous;
        }
    }

    [Fact]
    public void 整数尺寸不产生警告噪音()
    {
        var json = "{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\","
            + "\"documentID\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"width\":10,\"height\":10,"
            + "\"layers\":[{\"id\":\"11111111-2222-4333-8444-555555555555\",\"name\":\"L\",\"isVisible\":true,"
            + "\"transform\":{\"origin\":[0,0],\"size\":[64,32],\"rotation\":0,\"flipX\":false,\"flipY\":false,"
            + "\"sampling\":\"High quality\"}}]}";

        var warnings = new List<string>();
        var previous = ProjectJson.WarningSink;
        ProjectJson.WarningSink = warnings.Add;
        try
        {
            var parsed = JsonSerializer.Deserialize<ProjectManifest>(json, ProjectJson.Options);
            Assert.Equal(64, parsed!.Layers[0].Transform.Size.Width);
            Assert.Empty(warnings);
        }
        finally
        {
            ProjectJson.WarningSink = previous;
        }
    }

    [Fact]
    public void 超出int范围的小数钳到边界_不能变成负数()
    {
        // 回归用例：曾写成 (int)Math.Ceiling(raw) 再钳位，转型先发生，
        // 3e9 会被转成 int.MinValue —— 一个负数宽度的图层能一路畅通到下游才炸。
        var json = "{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\","
            + "\"documentID\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"width\":10,\"height\":10,"
            + "\"layers\":[{\"id\":\"11111111-2222-4333-8444-555555555555\",\"name\":\"L\",\"isVisible\":true,"
            + "\"transform\":{\"origin\":[0,0],\"size\":[3e9,4.2],\"rotation\":0,\"flipX\":false,\"flipY\":false,"
            + "\"sampling\":\"High quality\"}}]}";

        var parsed = JsonSerializer.Deserialize<ProjectManifest>(json, ProjectJson.Options);

        Assert.True(parsed!.Layers[0].Transform.Size.Width > 0,
            $"宽变成了 {parsed.Layers[0].Transform.Size.Width}，说明发生了 int 溢出回绕");
        Assert.Equal(int.MaxValue, parsed.Layers[0].Transform.Size.Width);
        Assert.Equal(5, parsed.Layers[0].Transform.Size.Height);
    }

    [Fact]
    public void 未知字面量抛错而不是静默降级()
    {
        var json = "{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\","
            + "\"documentID\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"width\":10,\"height\":10,"
            + "\"layers\":[{\"id\":\"11111111-2222-4333-8444-555555555555\",\"name\":\"L\",\"isVisible\":true,"
            + "\"transform\":{\"origin\":[0,0],\"size\":[10,10],\"rotation\":0,\"flipX\":false,\"flipY\":false,"
            + "\"sampling\":\"high_quality\"}}]}";

        // 大小写错、写法错都必须炸出来。静默落到默认值 = 整层采样设置失效。
        // 抛 FormatException 是契约 v1.1 明确规定的（BlendModeStrings.Parse /
        // SamplingStrings.Parse 均声明「未知字面量抛 FormatException，区分大小写」），
        // 不是 JsonException —— 断言要跟契约走，别拿框架异常类型想当然。
        Assert.Throws<FormatException>(
            () => JsonSerializer.Deserialize<ProjectManifest>(json, ProjectJson.Options));
    }

    [Fact]
    public void 未知混合模式字面量同样抛错()
    {
        var json = "{\"format\":\"com.compositor.project\",\"version\":11,\"colorSpace\":\"sRGB\","
            + "\"documentID\":\"aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\",\"width\":10,\"height\":10,"
            + "\"layers\":[{\"id\":\"11111111-2222-4333-8444-555555555555\",\"name\":\"L\",\"isVisible\":true,"
            + "\"blendMode\":\"LinearDodge\","
            + "\"transform\":{\"origin\":[0,0],\"size\":[10,10],\"rotation\":0,\"flipX\":false,\"flipY\":false,"
            + "\"sampling\":\"High quality\"}}]}";

        // 漏了空格 → Mac 版读不到该图层 → 静默落到 Normal。这是三条铁律里最致命的一条。
        Assert.Throws<FormatException>(
            () => JsonSerializer.Deserialize<ProjectManifest>(json, ProjectJson.Options));
    }
}