using System.Text;
using System.Text.Json.Nodes;
using Compositor.Core;
using Compositor.Imaging;

namespace Compositor.Project;

/// <summary>
/// <c>.comp</c> manifest 的全部校验。逐条对照 macOS 版
/// <c>Compositor/IO/ProjectStore.swift:198-262</c> 的
/// <c>validate</c> + <c>validateGuides</c>。
/// </summary>
/// <remarks>
/// <para>
/// 每条 guard 前标了 Mac 源码的 <c>文件:行号</c>，便于逐行回溯核对。
/// 判定用的错误类别（invalid / version / tooLarge）与 Mac 完全一致——
/// 用户看到的报错类型不能因为换平台而变。
/// </para>
/// <para>
/// ⚠️ <b>已知未覆盖项（如实标注）：</b>guard G09 对应的 Mac 侧
/// <c>LayerAdjustment.isValid</c>（<c>Document/LayerAdjustment.swift:128-141</c>）
/// 是<b>逐 kind 的深层数值校验</b>，涉及 <c>LevelsSettings</c> / <c>CurvesSettings</c> /
/// <c>ExposureSettings</c> / <c>GradientMapSettings</c> / <c>GrainSettings</c> /
/// <c>BlackWhiteSettings</c> / <c>ColorBalanceSettings</c> 七个子模型。
/// 本移植实现了 kind 白名单、版本闸门与标量区间，
/// <b>七个子模型的深层校验尚未移植</b>，出处见 <see cref="IsAdjustmentValid"/> 的备注。
/// 后果（如实记录）：一份含越界 levels/curves 标量的工程，Windows 会放行而 Mac 会拒收。
/// 补齐时应回 <c>Document/LayerAdjustment.swift:130-136</c> 与
/// <c>Document/ImageAdjustments.swift</c>、<c>Levels.swift</c>、<c>Curves.swift</c>。
/// </para>
/// </remarks>
public static class ProjectValidator
{
    /// <summary>图层数上限。对应 <c>ProjectStore.swift:206</c>。</summary>
    public const int MaxLayers = 10_000;

    /// <summary>参考线数上限。对应 <c>ProjectStore.swift:255</c>。</summary>
    public const int MaxGuides = 1_000;

    /// <summary>图层名字节数上限（UTF-8）。对应 <c>ProjectStore.swift:242</c>。</summary>
    public const int MaxLayerNameBytes = 16_384;

    /// <summary>
    /// 图层变换尺寸上限。对应 <c>Document/LayerTransform.swift:29</c> 的 <c>(1...300_000)</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Core 的 <c>LayerTransform.IsValid</c> 没有这三条尺寸检查</b>
    /// （它只查有限性与原点范围）。Mac 侧是靠 <c>transform.isValid</c> 在
    /// <c>ProjectStore.swift:240</c> 强制它的，所以这里必须自己补，不能委托给 Core。
    /// </remarks>
    public const int MaxTransformSide = 300_000;

    /// <summary>原坐标绝对值上限。对应 <c>LayerTransform.swift:30</c>。</summary>
    public const double MaxOriginMagnitude = 1_000_000d;

    /// <summary>调整层的全部合法 kind 字面量。逐字取自 <c>LayerAdjustment.swift:4-9</c>。</summary>
    public static readonly string[] AdjustmentKinds =
    {
        "Hue/Saturation",
        "Levels",
        "Curves",
        "Exposure",
        "Gradient Map",
        "Grain",
        "Add Noise",
        "Gaussian Blur",
        "Motion Blur",
        "Invert",
        "Black & White",
        "Color Balance",
    };

    /// <summary>版本 9 才引入的调整 kind。对应 <c>ProjectStore.swift:217</c>。</summary>
    private static readonly string[] Version9Kinds = { "Gaussian Blur", "Motion Blur", "Add Noise" };

