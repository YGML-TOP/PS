using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Compositor.Core;

namespace Compositor.Project;

/// <summary>
/// <c>.comp</c> manifest 的 JSON 序列化配置与自定义转换器。
/// </summary>
/// <remarks>
/// <para>
/// <b>三条序列化铁律（错一条 Mac 版就读不出来）：</b>
/// </para>
/// <list type="number">
/// <item><c>blendMode</c> 写字符串字面量，形如 <c>"Linear Dodge (Add)"</c>——<b>带空格与括号</b>。
/// 写裸枚举名会让 Mac 版把每个图层的混合模式都落到 Normal。</item>
/// <item><c>sampling</c> 写 <c>"Nearest"</c> / <c>"Smooth"</c> / <c>"High quality"</c> 三者之一，
/// 区分大小写。</item>
/// <item><c>version</c> 写 <c>11</c>。</item>
/// </list>
/// <para>
/// <b>关于键序：</b>Mac 的 <c>JSONEncoder</c> 开了 <c>.sortedKeys</c>，本移植照同样的字母序写出，
/// 这样两边产出的 manifest 可做字节级比对。键序对语义互通无影响。
/// </para>
/// </remarks>
public static class ProjectJson
{
    /// <summary>manifest 的序列化配置。</summary>
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    /// <summary>
    /// 可恢复异常的诊断出口。主要是 <see cref="DocSizeConverter"/> 遇到小数时按
    /// <see cref="Math.Ceiling(double)"/> 取整的记录。
    /// </summary>
    /// <remarks>
    /// 做成可注入的静态回调而不是直接写日志：本层不该决定日志框架，
    /// 而测试需要一个不依赖日志设施就能断言的出口。
    /// </remarks>
    public static Action<string>? WarningSink { get; set; }

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,

            // 🔴 必须关掉 .NET 默认的非 ASCII 转义：它会把中文图层名写成 \u6700\u5E95。
            // Mac 的 JSONEncoder 直接输出 UTF-8 原字符。两边都仍是合法 JSON、
            // 互相读得到，但转义后的 manifest 不可读、体积还大一倍，
            // 且会让基于字节的往返比对全部对不上。
            // 这里的 "Unsafe" 只针对 HTML/JS 上下文；我们是写文件，不存在注入面。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.Converters.Add(new DocPointConverter());
        options.Converters.Add(new DocSizeConverter());
        options.Converters.Add(new LayerTransformConverter());
        options.Converters.Add(new BlendModeConverter());
        options.Converters.Add(new SamplingQualityConverter());

        // 🔴 UUID 必须按大写写出。这是「两边产出的 manifest 可做字节级比对」的前提，
        // 不是风格问题：Swift 的 UUID.uuidString 是大写，.NET 的 Guid.ToString() 是小写，
        // 而同一份 manifest 里的 imageFile 又必须是大写 UUID + ".png"
        // （Mac 在 ProjectStore.swift:223,243 按 uuidString 校验它）。
        // 于是小写 id + 大写 imageFile 会在两边产出**永远不可能相同**的字节，
        // 且 id 与 imageFile 自身看起来就是"对不上"。
        options.Converters.Add(new UppercaseGuidConverter());
        options.Converters.Add(new UppercaseNullableGuidConverter());
        return options;
    }
}

/// <summary>
/// <c>Guid</c> ↔ 大写 UUID 字符串。
/// </summary>
/// <remarks>
/// <para>
/// Swift 的 <c>UUID</c> 走 Foundation 的 <c>Codable</c>，编码结果是 <c>uuidString</c>，即<b>大写</b>；
/// .NET 的 <c>Guid.ToString()</c> 默认<b>小写</b>。两者读得进对方（Foundation 解析忽略大小写），
/// 所以互通不受影响，但字节级比对永远对不上。
/// </para>
/// <para>
/// <b>读取一律忽略大小写</b>：既兼容 Mac 写出的，也兼容早于本转换器写出的
/// 小写历史文件。
/// </para>
/// </remarks>
public sealed class UppercaseGuidConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetGuid();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());
}

/// <summary>
/// <see cref="UppercaseGuidConverter"/> 的可空版。仅在值非 null 时接管，
/// null 交给 <c>WhenWritingNull</c> 处理。
/// </summary>
public sealed class UppercaseNullableGuidConverter : JsonConverter<Guid?>
{
    /// <inheritdoc />
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetGuid();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant());
    }
}

