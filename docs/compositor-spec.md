# 合成器规格（Compositor macOS → Windows）

> 面向 `CompositorWindows`。本文是**从 Swift 源码提取的行为规格**，供 AI-1 写合成器、AI-2 写
> `.comp` 读写、AI-3~AI-6 接入各自层时共用。
>
> **核实状态**：标 ✅ 的段落由 AI-1 亲自回源码逐行核对；标 📄 的段落来自只读子代理提取，
> 已抽查行号但未逐字复核。**两类都不要当权威直接抄——引用前回源码看一眼。**

---

## 0. 已确证的三条铁律（本文的前提）

1. **文档坐标系 = 左上原点、Y 向下**。`DocCoord` 是唯一的视图↔文档边界。
2. **像素全程预乘 alpha 的 RGBA8**，stride = `width * 4`。灰度蒙版无 alpha，stride = `width`。
   ✅ `Rendering/BrushStroke.swift:36-42`（📄）：所有中间 surface 的统一契约，
   `premultipliedLast | byteOrder32Big`，建好后立刻 `translateBy(0, height)` + `scaleBy(1,-1)` 翻成 top-left。
3. **混合在编码 sRGB 空间计算**，仅 Camera Raw 路径进线性。理由见 §6.2。

---

## 1. 图层数组与遍历顺序

### 1.1 索引 0 = 最底层 ✅

```swift
// Document/EditorSession.swift:67
var layers: [ImageLayer] = [] // Bottom to top.
```

合成循环**不反向迭代**，直接把绘制序喂进去。面板顺序相反（`LayerGroups.swift:242`
`topFirst: true`）——**只有面板是顶在前，存储是底在前**，搞反会整个渲染颠倒。

### 1.2 绘制序 = `renderLayers` ✅/📄

= `LayerOrder.resolve(layers).drawn`（`LayerGroups.swift:90-95`）：

- 递归深度优先，**先 group 自身、再其内容**（`LayerGroups.swift:126-133`）
- 同一 `parentID` 内按数组下标**升序**（`:122` 的 `Dictionary(grouping:)` 保序 + `:126`）
- 只收**非 group 且 `shown && isVisible`** 的节点（`:131-132`）
- 入口 `visit(nil, depth: 0, shown: true)`（`:136`）

⚠️ **group 不可见 ⇒ 子孙根本不进 `drawn`，一个像素都不画**（`shown: effective` 递归传递）。

### 1.3 两条同构的合成入口 📄

| 入口 | 位置 | 用途 |
|---|---|---|
| `drawLiveComposite` | `Document/LiveLayerMask.swift:159` | **离线渲染**（导出、⌘E 合并）—— 规格以这条为准 |
| `drawLayers` | `Rendering/EditorCanvas.swift:948` | 画布交互预览，分支多得多（`:1005-1149` 共 10 个） |

两条**复用同一个 `LiveMaskRenderer` + `FolderMaskClip`**，核心语义一致。差异仅在预览分支
（笔刷/渐变/变形/文本草稿等）。**复刻编辑期预览需单独提取，不要拿离线路径顶替。**

---

## 2. 有效不透明度：文件夹是 pass-through ✅

### 2.1 算法 ✅

```swift
// Document/LayerGroups.swift:53-63
static func effective(_ own: Double, parent: UUID?,
                      folder: (UUID) -> (opacity: Double, parentID: UUID?)?) -> Double {
    var opacity = own, id = parent, depth = 0
    while let current = id, depth < 64, let node = folder(current) {
        opacity *= node.opacity     // 逐级相乘
        id = node.parentID
        depth += 1
    }
    return opacity
}
```

即 `own × 父.opacity × 祖父.opacity × ...`，**深度上限 64，超出即静默截断，不报错**。

### 2.2 设计意图（源码注释原文）✅

> `LayerGroups.swift:49-52`：
> "A folder's opacity multiplies into everything inside it: a layer at 50% in a folder at 50%
> shows at 25%, while the layer itself still reads 50% in the panel.
> **Folders are pass-through — what's inside is drawn straight onto what is below, never
> composited as a unit** — so the folder's opacity is applied to each of those layers
> rather than to the folder as a whole."

