# 色彩空间标定结论

> # ⚠️ 未经 Mac 实测标定
>
> **本文件的所有结论均来自对原项目 macOS 源码的静态核实，没有任何一条经过实跑验证。**
>
> 我们没有 Mac 电脑，无法运行原版 Compositor 产出标定图。下列结论是**源码证据推导出的最可能答案**，
> 不是实测答案。补充通知 §四 要求「选定方案 + 标注未经实测标定」，本文件即按此交付。

| | |
|---|---|
| **结论** | 混合模式应工作在**编码 sRGB（非线性）** |
| **建议枚举值** | `ColorSpace.Srgb` |
| **与补充通知 §四 的分歧** | ⚠️ **结论相反，见 §三** |
| **是否已通知项目负责人** | ✅ 是（依 §四 第 4 条，因结论为 `Srgb` 而非 `LinearSrgb`） |
| **待补验证** | Tier 2 · GitHub Actions `macos-26` runner |

---

## 一、结论与证据

### 1.1 决定性证据（源码原文）

原项目 `Compositor/Rendering/SeparableBlend.swift:14-17`：

```swift
// Core Image works in a linear space unless told otherwise, and these two modes are not separable from
// the gamma they are computed in: over 40% grey, an 80% grey layer dodges to 62% instead of Photoshop's
// 100%, and burns to 0% instead of 25%. The blend has to happen in the same sRGB the canvas is in.
private static let ciContext = CIContext(options: [.cacheIntermediates: false, .workingColorSpace: space])
```

其中 `space` 的定义在同文件 L12：

```swift
private static let space = CGColorSpace(name: CGColorSpace.sRGB)!
```

**这段注释给出了三件事：**

1. **Core Image 的默认工作空间是线性的** —— 原文第一句 "Core Image works in a linear space unless told otherwise"
2. **线性空间是错的** —— Color Dodge 在 40% 灰底 + 80% 灰图层上会算成 **62%**，而 Photoshop 是 **100%**；Color Burn 会算成 **0%**，而 Photoshop 是 **25%**
3. **原项目把工作空间强制改回编码 sRGB** —— `.workingColorSpace: space`，即 `CGColorSpace.sRGB`

### 1.2 release notes 的佐证

原项目 release notes 原文：

> "Color Dodge and Color Burn were blending in the wrong color space"

对应的修复就是上面那段 `.workingColorSpace` 覆盖。

**即：Mac 版当年修的 bug，恰恰是「从 Core Image 的默认线性空间，改回编码 sRGB」。**

---

## 二、混合模式根本不在 C 层（这改变了任务性质）

### 2.1 实测结果

对 `Compositor/` 下全部 **19 个 `.c` / `.h` 文件**做 `CGBlendMode|CIColor\w*BlendMode|blend\w*\(` 检索：

> **零命中。9 个 C 文件里没有任何混合模式实现。**

### 2.2 混合模式的真实实现位置

| 路径 | 内容 |
|---|---|
| `LayerAppearance.swift:28-49` | `var cgMode: CGBlendMode` —— Core Graphics 直接能画的 17 种 |
| `LayerAppearance.swift:54-69` | `var coreImageFilter: String?` —— Core Image 滤镜名，11 种 |
| `SeparableBlend.swift` | 把图层画进副本后用 Core Image 混合再贴回 |
| `LayerRenderer.swift` / `TiledLayerRenderer.swift` / `GPUCanvas.swift` | 合成器调度 |

### 2.3 由此产生的三条结论

1. **波次 1-A 的直译不涉及铁律 3。** 我直译的 2284 行 C 是像素调整、抖动、修复、魔棒、填充、色阶、笔刷、噪点、畸变 —— 里面**一个混合模式都没有**。
2. **Windows 端没有 Core Graphics / Core Image**，所以混合模式必须**从零手写**，不是直译。手写代码没有 Mac 版可逐行对照，这正是铁律 3 存在的意义。
3. **手写混合模式时，工作空间选错不会「差一点」，会错到 62% vs 100% 这种量级**（见 §1.1 的实测数字）。所以这一条必须一开始就定对，不能靠调参补。