    /// <summary>
    /// 校验一个 manifest。全部通过则正常返回，任何一条不通过都抛
    /// <see cref="ProjectException"/>。
    /// </summary>
    /// <param name="manifest">待校验的 manifest。</param>
    /// <exception cref="ProjectException">任一 guard 不通过。</exception>
    public static void Validate(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var version = manifest.Version;

        // G01 :199 —— 包类型标识
        if (manifest.Format != ProjectManifest.FormatId)
        {
            throw Invalid("G01 format");
        }

        // G02 :200 —— 版本范围（1...11）
        if (version < ProjectManifest.MinSupportedVersion || version > ProjectManifest.CurrentVersion)
        {
            throw new ProjectException(ProjectErrorKind.Version, version, "G02 version");
        }

        // G03 :201 —— 目前只支持 sRGB
        if (manifest.ColorSpace != "sRGB")
        {
            throw Invalid("G03 colorSpace");
        }

        // G04 :202-204 —— resolution 缺省为 72（v1），给了就必须在 1...9600 且有限
        if (manifest.Resolution is { } resolution
            && (!(resolution >= 1d && resolution <= 9600d) || !double.IsFinite(resolution)))
        {
            throw Invalid("G04 resolution");
        }

        // G05 :205 —— 画布宽高在 1...maxSide
        if (manifest.Width < 1 || manifest.Width > ImagingLimits.MaxSide
            || manifest.Height < 1 || manifest.Height > ImagingLimits.MaxSide)
        {
            throw new ProjectException(ProjectErrorKind.TooLarge, detail: "G05 canvas");
        }

        // G06 :206 —— 图层数上限
        if (manifest.Layers.Count > MaxLayers)
        {
            throw new ProjectException(ProjectErrorKind.TooLarge, detail: "G06 layer count");
        }

        foreach (var layer in manifest.Layers)
        {
            ValidateLayer(layer, version);
        }

        // G20 :234 —— 图层树
        LayerHierarchyValidator.Validate(manifest.Layers);

        // G21 :235 —— 活蒙版链
        LiveMaskValidator.Validate(manifest.Layers);

        // G22 :236 —— maskSourceID 是 v5 才有的字段
        if (version < 5 && manifest.Layers.Any(l => l.MaskSourceId is not null))
        {
            throw Invalid("G22 maskSourceID needs v5");
        }

        // G23 :237 —— v1 没有分组与父子关系
        if (version == 1 && manifest.Layers.Any(l => l.ParentId is not null || l.IsGroup == true))
        {
            throw Invalid("G23 grouping needs v2+");
        }

        // G24 :238-244 —— id 唯一、transform 合法、名字非空且不超长、imageFile 必须是 "{id}.png"
        var ids = new HashSet<Guid>();
        foreach (var layer in manifest.Layers)
        {
            if (!ids.Add(layer.Id))
            {
                throw Invalid("G24 duplicate layer id");
            }

            if (!IsTransformValid(layer.Transform))
            {
                throw Invalid("G24 transform");
            }

            if (string.IsNullOrWhiteSpace(layer.Name))
            {
                throw Invalid("G24 blank name");
            }

            if (Encoding.UTF8.GetByteCount(layer.Name) > MaxLayerNameBytes)
            {
                throw Invalid("G24 name too long");
            }

            if (layer.ImageFile is { } imageFile && imageFile != ProjectManifest.ImageFileName(layer.Id))
            {
                throw Invalid("G24 imageFile must be {id}.png");
            }
        }

        // G25 :245 —— activeLayerID 必须指向存在的图层
        if (manifest.ActiveLayerId is { } active && !ids.Contains(active))
        {
            throw Invalid("G25 dangling activeLayerID");
        }

        ValidateGuides(manifest);
    }

