namespace Compositor.Project;

/// <summary>
/// 图层树结构校验。逐字对照 macOS 版 <c>Document/LayerGroups.swift:30-46</c> 的
/// <c>LayerHierarchy.validate</c>。
/// </summary>
/// <remarks>
/// 防的是两类会让渲染器出事的结构：<b>环</b>（A 的父是 B、B 的父是 A）与
/// <b>指向非分组的父</b>（普通图层不能有父级）。两者都会让递归遍历不终止或渲染错位。
/// 嵌套深度上限 64 是 Mac 侧的显式闸门，本移植照搬。
/// </remarks>
public static class LayerHierarchyValidator
{
    /// <summary>父链长度上限。对应 <c>LayerGroups.swift:40,44</c> 的 <c>seen.count &lt;= 64</c>。</summary>
    public const int MaxDepth = 64;

    /// <summary>校验图层树。</summary>
    /// <param name="layers">manifest 中的全部图层记录。</param>
    /// <exception cref="ProjectException">
    /// id 重复、分组带像素、父链成环、父级不存在、父级不是分组，或嵌套超深。
    /// </exception>
    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        // :31-35 —— 建立 id → 记录 的索引，重复 id 直接拒；分组不得带 imageFile
        var byId = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (!byId.TryAdd(layer.Id, layer))
            {
                throw new ProjectException(ProjectErrorKind.Invalid, detail: "hierarchy: duplicate id");
            }

            if (layer.IsGroup == true && layer.ImageFile is not null)
            {
                throw new ProjectException(ProjectErrorKind.Invalid, detail: "hierarchy: group carries pixels");
            }
        }

        foreach (var layer in layers)
        {
            // :37-43 —— 沿父链上行，seen 起点含自己；超深 / 成环 / 父不存在 / 父非分组都拒
            var seen = new HashSet<Guid> { layer.Id };
            var parent = layer.ParentId;

            while (parent is { } parentId)
            {
                if (seen.Count > MaxDepth || !seen.Add(parentId))
                {
                    throw new ProjectException(ProjectErrorKind.Invalid, detail: "hierarchy: depth or cycle");
                }

                if (!byId.TryGetValue(parentId, out var node) || node.IsGroup != true)
                {
                    throw new ProjectException(ProjectErrorKind.Invalid, detail: "hierarchy: bad parent");
                }

                parent = node.ParentId;
            }

            // :44 —— 分组自身也受 64 层上限约束
            if (layer.IsGroup == true && seen.Count > MaxDepth)
            {
                throw new ProjectException(ProjectErrorKind.Invalid, detail: "hierarchy: group too deep");
            }
        }
    }
}

/// <summary>
/// 活蒙版链校验。逐字对照 macOS 版 <c>Document/LiveLayerMask.swift:4-19</c> 的
/// <c>LiveMaskGraph.validate</c>。
/// </summary>
/// <remarks>
/// 活蒙版是 <c>maskSourceID</c> 构成的有向链：A 的蒙版来自 B、B 的蒙版来自 C。
/// 本校验拒掉：id 重复、成环（链长上限 256）、链中断（来源不存在）、
/// 分组做链上节点、分组做来源、调整层做来源。
/// </remarks>
public static class LiveMaskValidator
{
    /// <summary>蒙版链长度上限。对应 <c>LiveLayerMask.swift:12</c> 的 <c>path.count &lt; 256</c>。</summary>
    public const int MaxChainLength = 256;

    /// <summary>校验活蒙版链。</summary>
    /// <param name="layers">manifest 中的全部图层记录。</param>
    /// <exception cref="ProjectException">
    /// id 重复，或蒙版链成环、超长、来源不存在、来源是分组或调整层。
    /// </exception>
    public static void Validate(IReadOnlyList<ProjectLayerRecord> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        // :5-8 —— 建立索引，重复 id 拒
        var records = new Dictionary<Guid, ProjectLayerRecord>();
        foreach (var layer in layers)
        {
            if (!records.TryAdd(layer.Id, layer))
            {
                throw new ProjectException(ProjectErrorKind.Invalid, detail: "live mask: duplicate id");
            }
        }

        // :9-17 —— 从每个图层出发，沿 maskSourceID 走到底
        foreach (var layer in layers)
        {
            var path = new HashSet<Guid>();
            Guid? current = layer.Id;

            while (current is { } currentId)
            {
                // :12 —— 链长上限 + 不重复 + 记录必须存在
                if (path.Count >= MaxChainLength || !path.Add(currentId))
                {
                    throw new ProjectException(ProjectErrorKind.Invalid, detail: "live mask: cycle or length");
                }

                if (!records.TryGetValue(currentId, out var record))
                {
                    throw new ProjectException(ProjectErrorKind.Invalid, detail: "live mask: dangling chain");
                }

                if (record.MaskSourceId is { } source)
                {
                    // :14 —— 本节点不能是分组；来源必须存在、不是分组、不是调整层
                    if (record.IsGroup == true
                        || !records.TryGetValue(source, out var sourceRecord)
                        || sourceRecord.IsGroup == true
                        || sourceRecord.Adjustment is not null)
                    {
                        throw new ProjectException(ProjectErrorKind.Invalid, detail: "live mask: bad source");
                    }
                }

                current = record.MaskSourceId;
            }
        }
    }
}