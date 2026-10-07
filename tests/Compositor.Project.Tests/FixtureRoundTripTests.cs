using System.Text.Json;
using Xunit;

namespace Compositor.Project.Tests;

/// <summary>
/// 用 <c>ai2-06</c> 的三套手工夹具（v1 / v7 / v11）做 manifest 往返。
/// </summary>
/// <remarks>
/// <para>
/// <b>⚠️ 这一组能证明什么、不能证明什么，必须说清楚：</b>
/// </para>
/// <list type="bullet">
/// <item>
/// <b>能</b>：证明本移植的序列化器对这三份 manifest 是<b>无损且自洽</b>的
/// （parse → write → parse → write 逐字节收敛），键名与 Mac 侧属性名一致，
/// 数值无损，且三份夹具都能通过全部 guard。
/// </item>
/// <item>
/// <b>不能</b>：证明"与 Mac 版互通"。夹具是 AI-2 自己按 <c>ai2-00</c> 手工构造的，
/// 用自己造的输入测自己的解析器，只能证<b>自洽</b>，证不了<b>他机一致</b>。
/// 真互通要等阶段二：真实 Mac 产出的 <c>.comp</c>（需 GitHub Actions macOS runner）。
/// </item>
/// </list>
/// <para>
/// 之所以仍然值得做：互通失败最常见的形态是<b>静默降级</b>（字段读不到 → 落默认值），
/// 而不是报错。键名与字面量的逐条断言能在阶段二之前就把这类错误挡住一大半。
/// </para>
/// <para>
/// 范围：<b>仅 manifest</b>。像素资源（<c>images/*.png</c>）的往返属
/// <c>ProjectStore</c> 的职责，那部分随 v1.2 的 <c>IProjectAsset</c> 一起做，尚未开始。
/// </para>
/// </remarks>
public sealed class FixtureRoundTripTests
{
    private static string FixtureDir(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Mac 侧 manifest 的全部合法顶层键（ProjectStore.swift:20-30）。</summary>
    private static readonly string[] MacTopLevelKeys =
    {
        "activeLayerID", "colorSpace", "documentID", "format", "guides",
        "height", "layers", "resolution", "version", "width",
    };

    /// <summary>Mac 侧图层记录的合法键（ProjectStore.swift:36-55）。</summary>
    private static readonly string[] MacLayerKeys =
    {
        "adjustment", "blendMode", "effects", "id", "imageFile", "isGroup", "isVisible",
        "maskEnabled", "maskFile", "maskLinked", "maskPlacement", "maskSourceID",
        "name", "opacity", "parentID", "shape", "text", "transform",
    };

    [Theory]
    [InlineData("v1-minimal.comp", 1)]
    [InlineData("v7-structure.comp", 7)]
    [InlineData("v11-full.comp", 11)]
    public void 三套夹具都能解析(string packageName, int expectedVersion)
    {
        var manifest = LoadManifest(packageName);

        Assert.Equal(expectedVersion, manifest.Version);
        Assert.Equal("com.compositor.project", manifest.Format);
        Assert.Equal("sRGB", manifest.ColorSpace);
        Assert.NotEmpty(manifest.Layers);
    }

    [Theory]
    [InlineData("v1-minimal.comp")]
    [InlineData("v7-structure.comp")]
    [InlineData("v11-full.comp")]
    public void 三套夹具都通过全部guard(string packageName)
    {
        var manifest = LoadManifest(packageName);
        ProjectValidator.Validate(manifest);
    }

    [Theory]
    [InlineData("v1-minimal.comp")]
    [InlineData("v7-structure.comp")]
    [InlineData("v11-full.comp")]
    public void 往返是无损且收敛的(string packageName)
    {
        var first = LoadManifest(packageName);
        var once = JsonSerializer.Serialize(first, ProjectJson.Options);

        var second = JsonSerializer.Deserialize<ProjectManifest>(once, ProjectJson.Options)!;
        var twice = JsonSerializer.Serialize(second, ProjectJson.Options);

        // parse → write → parse → write 必须是不动点。
        // 不收敛说明有字段读进来就丢，或写出去的键名读不回来。
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("v11-full.comp")]
    public void 写出的顶层键全部在Mac白名单内(string packageName)
    {
        var manifest = LoadManifest(packageName);
        var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);

        using var doc = JsonDocument.Parse(json);
        var actual = doc.RootElement.EnumerateObject().Select(static p => p.Name).ToArray();

        foreach (var key in actual)
        {
            Assert.Contains(key, MacTopLevelKeys);
        }

        foreach (var key in MacTopLevelKeys)
        {
            Assert.Contains(key, actual);
        }
    }

    [Theory]
    [InlineData("v11-full.comp")]
    public void 写出的图层键全部在Mac白名单内(string packageName)
    {
        var manifest = LoadManifest(packageName);
        var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);

        using var doc = JsonDocument.Parse(json);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in doc.RootElement.GetProperty("layers").EnumerateArray())
        {
            foreach (var p in layer.EnumerateObject())
            {
                keys.Add(p.Name);
            }
        }

        foreach (var key in keys)
        {
            Assert.Contains(key, MacLayerKeys);
        }
    }

    [Fact]
    public void v11夹具的变换键名与Mac一致()
    {
        var manifest = LoadManifest("v11-full.comp");
        var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);

        using var doc = JsonDocument.Parse(json);
        var transform = doc.RootElement.GetProperty("layers")[0].GetProperty("transform");

        foreach (var key in new[] { "flipX", "flipY", "origin", "rotation", "sampling", "size" })
        {
            Assert.True(transform.TryGetProperty(key, out _), $"transform 缺键 {key}");
        }

        Assert.Equal(JsonValueKind.Array, transform.GetProperty("origin").ValueKind);
        Assert.Equal(JsonValueKind.Array, transform.GetProperty("size").ValueKind);
        Assert.Equal(JsonValueKind.String, transform.GetProperty("sampling").ValueKind);
    }

    [Fact]
    public void v7与v11夹具的混合模式是字符串而非整数()
    {
        foreach (var package in new[] { "v7-structure.comp", "v11-full.comp" })
        {
            var manifest = LoadManifest(package);
            var json = JsonSerializer.Serialize(manifest, ProjectJson.Options);

            using var doc = JsonDocument.Parse(json);
            foreach (var layer in doc.RootElement.GetProperty("layers").EnumerateArray())
            {
                if (!layer.TryGetProperty("blendMode", out var blend))
                {
                    continue;
                }

                Assert.Equal(JsonValueKind.String, blend.ValueKind);
            }
        }
    }

    [Fact]
    public void v1夹具不得含分组与父子关系()
    {
        // 对应 ProjectStore.swift:237
        var manifest = LoadManifest("v1-minimal.comp");
        Assert.All(manifest.Layers, l =>
        {
            Assert.Null(l.ParentId);
            Assert.NotEqual(true, l.IsGroup);
        });
    }

    [Fact]
    public void v1夹具不得含参考线()
    {
        // 对应 ProjectStore.swift:251-253
        var manifest = LoadManifest("v1-minimal.comp");
        Assert.True(manifest.Guides is null || manifest.Guides.Count == 0);
    }

    private static ProjectManifest LoadManifest(string packageName)
    {
        var path = Path.Combine(FixtureDir(packageName), "manifest.json");
        Assert.True(File.Exists(path), $"找不到夹具 {path}");
        return JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path), ProjectJson.Options)!;
    }
}