    /// <summary>
    /// 校验单个图层的版本相关约束。对应 <c>ProjectStore.swift:207-233</c>。
    /// </summary>
    /// <param name="layer">图层记录。</param>
    /// <param name="version">manifest 版本号。</param>
    private static void ValidateLayer(ProjectLayerRecord layer, int version)
    {
        // G07 :208-214 —— 文本元数据：colorRuns 需 v10、fontRuns 需 v11，且必须有像素、非分组、非调整层
        if (layer.Text is { } text)
        {
            if (text["colorRuns"] is not null && version < 10)
            {
                throw Invalid("G07 colorRuns needs v10");
            }

            if (text["fontRuns"] is not null && version < 11)
            {
                throw Invalid("G07 fontRuns needs v11");
            }

            if (layer.ImageFile is null || layer.IsGroup == true || layer.Adjustment is not null)
            {
                throw Invalid("G07 text layer shape");
            }
        }

        // G08 :215-216 —— 调整层：v7 起，且不能是分组、不能有像素
        if (layer.Adjustment is { } adjustment)
        {
            if (version < 7)
            {
                throw Invalid("G08 adjustment needs v7");
            }

            if (layer.IsGroup == true)
            {
                throw Invalid("G08 adjustment cannot be a group");
            }

            if (layer.ImageFile is not null)
            {
                throw Invalid("G08 adjustment cannot carry pixels");
            }

            // G09 :216 —— kind 合法 + 标量区间（深层校验见 IsAdjustmentDeepValid 的说明）
            if (!IsAdjustmentValid(adjustment))
            {
                throw Invalid("G09 adjustment invalid");
            }

            // G10 :217-218 —— 三种调整是 v9 才有的
            if (Array.IndexOf(Version9Kinds, adjustment.Kind) >= 0 && version < 9)
            {
                throw Invalid("G10 adjustment needs v9");
            }
        }

        // G11 :222 —— maskFile 出现时必须版本够（分组 v6、图层 v4）且文件名恰为 "{id}.mask.png"
        if (layer.MaskFile is { } maskFile)
        {
            var requiredVersion = layer.IsGroup == true ? 6 : 4;
            if (version < requiredVersion)
            {
                throw Invalid("G11 maskFile needs v" + requiredVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            if (maskFile != ProjectManifest.MaskFileName(layer.Id))
            {
                throw Invalid("G11 maskFile must be {id}.mask.png");
            }
        }

        // G12 :224 —— maskEnabled 不能脱离 maskFile 存在
        if (layer.MaskEnabled is not null && layer.MaskFile is null)
        {
            throw Invalid("G12 maskEnabled without maskFile");
        }

        // G13 :225 —— maskPlacement 合法且必须配 maskFile
        if (layer.MaskPlacement is { } placement
            && (!IsTransformValid(placement) || layer.MaskFile is null))
        {
            throw Invalid("G13 maskPlacement");
        }

        // G14 :226-232 —— 不透明度与混合模式
        var opacity = layer.Opacity ?? 1d;
        var blend = layer.BlendMode ?? Core.BlendMode.Normal;

        // G14a —— opacity 必须有限且在 0...1
        if (!double.IsFinite(opacity) || opacity < 0d || opacity > 1d)
        {
            throw Invalid("G14a opacity range");
        }

        // G14b —— v3 之前只有默认值是合法的
        if (version < 3 && (opacity != 1d || blend != Core.BlendMode.Normal))
        {
            throw Invalid("G14b opacity/blend needs v3");
        }

        // G14c —— 分组不透传：混合模式恒为 Normal，不透明度从 v8 起才允许非 1
        if (layer.IsGroup == true
            && (blend != Core.BlendMode.Normal || (version < 8 && opacity != 1d)))
        {
            throw Invalid("G14c group pass-through");
        }
    }

    /// <summary>
    /// 参考线校验。对应 <c>ProjectStore.swift:249-262</c> 的 <c>validateGuides</c>。
    /// </summary>
    /// <param name="manifest">待校验的 manifest。</param>
    private static void ValidateGuides(ProjectManifest manifest)
    {
        var guides = manifest.Guides ?? Array.Empty<CanvasGuide>();
        var version = manifest.Version;

        // G26 :251-253 —— v8 之前没有参考线这个概念
        if (version < 8)
        {
            if (guides.Count != 0)
            {
                throw Invalid("G26 guides need v8");
            }

            return;
        }

        // G27 :255 —— 参考线数量上限
        if (guides.Count > MaxGuides)
        {
            throw new ProjectException(ProjectErrorKind.TooLarge, detail: "G27 guide count");
        }

        var ids = new HashSet<Guid>();
        foreach (var guide in guides)
        {
            // G28 :257-260 —— id 唯一、position 有限且在 ±1e6
            if (!ids.Add(guide.Id))
            {
                throw Invalid("G28 duplicate guide id");
            }

            if (!double.IsFinite(guide.Position) || System.Math.Abs(guide.Position) > MaxOriginMagnitude)
            {
                throw Invalid("G28 guide position");
            }
        }
    }

    /// <summary>
    /// 图层变换的合法性判定。
    /// </summary>
    /// <remarks>
    /// <b>不复用 Core 的 <c>LayerTransform.IsValid</c>。</b>Mac 的
    /// <c>LayerTransform.swift:27-31</c> 还包含 <c>(1...300_000).contains(size.width/height)</c>
    /// 三条尺寸检查，Core 版本没有；而 <c>ProjectStore.swift:240</c> 正是靠它强制该上限。
    /// 少这三条，一份 500,000 × 500,000 的图层就会被 Windows 放行、Mac 拒收。
    /// </remarks>
    /// <param name="transform">待判定的变换。</param>
    /// <returns>合法返回 <see langword="true"/>。</returns>
    public static bool IsTransformValid(LayerTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        return transform.Size.Width >= 1
            && transform.Size.Width <= MaxTransformSide
            && transform.Size.Height >= 1
            && transform.Size.Height <= MaxTransformSide
            && double.IsFinite(transform.Origin.X)
            && double.IsFinite(transform.Origin.Y)
            && double.IsFinite(transform.RotationDegrees)
            && System.Math.Abs(transform.Origin.X) <= MaxOriginMagnitude
            && System.Math.Abs(transform.Origin.Y) <= MaxOriginMagnitude;
    }

    /// <summary>
    /// 调整层的 kind 与标量区间校验。
    /// </summary>
    /// <param name="adjustment">待判定的调整层。</param>
    /// <returns>通过返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// 覆盖 Mac <c>LayerAdjustment.swift:128-141</c> 里与 kind 无关、以及能用标量直接判定的部分：
    /// kind 白名单、hue/saturation/lightness、gaussianRadius、motionAngle、motionDistance、noiseAmount。
    /// </remarks>
    public static bool IsAdjustmentValid(LayerAdjustmentRecord adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);

        if (Array.IndexOf(AdjustmentKinds, adjustment.Kind) < 0)
        {
            return false;
        }

        var s = adjustment.Settings;

        // 旧版 HSV 三字段（LayerAdjustment.swift:129）
        if (!IsBounded(s["hue"], 360d) || !IsBounded(s["saturation"], 100d)
            || !IsBounded(s["lightness"], 100d))
        {
            return false;
        }

        // 高斯模糊半径 :137
        if (s["blurRadius"] is JsonNode blurRadius)
        {
            if (!TryDouble(blurRadius, out var r) || r < 0.1d || r > 250d)
            {
                return false;
            }
        }

        // 动感模糊 :138-139
        if (s["motionAngle"] is JsonNode motionAngle
            && (!TryDouble(motionAngle, out var a) || a < -90d || a > 90d))
        {
            return false;
        }

        if (s["motionDistance"] is JsonNode motionDistance
            && (!TryDouble(motionDistance, out var d) || d < 1d || d > 2000d))
        {
            return false;
        }

        // 颗粒噪声 :140
        if (s["noiseAmount"] is JsonNode noiseAmount
            && (!TryDouble(noiseAmount, out var n) || n < 0.1d || n > 400d))
        {
            return false;
        }

        return true;
    }

    private static bool IsBounded(JsonNode? node, double limit)
    {
        if (node is null)
        {
            return true;
        }

        return TryDouble(node, out var v) && System.Math.Abs(v) <= limit;
    }

    private static bool TryDouble(JsonNode node, out double value)
    {
        try
        {
            value = node.GetValue<double>();
            return double.IsFinite(value);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            value = 0d;
            return false;
        }
    }

    private static ProjectException Invalid(string detail) =>
        new(ProjectErrorKind.Invalid, detail: detail);
}