`LayerMask.swift:168-171` 独立复述了同一句：**folder 永不作为独立 surface 合成**。

### 2.3 `effectiveOpacity` 什么都不跳过 ✅

它**不**检查 `isVisible`、**不**看 `maskSourceID`、**不**区分调整层、**不** clamp、
**不**因 opacity 已为 0 而提前 break。剔除全部发生在别处：

| 剔除什么 | 在哪 |
|---|---|
| 自身或祖先不可见 | `LayerGroups.swift:127-132`（不进 `drawn`） |
| group 自身（无像素） | `LayerGroups.swift:131` |
| group 的 blendMode | `LayerAppearance.swift:86-88`（blending 归每个层，group 上关闭） |
| 栈内 child 的顶层绘制 | `LiveMaskRenderer.swift:94` `guard !stacked.contains(id)` |
| 被剪贴的调整层 | `LiveMaskRenderer.swift:95-98` `if source(id) == nil { adjust(...) }` |

⚠️ **folder opacity = 0 的子层仍会被绘制**（只是 effective 为 0），不提前剔除。

---

## 3. 剪贴蒙版：两套机制（本项目最容易做错的地方）✅

### 3.0 先分清三个概念 📄

| 名字 | 载体字段 | 含义 |
|---|---|---|
| **剪贴链接**（UI 一律叫 "Clipping Mask"） | `maskSourceID` | 被剪贴层指向 **base 图层 id** |
| **栅格图层蒙版** | `maskFile` + `maskEnabled` + `maskPlacement` + `maskLinked` | 图层自身的灰度蒙版 |
| **"live mask"** | —— | ⚠️ **不是第三种东西**，是剪贴机制在代码里的内部叫法（`LiveLayerMask.swift` / `LiveMaskRenderer`） |

✅ 已核实 `ProjectStore.swift:43-50` 五个字段**并列**且互不相关：

```swift
var maskFile: String? = nil
var maskEnabled: Bool? = nil
var maskSourceID: UUID? = nil          // ← 剪贴
/// A mask moved apart from its layer: where it sits on the document.
var maskPlacement: LayerTransform? = nil
/// Nil (older projects) is linked.
var maskLinked: Bool? = nil
```

**`maskSourceID` 与栅格蒙版字段族完全独立，不共用。** 混淆这两族会导致整个蒙版体系写错。

### 3.1 源码自己写明的两套机制 ✅

> `Rendering/LiveMaskRenderer.swift:76-77` 原文注释：
> "**Clipping stacks share the base's alpha instead of painting that alpha over itself.
> Other dependency links retain independent-mask behavior.**"

**这是源码写明的，不是推断。** 两种情况产生**不同的像素结果**。

### 3.2 机制 A —— 连续栈 ✅

**成栈条件**（`LiveMaskRenderer.swift:81-91`）：

```swift
for (index, base) in ids.enumerated() where source(base) == nil && adjustment(base) == nil {
    var children: [UUID] = []
    for child in ids.dropFirst(index + 1) {
        guard source(child) == base, parent(child) == parent(base) else { break }   // :84 一断即停
        children.append(child)
    }
    guard !children.isEmpty else { continue }
    stacks[base] = children
    stackModes[base] = blend(base)     // 整栈用 base 的混合模式
    stacked.formUnion(children)        // child 不再顶层单独绘制
}
```

- base 必须**无剪贴链接**且**非调整层**（`:81`）⇒ 数据层面杜绝"被剪贴的层再当 base"
- **必须连续**：同一 `parentID` + 紧邻 + 同 source，遇异即 `break`
- **同父才成栈**（`:84`）——子层和 base 进了不同分组就退化成机制 B，**像素结果不同**

**栈的合成**（`LiveMaskRenderer.swift:105-116`，AI-1 逐行核对）：

