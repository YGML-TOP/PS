# 调整层实现要点（M6）

> 技术文档，随代码进 git。交付报告在 `文档\AI5\ai5-01-M6调整层框架交付报告.md`。

## 一、manifest 字面量 ≠ C# 枚举名

`AdjustmentKind` 的 12 个枚举成员里，**两个的字面量含 C# 标识符不允许的字符**：

| 枚举成员 | 字面量 | 陷阱 |
|---|---|---|
| `HueSaturation` | `"Hue/Saturation"` | 漏斜杠 → 漏斜杠的写法 `HueSaturation` 看起来完全合理 |
| `BlackWhite` | `"Black & White"` | 漏 `&` 号 |

**写错的后果不是报错，是静默失效**：Mac 版 `Codable` 解不出该 kind，调整层被丢弃或整层失效，工程照样能打开。

双来源确认：`Document/LayerAdjustment.swift:5,9` 与 `docs/project-format.md:29`。

`&` 与 `/` 在 JSON 与 Markdown 中**都不转义**，原样输出。

## 二、🔴 两种 LUT 布局，混用即串通道

这是本模块最容易复发的坑。**同一个 768 项的表，两种 API 要求相反的布局。**

| API | 布局 | 索引方式 |
|---|---|---|
| `AdjustPixels.GradientMap` | **每项连续 3 字节**：`[r,g,b,r,g,b,…]` | `color = level * 3`，读 `[color]`/`[color+1]`/`[color+2]` |
| `LevelsPixels.Apply` | **R/G/B 各一段**：`[r×256, g×256, b×256]` | `table[channel*256 + i]` |

`AdjustPixels.cs:74-78` vs `LevelsPixels.cs:30-40`。

建错 GradientMap 的表现：128 灰被染成纯黑而不是半红。

## 三、🔴 LevelsPixels 的 LUT 量纲是 0…1，不是 0…255

原 C 写的是 `fminf(alpha, roundf(result*alpha))`，只有 `result` 落在 0…1 时才成立。传 0…255 的表**不是"效果更强"，是"全屏过曝"**。

三个构造 LUT 的地方（`ExposureSettings` / `LevelsSettings` / `CurvesSettings`）都必须除以 255。

## 四、缺省值：调整层 10，滤镜菜单 1

`LayerAdjustment.gaussianRadius` = `blurRadius ?? 10`（`LayerAdjustment.swift:93`）
`FilterSettings.radius` = `1`（`Filters.swift:43`）

**两个入口刻意不同，照抄不统一。** 统一会导致"新建调整层"与"从滤镜菜单加高斯模糊"效果差 10 倍。

## 五、`isValid` 串联所有子结构，不按 kind 分支

`LayerAdjustment.swift:128-141` 用 `&&` 串起**全部**子结构校验，无论当前 `kind` 是什么。理由：调整层可在两种 kind 间切换，此前留下的设置仍须合法。

## 六、序列化：只写非缺省的键

对应 Swift `encodeIfPresent`。写 `blurRadius: null` 与不写**对 Mac 版等价**，读到 null 须走 `?? 10` 而不是报错。

大量字段可选的原因见 `LayerAdjustment.swift:51,58,64` 的注释：这些调整出现之前保存过的工程必须仍能原样解出。

## 七、色彩空间

工作域 = **编码 sRGB**（铁律 3 裁决）。唯一例外：`ExposureSettings.BuildTable` 内部解码到线性再编码回来（`ImageAdjustments.swift:57-58`），与 `docs/color-space.md` 的结论一致。

## 八、已知差异

- **HSL 换算有两份**：`AdjustPixels.RgbToHsl` / `HslToRgb` 是 `private`，`Compositor.Adjust` 内另有一份同数学实现。改动其中一边会造成静默分歧。
- **Invert 用逐像素展开**而非 vImage 向量化，矩阵相同、数学等价、性能不等价。
- **11 个混合模式确定与 W3C 公式不等价**（Mac 版走闭源 CoreImage），13 个走闭源 CoreGraphics 无法验证。M7 合并时会用到，届时须重述。