/// <summary>
/// <c>DocPoint</c> ↔ <c>[x, y]</c> 数组。
/// </summary>
/// <remarks>
/// Swift 的 <c>CGPoint</c> 是 <c>Codable</c>，Foundation 按<b>无键容器</b>写成数组。
/// 用默认的对象式序列化会写出 <c>{"x":0,"y":0}</c>，Mac 版读不到。
/// </remarks>
public sealed class DocPointConverter : JsonConverter<DocPoint>
{
    /// <inheritdoc />
    public override DocPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"CGPoint 应为 [x, y] 数组，实际是 {reader.TokenType}。");
        }

        reader.Read();
        var x = reader.GetDouble();
        reader.Read();
        var y = reader.GetDouble();
        ExpectEndArray(ref reader);
        return new DocPoint(x, y);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DocPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }

    internal static void ExpectEndArray(ref Utf8JsonReader reader)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("数组元素个数不是 2。");
        }
    }
}

/// <summary>
/// <c>DocSize</c> ↔ <c>[width, height]</c> 数组。
/// </summary>
/// <remarks>
/// <b>🔴 小数取整规则：</b>契约把 <c>DocSize</c> 定为 <c>(int Width, int Height)</c>，
/// 但 Mac 的 <c>CGSize</c> 是可含小数的。若 manifest 里出现 <c>[800.5, 600.2]</c>，
/// 本移植<b>按 <see cref="Math.Ceiling(double)"/> 取整并记一条警告，绝不抛异常</b> ——
/// 不能因为 Mac 端偶发一个浮点就打不开用户的工程。
/// </remarks>
public sealed class DocSizeConverter : JsonConverter<DocSize>
{
    /// <inheritdoc />
    public override DocSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"CGSize 应为 [width, height] 数组，实际是 {reader.TokenType}。");
        }

        reader.Read();
        var width = ReadComponent(ref reader, "width");
        reader.Read();
        var height = ReadComponent(ref reader, "height");
        DocPointConverter.ExpectEndArray(ref reader);

        return new DocSize(width, height);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DocSize value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Width);
        writer.WriteNumberValue(value.Height);
        writer.WriteEndArray();
    }

    private static int ReadComponent(ref Utf8JsonReader reader, string name)
    {
        var raw = reader.GetDouble();
        if (double.IsNaN(raw) || double.IsInfinity(raw))
        {
            throw new JsonException($"{name} 不是有限值。");
        }

        // 整数直接用；带小数才 Ceiling 并记录，避免对正常整数产生噪音。
        if (raw == System.Math.Floor(raw) && raw >= int.MinValue && raw <= int.MaxValue)
        {
            return (int)raw;
        }

        // ⚠️ 必须先钳位再转型：直接 (int)3e9 在 C# 里会得到 int.MinValue（负数），
        // 一个 width 变成负数的图层会一路畅通无阻，直到下游限额判定才炸。
        if (raw > int.MaxValue)
        {
            ProjectJson.WarningSink?.Invoke(
                $"manifest 的 {name} 是 {raw.ToString("R", CultureInfo.InvariantCulture)}，"
                + "超出 int 范围，已钳到 int.MaxValue。");
            return int.MaxValue;
        }

        if (raw < int.MinValue)
        {
            ProjectJson.WarningSink?.Invoke(
                $"manifest 的 {name} 是 {raw.ToString("R", CultureInfo.InvariantCulture)}，"
                + "超出 int 范围，已钳到 int.MinValue。");
            return int.MinValue;
        }

        var rounded = (int)Math.Ceiling(raw);
        ProjectJson.WarningSink?.Invoke(
            $"manifest 的 {name} 是小数 {raw.ToString("R", CultureInfo.InvariantCulture)}，"
            + $"已按 Math.Ceiling 取整为 {rounded.ToString(CultureInfo.InvariantCulture)}。");

        return rounded;
    }
}