```swift
group.scaleBy(x: resolution, y: resolution)
group.translateBy(x: -bounds.minX, y: -bounds.minY)
drawOwn(id, group)                                      // ① 先画 base（用 drawOwn，不走 draw）
layer_extract_alpha(pixels, …, coverage, …)             // ② 抽 base 的 alpha 存为 coverage
layer_unpremultiply_opaque(pixels, …)                   // ③ base 整体变不透明（空洞处变不透明黑）
for child in children {                                 // ④ 子层照常叠上，各用各的混合模式
    if adjustment(child) != nil { adjust(child, in: group) }
    else { drawOwn(child, group) }
}
layer_restore_alpha(pixels, …, coverage, …)             // ⑤ RGB 按 coverage 重新预乘、alpha 写回 coverage
```

**净效果：多个半透明子层不会把 base 的边缘叠厚。** 这与"C# 常见的逐层 alpha 求交"
是**不同的像素结果**，不是同一件事的两种写法。

**混合模式归属** ✅/📄：栈内子层之间用**各自**的模式；**整栈与下方合成用 base 的**
（`:89` `stackModes[base] = blend(base)`，`:128` 用 `stackModes[id]`）。

⚠️ `drawOwn(base, group)` 用的是 `drawOwn` 而非 `draw`，所以 **base 自身的剪贴链接在栈内不被施加**
（`LiveMaskRenderer.swift:107` vs `:143`）。📄 声称这是有意的，但**未见显式注释说明**，存疑。

### 3.3 机制 B —— 非连续链接 📄

`LiveMaskRenderer.swift:143-152`：`source(id) != nil` 时取 `coverage(sourceID)`，
对 `bounds` 做 `context.clip(to:mask:)`，再 `drawOwn(id, context)`。

- **coverage 为 nil ⇒ 整层不画**（`:144`），**不是"不裁剪"**
- coverage 与 source 的**可见性和颜色无关**，但**包含 source 自己的栅格蒙版**
  （`LiveMaskRenderer.swift:5-6` 类注释）
- coverage 递归调用 `draw`（`:162`），带 `cache` 记忆化与 `visiting` 防环（`:156-157`，深度 < 256）

### 3.4 约束与回退 ✅/📄

- **链长 < 256** 防环（`LiveLayerMask.swift:12`），`path.insert(id).inserted` 拒绝自引用
- **分组既不能被剪贴、也不能当 base**（`LiveLayerGraph.validate:14`、`canLinkMask:26`）
- **调整层既不能被剪贴、也不能当 base**，但**可以作为栈内子层**（`LiveMaskRenderer.swift:81,96,113`）
- 栈离屏 surface 分配失败 ⇒ 退回逐层 `draw`，coverage 裁剪仍生效（`:102-103`）
- **UI 永不产生链式剪贴**：`linkMask` 归一到 `below.maskSourceID ?? below.id`
  （`LiveLayerMask.swift:211,222`）。但数据模型**允许**链式，`coverage` 能递归处理

---

## 4. 分组（folder）语义 ✅/📄

- **pass-through，永不作为独立 surface 合成**（§2.2）
- 不透明度**逐级相乘**，深度上限 64（`LayerGroups.swift:57`）
- **blendMode 被忽略**，blending 归每个层（`LayerAppearance.swift:86-88`）
- **必须无像素**：`isGroup == true` 的记录不得有 `imageFile`（`LayerGroups.swift:34`）
- **group 蒙版是祖先链上的乘子**：`FolderMaskClip.draw`（`LayerMask.swift:191-209`）从
  `parent(id)` 上溯，每层有启用蒙版就再 `context.clip` 一次，深度上限 64（`:197`）
- **进出分组会重写剪贴关系**：`releaseDetachedClipping` 按 `parentID` 分桶各自重算 base
  （`LiveLayerMask.swift:226-235`）

> `EditorCanvas.swift:516` 注释 "Moving a layer into or out of a masked folder changes how it
> is clipped." 📄 对应三条代码事实（同父要求 / 祖先链 / 按父分桶），但**注释作者具体指哪条不确定**。