---

## 三、与补充通知 §四 的分歧（须知会项目负责人）

补充通知 §四 写：

> 1. 按 **扩展线性 sRGB** 实现（CoreImage 的默认行为，理论上最接近 Mac 版）

**这条的前提与源码证据相反：**

| | 补充通知 §四 的说法 | 源码证据 |
|---|---|---|
| Core Image 默认空间 | 线性 | ✅ 线性（`SeparableBlend.swift:14` 确认） |
| 哪个最接近 Mac 版 | 线性 | ❌ **编码 sRGB**。Mac 版**主动把默认线性改掉了** |
| 依据 | 「理论上最接近」 | Mac 版注释给出了线性下的具体错值：62% vs 100%、0% vs 25% |

**为什么这个分歧值得当面说清**：补充通知把「CoreImage 的默认行为」当成了「最接近 Mac 版」的依据。
但 Mac 版正是因为**不**用 CoreImage 默认行为，才修掉了那个 bug。默认行为恰恰是被否定的那一个。

### 建议动作

- `ColorSpace` 枚举先保留 `Srgb = 0` 与 `LinearSrgb = 1`（序列化顺序不能动，见 `LayerAppearance.swift:4` 的 `CaseIterable`）
- 混合模式的**工作空间选择**按 `ColorSpace.Srgb` 实现
- 若后续 Tier 2 实测推翻此结论，改的是混合模式内部的开关，**不动枚举值**，其他模块不受影响
- 依 §四 第 4 条，此处已构成「结论为 `Srgb`」，**已通知项目负责人**

---

## 四、我已直译的 9 个 C 文件，各自的工作空间（实测）

这一节是顺手做的静态核实，对理解整条色彩管线有用。

### 4.1 唯一做线性转换的地方：`adjust_camera_raw` 的 L320–L322

对全文件 1157 行检索 `srgb_to_linear` / `linear_to_srgb`，**调用点只有 6 处，全部在 L320–L325**，即 `adjust_camera_raw`（L299–L349）函数体内的连续 6 行：

```c
L320  r = camera_clamp(srgb_to_linear(r) * redGain * light);   // 编码 → 线性，乘白平衡增益与曝光
L321  g = camera_clamp(srgb_to_linear(g) * greenGain * light);
L322  b = camera_clamp(srgb_to_linear(b) * blueGain * light);
L323  r = camera_clamp(0.5 + (linear_to_srgb(r) - 0.5) * contrastScale);   // 线性 → 编码，之后再算对比度
L324  g = camera_clamp(0.5 + (linear_to_srgb(g) - 0.5) * contrastScale);
L325  b = camera_clamp(0.5 + (linear_to_srgb(b) - 0.5) * contrastScale);
```

> 🔴 **一处必须精确的更正**：`linear_to_srgb` 出现在**对比度乘法之前**。
> 因此**在线性光里计算的只有「白平衡三通道增益」与「曝光档数」两项**（L320–L322）；
> **对比度是在编码 sRGB 里做的**（L323–L325 先转回编码再乘 `contrastScale`）。
> 我最初的口头结论是"白平衡、曝光、对比度三者都在线性光"，**这是错的**，此处按调用点更正。

线性段之后，从 L326 开始的影调分级（高光 / 阴影 / 白场 / 黑场）、自然饱和度、饱和度，**全部在编码 sRGB 域**。

> 这与 Photoshop / Camera Raw 的惯例一致，属有意为之，**不属于铁律 3 的范畴**。
> 但它说明一件事：原项目**并没有"统一在线性光工作"的做法**，只有白平衡与曝光这两步走线性。

### 4.2 其余全部在编码 sRGB

依据同上：**全文件除 L320–L325 外没有任何 sRGB↔线性转换**。