/// <summary>
/// <c>LayerTransform</c> ↔ <c>{flipX, flipY, origin, rotation, sampling, size}</c>。
/// </summary>
/// <remarks>
/// <b>🔴 键名 <c>rotation</c> 不是 <c>rotationDegrees</c>。</b>Mac 的属性名就是
/// <c>rotation</c>（<c>Document/LayerTransform.swift:21</c>），而 Core 的契约类型把它叫
/// <c>RotationDegrees</c>。这个转换器是两者之间唯一的桥，键名写错 Mac 版整个图层变换就丢默认值。
/// </remarks>
public sealed class LayerTransformConverter : JsonConverter<LayerTransform>
{
    /// <inheritdoc />
    public override LayerTransform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("LayerTransform 应为 JSON 对象。");
        }

        DocPoint origin = default;
        DocSize size = default;
        var rotation = 0d;
        var flipX = false;
        var flipY = false;
        var sampling = SamplingQuality.HighQuality;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new LayerTransform
                {
                    Origin = origin,
                    Size = size,
                    RotationDegrees = rotation,
                    FlipX = flipX,
                    FlipY = flipY,
                    Sampling = sampling,
                };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("期望属性名。");
            }

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case "origin":
                    origin = ReadPoint(ref reader);
                    break;
                case "size":
                    size = ReadSize(ref reader);
                    break;
                case "rotation":
                    rotation = reader.GetDouble();
                    break;
                case "flipX":
                    flipX = reader.GetBoolean();
                    break;
                case "flipY":
                    flipY = reader.GetBoolean();
                    break;
                case "sampling":
                    sampling = SamplingStrings.Parse(reader.GetString()!);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("LayerTransform 对象未正常结束。");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LayerTransform value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        // 按字母序，与 Mac 的 .sortedKeys 一致。
        writer.WriteBoolean("flipX", value.FlipX);
        writer.WriteBoolean("flipY", value.FlipY);

        writer.WritePropertyName("origin");
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Origin.X);
        writer.WriteNumberValue(value.Origin.Y);
        writer.WriteEndArray();

        writer.WriteNumber("rotation", value.RotationDegrees);

        // 铁律：字符串字面量，区分大小写。
        writer.WriteString("sampling", SamplingStrings.ToLiteral(value.Sampling));

        writer.WritePropertyName("size");
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Size.Width);
        writer.WriteNumberValue(value.Size.Height);
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static DocPoint ReadPoint(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("origin 应为 [x, y] 数组。");
        }

        reader.Read();
        var x = reader.GetDouble();
        reader.Read();
        var y = reader.GetDouble();
        DocPointConverter.ExpectEndArray(ref reader);
        return new DocPoint(x, y);
    }

    private static DocSize ReadSize(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("size 应为 [width, height] 数组。");
        }

        var converter = new DocSizeConverter();
        return converter.Read(ref reader, typeof(DocSize), null!);
    }
}

/// <summary>
/// <c>BlendMode</c> ↔ <c>.comp</c> 字符串字面量。
/// </summary>
/// <remarks>
/// 契约 v1.1 修正记录第 1、3 条：真实枚举是 <c>enum LayerBlendMode: String, Codable</c>，
/// 24 个字面量<b>带空格与括号</b>（如 <c>colorBurn = "Color Burn"</c>、
/// <c>linearDodge = "Linear Dodge (Add)"</c>）。按整数或裸枚举名序列化会让
/// Mac 版读到的每个图层都落到 Normal。
/// </remarks>
public sealed class BlendModeConverter : JsonConverter<BlendMode>
{
    /// <inheritdoc />
    public override BlendMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var literal = reader.GetString();
        if (literal is null)
        {
            throw new JsonException("blendMode 不能为 null。");
        }

        return BlendModeStrings.Parse(literal);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BlendMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(BlendModeStrings.ToLiteral(value));
}

/// <summary>
/// <c>SamplingQuality</c> ↔ <c>.comp</c> 字符串字面量。
/// </summary>
/// <remarks>
/// 注册它是为了防止任何地方"顺手"把 <c>SamplingQuality</c> 直接序列化成一个整数。
/// 它在 <see cref="LayerTransformConverter"/> 里是手工写的，这里是第二道防线。
/// </remarks>
public sealed class SamplingQualityConverter : JsonConverter<SamplingQuality>
{
    /// <inheritdoc />
    public override SamplingQuality Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var literal = reader.GetString();
        if (literal is null)
        {
            throw new JsonException("sampling 不能为 null。");
        }

        return SamplingStrings.Parse(literal);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SamplingQuality value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SamplingStrings.ToLiteral(value));
}