---

## 5. 有效蒙版 = 三重交集 📄

一个图层最终被三样东西裁剪，靠 CGContext 的 clip 逐层相乘：

1. **自身栅格蒙版** — `LayerRenderer.swift:24` `context.clip(to:…, mask: clip.image)`
2. **剪贴 coverage** — `LiveMaskRenderer.swift:148`
3. **所有祖先分组的蒙版** — `FolderMaskClip.draw` 沿 parent 链（`LayerMask.swift:191-209`）

⚠️ 栅格蒙版**禁用时是完全不裁、全画**，不是画成全黑（`LayerMask.swift:111` 返回 nil）。

---

## 6. 混合模式路由 ✅

### 6.1 二元门 ✅

```swift
// Rendering/SeparableBlend.swift:12
static func needsSurface(_ mode: LayerBlendMode) -> Bool { mode.coreImageFilter != nil }
```

**没有第三条路。** Core Graphics 与 Core Image **都是 macOS 专有**，
所以 **Windows 端 24 个模式全部要手写逐通道数学**，不存在复用系统 API 的可能。

| 组 | 数量 | 名单 | 依据强度 |
|---|---|---|---|
| **A：Core Graphics 直接绘制** | 13 | normal, darken, multiply, lighten, screen, overlay, hardLight, difference, exclusion, hue, saturation, color, luminosity | **高** — `CGBlendMode` 算术是公开标准（W3C Compositing and Blending L1 §10） |
| **B：Core Image filter** | 11 | colorBurn, colorDodge, softLight, linearBurn, linearDodge, vividLight, linearLight, pinLight, hardMix, subtract, divide | **中高** — 公式同名且公开，但 **CI filter 闭源** |

⚠️ `cgMode` 里有 **3 个值是死代码**：`colorBurn` / `colorDodge` / `softLight` 同时有
`cgMode` 与 `coreImageFilter`，而门是 `needsSurface`（后者优先），其 CG 值永不被读到。
另有 8 个（`LayerAppearance.swift:47`）显式返回 `.normal`，同样从不使用。

### 6.2 工作空间 = 编码 sRGB ✅

> `SeparableBlend.swift:14-17` 原文：
> "Core Image works in a linear space unless told otherwise, and **these two modes are not
> separable from the gamma they are computed in**: over 40% grey, an 80% grey layer dodges to
> **62% instead of Photoshop's 100%**, and burns to **0% instead of 25%**. The blend has to
> happen in the same sRGB the canvas is in."

```swift
private static let space = CGColorSpace(name: CGColorSpace.sRGB)!              // :13
private static let ciContext = CIContext(options: [.cacheIntermediates: false,
                                                   .workingColorSpace: space]) // :17
```

**「编码 sRGB」不是我推的，是 Mac 版用注释 + 常量钉死的，且附了可验证的错值。**
Color Dodge / Color Burn 是最容易被"看着合理就按线性算"毁掉的两个。

### 6.3 Soft Light 走 Core Image 是有理由的 ✅

> `LayerAppearance.swift:51-53` 原文：
> "…or **computes wrongly**, as it does for Color Burn and Color Dodge, and for **Soft Light,
> whose formula is up to 25 levels off Photoshop's with a light blend color (Core Image's is
> within 5)**."

即 CG 画得出 Soft Light 但**故意不用**。⚠️ Windows 端没有"25 级误差"这个量化基准，
只能按 W3C 标准的 soft-light 公式实现，差异须列入「已知差异」。

### 6.4 三级分支 📄

1. **入口短路**（`LiveLayerMask.swift:160-163` / `EditorCanvas.swift:977-986`）：
   文档含**任一**调整层或**任一** `needsSurface` 模式 ⇒ 整份文档先渲染进离屏 surface
2. **单层分支**（`LiveLayerMask.swift:183`）：该层走 surface 混合，body 内强制 `.normal` 画
3. **栈分支**（`LiveMaskRenderer.swift:129-134`）：整栈以 base 的模式走 `SeparableBlend`，失败回落 CG