| C 函数 | 起始行 | 工作空间 | 依据 |
|---|---|---|---|
| `adjust_gradient_map` | L7 | 编码 sRGB | L22 对 0–255 直算 `level`，无转换 |
| `adjust_grain` | L64 | 编码 sRGB | L85–86 反预乘后直算 `level`，无转换 |
| `adjust_black_white` | L110 | **编码 sRGB** | L118–119 `fminf(255,r)*255/alpha` 后直接 `/255`，**无转换** |
| `adjust_color_balance` | L169 | **编码 sRGB** | L178 同上，**无转换** |
| `rgba_clamp_premultiplied` | L98 | 不涉及 | 只做整数钳制 |
| `adjust_camera_raw` | L299 | **仅 L320–L325 线性** | 见 §4.1；其余全编码 |
| `adjust_colored_vignette` | L461 | 编码 sRGB | L479–481 直通域 |
| `adjust_camera_raw_clip_overlay` | L502 | 编码 sRGB | L510–512 直通域 |
| `adjust_camera_raw_effects` | L524 | 编码 sRGB | 亮度平面由直通域算出 |
| `adjust_tonal_contrast` | L641 | 编码 sRGB | L657 `rec709` 直算 |
| `adjust_camera_raw_curve_color`（含 **color_mixer**） | L731 | **编码 sRGB** | `lut_at`(L674) 直接作用于 0–1；`rgb_to_hsl`(L682) 无转换；色彩混合器 L763–783 与调色轮 L796–807 全在编码域 |
| `adjust_camera_raw_sharpen_mask_overlay` | L859 | 编码 sRGB | |
| `adjust_camera_raw_detail` | L893 | 编码 sRGB | |
| `adjust_camera_raw_optics` | L1085 | 编码 sRGB | |
| `adjust_camera_raw_calibration` | L1117 | 编码 sRGB | L1139 `rgb_to_hsl` 无转换 |

### 4.3 验收意见点名要核实的三项

| 函数 | 结论 | 硬证据 |
|---|---|---|
| `adjust_black_white` | **编码 sRGB** | L118–119 无 `srgb_to_linear`；全文件该调用只出现在 L320–325 |
| `adjust_color_balance` | **编码 sRGB** | L178 同上 |
| `color_mixer`（在 `adjust_camera_raw_curve_color` 内） | **编码 sRGB** | 其上游 `lut_at`(L674–680) 与 `rgb_to_hsl`(L682–693) 均无转换；mixer 自身 L763–783 也不含转换 |

**「无转换」不是我的推断，是全文件检索的结果**：`srgb_to_linear|linear_to_srgb` 在 1157 行里只有 6 个调用点，全在 `adjust_camera_raw` 的 L320–L325。

**这对 AI-5（波次 3，12 种调整层）意味着**：照抄原项目时，绝大多数调整层都应工作在**编码 sRGB**，只有白平衡与曝光两步走线性。若统一按线性实现，会与 Mac 版系统性偏离。

---

## 五、待补验证（Tier 2）

按补充通知 §三，Mac 通道走 GitHub Actions `macos-26` runner（原项目 `verify.yml` 注释明证该 runner 能真实跑起 macOS GUI 应用）。就绪后：

1. 在 Mac 版做标定图：12×12 灰阶作底层 + 一个 50% 灰图层，分别用 Multiply / Screen / Overlay / ColorDodge / ColorBurn / LinearDodge 叠加，导出 PNG
2. Windows 端手写混合模式用同一输入跑一遍，**逐像素 diff**
3. 若 Windows 结果 ≠ Mac，则反过来把 `CIContext` 的 `workingColorSpace` 改成线性再比
4. 把结论回填本文件，并把「未经 Mac 实测标定」这句删掉

> 验收容差建议 **≤1/255**（任务书 §5 第 6 项）。
> 另注：`.NET` 的 `Math.Cos/Sin/Pow` 与 macOS libm 不保证逐位相同，手写混合公式时落在 `.5` 边界的值理论上可能差 1 —— 这是跨平台无法消除的，容差定 0 会永不稳定。

---

*编制于 2026-10-07 · AI-1 · 依据 `SeparableBlend.swift:12-17`、`LayerAppearance.swift:4/28/54-69`、release notes 静态核实得出，未实测。*