---

## 7. 像素摆放与缓冲布局 📄

**所有中间 surface 的统一契约**（`BrushStroke.swift:36-42`）：

| 参数 | RGBA | 灰度蒙版 |
|---|---|---|
| `bytesPerRow` | `width * 4` | `width * 1` |
| 色彩空间 | `CGColorSpace.sRGB` | `CGColorSpaceCreateDeviceGray()` |
| `bitmapInfo` | `premultipliedLast \| byteOrder32Big` | `none`（无 alpha 通道） |

建好后立刻 `translateBy(0, height)` + `scaleBy(1, -1)` 翻成 top-left（`:41-42`）。

**图层摆放**（`LayerRenderer.swift:5-27`）变换顺序固定：

```
setAlpha(opacity)                    // 不透明度 = 整体 alpha
setBlendMode(blendMode.cgMode)       // 混合交给 CG
setInterpolationQuality(...)
setShouldAntialias(sampling != .nearest)
translate(center.x, center.y)        // ① 平移到中心
rotate(transform.radians)            // ② 旋转
scale(flipX ? -1 : 1, flipY ? 1 : -1) // ③ 翻转（y 翻转含坐标系转换）
draw(image, in: centeredBounds)
```

---

## 8. 加载期不变量门 📄

`ProjectStore.validate` 调用顺序（`ProjectStore.swift:198-247`）：

```
字段级逐层校验 :207-233
→ LayerHierarchy.validate :234      // 管树（parentID）
→ LiveMaskGraph.validate   :235      // 管剪贴函数图（maskSourceID）
→ v5 剪贴门禁            :236
→ v1 门禁                :237
→ id 唯一 / transform 有效 / 名称非空 / 文件名规范 :238-244
→ activeLayerID 必须存在 :245
→ guides                 :246
```

⚠️ **v5 门禁排在 `LiveMaskGraph.validate` 之后** ⇒ 一个 v4 工程里的非法剪贴图会先以
`LiveMaskGraph` 的 `.invalid` 失败，而非版本错误。

**`LayerHierarchy.validate`（`LayerGroups.swift:30-46`）**：id 唯一 / group 不得有 `imageFile` /
parent 链全存在且都是 group / 无环 / 深度 ≤ 64。

**`LiveMaskGraph.validate`（`LiveLayerMask.swift:3-20`）**：id 唯一 / 链长 < 256 / 无环（含自引用）/
source 存在 / target 非 group / source 非 group / source 非调整层。
**它不检查** `parentID` 关系、Z 序、可见性——那三样在别处强制。

---

## 9. `.comp` 版本沿革 📄

当前版本 = **11**，接受 1…11（`ProjectStore.swift:15,18`）。

| 版本 | 引入/约束 | 行号 |
|---|---|---|
| v1 | 不得有 `parentID` / `isGroup` | `:237` |
| **v2** | **不可考** —— 全仓无任何针对 version 2 的门禁 | — |
| v3 | 图层可有 `opacity` / `blendMode` | `:231` |
| v4 | 图层栅格蒙版 | `:221-223` |
| v5 | **剪贴蒙版 `maskSourceID`** | `:236` |
| v6 | 分组蒙版 | `:221-223` |
| v7 | 调整图层 | `:216` |
| v8 | 分组独立 opacity（仍强制 blend == normal）；`guides` 必须为空 | `:232,228,251-253` |
| v9 | `gaussianBlur` / `motionBlur` / `addNoise` | `:217-219` |
| v10 | 文字逐字颜色 `colorRuns` | `:209-213` |
| v11 | 文字逐字字体 `fontRuns` | `:209-213` |

`maskFile` 名必须严格等于 `"\(layer.id.uuidString).mask.png"`（`:223`），`imageFile` 同理（`:243`）。
`maskPlacement` 只校验 `isValid` 且有 `maskFile`，**没有版本门禁**（`:225`）——
其引入版本在代码里查不到。

---

## 10. macOS 专有依赖清单 ✅/📄

| 框架 | 承担什么 | 备注 |
|---|---|---|
| **Core Graphics** | 混合模式实际执行、`setAlpha`、mask `clip(to:mask:)`、图元 `draw(image,in:)`、重采样 `interpolationQuality`、抗锯齿、预乘 RGBA8 位图上下文 | **公式与 Photoshop 不符**（`LayerAppearance.swift:51-53`） |
| **Core Image** | 11 种混合的 filter、强制 sRGB 工作空间、调整层 `CIBlendWithMask`、缩略图 | **filter 闭源** |
| **Accelerate** | `DownsampleCache.swift` 的降采样 halving 缓存 | |
| **Metal** | `GPUCanvas.swift` / `MetalLayerEffects` / `MetalWarp` / `MetalBrushCoverage` | ⚠️ **与 CPU 路径并行的第二套完整合成实现**，入口 `EditorCanvas.swift:865` |
| **AppKit** | 视图/事件/窗口 | 桌面实现需自建 |

**平台无关的硬限制 📄**：`DocumentLimits.maxSurfaceExtent`、`maxSurfacePixels`（具体数值未读）。

---

## 11. 可直接复用的 C 内核（已直译）✅

合成路径直接调用的三个算子，在 **`Rendering/BrushPixels.c:23-49`**，
**波次 1-A 直译时已全部实现**：

| C 函数 | C# | 行号 |
|---|---|---|
| `layer_unpremultiply_opaque` | `BrushPixels.UnpremultiplyOpaque` | `BrushPixels.c:23-35` |
| `layer_restore_alpha` | `BrushPixels.RestoreAlpha` | `BrushPixels.c:36-45` |
| `layer_extract_alpha` | `BrushPixels.ExtractAlpha` | `BrushPixels.c:46-49` |

⚠️ 注意 `layer_unpremultiply_opaque` 在 `a == 0` 时把 RGB 置 0、alpha 置 255
（**不透明黑**）——这是剪贴栈空洞处变不透明黑的原因，不是 bug。

> ⚠️ 派子代理提规格时曾把这两个函数指到 `AdjustPixels.c` 并列为「C# 头号阻塞项」，
> **实际早已存在**。子代理说"缺失" ≠ 真缺失，务必自己 grep 确认。

---

## 12. 未决 / 不确定项 📄

1. **`.comp` v2 引入了什么——不可考。** 全仓无任何能区分 v1/v2 的门禁。**不要臆测。**
2. **`maskPlacement` / `maskLinked` 的引入版本无法确定。** 无版本门禁、无 changelog。
3. **`drawOwn(base, group)` 不施加 base 自身剪贴链接**（§3.2）—— 见到过代码，但**未见注释说明是否有意**。
4. **GPU 路径与 CPU 路径是否逐像素等价，未验证。** `EditorCanvas.swift:2687-3110` 有一套独立实现，
   `:2694-2703` 与 `prepareStacks` 逐行等价，但后续 children 收集只读到片段。
5. **链式剪贴的真实可达路径未验证。** validate 允许、渲染器能递归，但所有 UI 入口都归一到 base。
6. **`LiveMaskBaker.bake` 的 `inverse` 变换精确含义**（`LiveLayerMask.swift:79`）是推断，未见显式注释。
7. **编辑期画布用 `displayed*`（含草稿），导出用存储值。** 📄 `LiveLayerMask.swift:168-169` vs
   `ImageExporter.swift:43` —— 两者**可能产生不同像素**，需在实现前定案。
8. **11 个 Core Image 模式的实际算术不可知。** 闭源。差异须逐条列入「已知差异」。

---

## 13. 相关文档

- `docs/color-space.md` — 色彩空间推导（⚠️ 未经 Mac 实测标定）
- `docs/nan-reachability.md` — NaN 可达性穷举排查（结论：不可达）
- `文档/AI1/ai1-01` ~ `ai1-03` — AI-1 交付报告
