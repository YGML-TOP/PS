# 色彩空间工作域判定（铁律 3）

> # ⚠️ 未经 Mac 实测标定
>
> **本文件的所有结论均来自对原项目 macOS 源码的静态核实，没有任何一条经过实跑验证。**
>
> 我们没有 Mac 电脑，无法运行原版 Compositor 产出标定图。
>
> 但与波次 1-A 初版不同：**第二批指令 `00d` §2 已确认「这份 C 源码就是 Mac 版的真实行为，
> 它被编译进 Mac 版应用里」，因此像素管线部分不再是「推测」，而是「照抄」。**
> 真正需要 Mac 的只剩手写的混合模式（见 §五）。

| | |
|---|---|
| **结论** | 全部像素调整函数工作在**编码 sRGB**；**唯一例外**是 `adjust_camera_raw` 内 L320–L322 两步 |
| **建议枚举值** | `ColorSpace.Srgb` |
| **依据强度** | 混合模式 = 源码注释 + release notes（强）；像素管线 = 全文件检索穷举（强） |
| **是否已通知项目负责人** | ✅ `00b` §四 第 4 条（结论为 `Srgb` 而非 `LinearSrgb`）；`00d` §2 已**撤回**原「按线性实现」的建议并采纳本结论 |
| **待补验证** | 混合模式部分 → Tier 2 · GitHub Actions `macos-26` runner |

---

## 一、结论速览（给 AI-5 的那一条）

> **照抄原项目时，除 `adjust_camera_raw` 的白平衡增益与曝光两步外，一切都在编码 sRGB 上做。**
> 若统一按扩展线性 sRGB 实现，会与 Mac 版**系统性偏离**，不是"差一点点"。

---

## 二、逐函数工作空间表（`00d` §2 要求的表，`?` 已全部查实）

判定方法：**对 9 个 `.c` 文件全文检索 `srgb_to_linear|linear_to_srgb|pow|powf|sqrt|exp|log`**，
凡出现转换函数即为线性域；`powf(v, gamma)` 这类**用户可调伽马**不算线性化（见 §四）。

### 2.1 `AdjustPixels.c` 公开函数

| 函数 | 起始行 | 工作空间 | 依据行号 |
|---|---|---|---|
| `adjust_gradient_map` | L7 | **编码 sRGB** | L15–22 整数域 `(2126r+7152g+722b+5000)/10000`，全程无转换 |
| `adjust_grain` | L64 | **编码 sRGB** | L84–86 反预乘后直接算 `level`，无转换 |
| `rgba_clamp_premultiplied` | L98 | 不涉及色彩 | L100–103 仅整数钳制 |
| `adjust_black_white` | L110 | **编码 sRGB**（`00d` 问号项①） | L118–119 `fminf(255,r)*255/alpha` 后直接 `/255`，无转换 |
| `adjust_color_balance` | L169 | **编码 sRGB**（`00d` 问号项②） | L178 `fminf(255,p[i]*255/alpha)/255`，L179 Rec.601 直算，无转换 |
| `adjust_camera_raw` | L299 | **混合：L320–L322 线性，其余编码** | 见 §2.2 |
| `adjust_colored_vignette` | L461 | **编码 sRGB**（`00d` 问号项⑤） | L479–481 反预乘直取，L482 `rec709` 直算，无转换 |
| `adjust_camera_raw_clip_overlay` | L502 | **编码 sRGB** | L510–512 直通域 |
| `adjust_camera_raw_effects` | L524 | **编码 sRGB** | L548 亮度平面由直通域算出 |
| `adjust_tonal_contrast` | L641 | **编码 sRGB**（`00d` 问号项④） | L654–657 `fmin(1.0, p[0]/alpha)` 后 `rec709` 直算，无转换 |
| `adjust_camera_raw_curve_color`（含 `color_mixer`） | L731 | **编码 sRGB**（`00d` 问号项③） | `lut_at`(L674–680) 直接作用于 0–1；`rgb_to_hsl`(L682) 无转换；mixer L763–783 与调色轮 L796–807 全在编码域 |
| `adjust_camera_raw_sharpen_mask_overlay` | L859 | **编码 sRGB** | L872 亮度平面由直通域算出 |
| `adjust_camera_raw_detail` | L893 | **编码 sRGB** | L910 / L989 亮度平面由直通域算出 |
| `adjust_camera_raw_optics` | L1085 | **编码 sRGB** | L1029 `optics_defringe` 的 `rec709` 直算 |
| `adjust_camera_raw_calibration` | L1117 | **编码 sRGB** | L1137 反预乘直取，L1139 `rgb_to_hsl` 无转换 |

**穷举证据**：全文件 1157 行中，`srgb_to_linear` / `linear_to_srgb` 的**调用点只有 6 处，
全部集中在 L320–L325**；`pow()` 的**调用点只有 2 处**（L206 / L213），就是这两个函数自己的实现体。
**因此"其余函数全在编码域"不是推断，是检索的完备结果。**

### 2.2 `adjust_camera_raw` 内部：哪几步在线性光

```c
L320  r = camera_clamp(srgb_to_linear(r) * redGain * light);   // 编码 → 线性
L321  g = camera_clamp(srgb_to_linear(g) * greenGain * light);
L322  b = camera_clamp(srgb_to_linear(b) * blueGain * light);
L323  r = camera_clamp(0.5 + (linear_to_srgb(r) - 0.5) * contrastScale);  // 线性 → 编码
L324  g = camera_clamp(0.5 + (linear_to_srgb(g) - 0.5) * contrastScale);
L325  b = camera_clamp(0.5 + (linear_to_srgb(b) - 0.5) * contrastScale);
L326  scale_luminance(&r, &g, &b, tone_highlights(rec709(r, g, b), highlightAmount));  // ← 已在编码域
```

| 步骤 | 行号 | 空间 |
|---|---|---|
| 白平衡三通道增益 | L320–L322 | **线性光** |
| 曝光档数 `exp2(exposure)` | L303（乘在 L320–322 上） | **线性光** |
| 对比度 `contrastScale` | L323–L325 | **编码 sRGB** |
| 高光 / 阴影 / 白场 / 黑场 | L326–L329 | **编码 sRGB** |
| 自然饱和度 + 饱和度 | L330 | **编码 sRGB** |
| 裁剪 `clipping` | L331–L345 | **编码 sRGB** |

> 🔴 **一处必须精确的更正**：`linear_to_srgb` 出现在**对比度乘法之前**。
> 所以在线性光里计算的**只有「白平衡三通道增益」与「曝光档数」两项**；
> **对比度是在编码 sRGB 里做的**。
> 我最初的口头结论是"白平衡、曝光、对比度三者都在线性光"，**那是错的**，此处按调用点更正。

这与 Photoshop / Camera Raw 的惯例一致，属有意为之。

### 2.3 其余 8 个 C 文件

| 文件 | 工作空间 | 依据 |
|---|---|---|
| `DitherPixels.c` | **编码 sRGB** | L19 的 `powf(clamp01(v), gamma)` 是**用户伽马滑杆**（L17 注释："Density darkens or lightens as a gamma"），不是线性化；L241/L272 的亮度权重直算 |
| `BrushPixels.c` | 不涉及色彩 | 仅 alpha 边界与 alpha 通道搬运 |
| `ContentFill.c` | 不涉及色彩 | 前景色/背景色按 `(x*a+127)/255` 整数预乘写入 |
| `HealPixels.c` | 不涉及色彩 | 空间滤波，仅覆盖度 |
| `WandPixels.c` | 不涉及色彩 | 阈值分割，RGB 直比 |
| `LevelsPixels.c` | 不涉及色彩 | 逐通道查表 `levels_apply` / 直方图 |
| `NoisePixels.c` | 不涉及色彩 | 逐通道加噪 |
| `LensPixels.c` | 不涉及色彩 | 桶形畸变重采样 |

---

## 三、混合模式：不在 C 层，Windows 端必须从零手写

### 3.1 检索结果

对 `Compositor/` 下全部 **19 个 `.c` / `.h` 文件**检索
`CGBlendMode|CIColor\w*BlendMode|blend\w*\(`：

> **零命中。9 个 C 文件里没有任何混合模式实现。**

### 3.2 混合模式的真实实现位置

| 路径 | 内容 |
|---|---|
| `LayerAppearance.swift:28-49` | `var cgMode: CGBlendMode` —— Core Graphics 直接能画的 17 种 |
| `LayerAppearance.swift:54-69` | `var coreImageFilter: String?` —— Core Image 滤镜名，11 种 |
| `SeparableBlend.swift` | 把图层画进副本后用 Core Image 混合再贴回 |
| `LayerRenderer.swift` / `TiledLayerRenderer.swift` / `GPUCanvas.swift` | 合成器调度 |

### 3.3 由此产生的三条结论

1. **波次 1-A / 1-B 的直译不涉及铁律 3。** 2284 行 C 里**一个混合模式都没有**。
2. **Windows 端没有 Core Graphics / Core Image**，混合模式必须**从零手写**，不是直译。
   手写代码没有 Mac 版可逐行对照 —— 这正是铁律 3 在波次 2 仍然成立的原因。
3. **工作空间选错不会「差一点」，会错到 62% vs 100% 这种量级**（见 §4.1），必须一开始就定对。

---

## 四、混合模式的工作空间：编码 sRGB（决定性证据）

### 4.1 源码注释原文

`Compositor/Rendering/SeparableBlend.swift:14-17`：

```swift
// Core Image works in a linear space unless told otherwise, and these two modes are not separable from
// the gamma they are computed in: over 40% grey, an 80% grey layer dodges to 62% instead of Photoshop's
// 100%, and burns to 0% instead of 25%. The blend has to happen in the same sRGB the canvas is in.
private static let ciContext = CIContext(options: [.cacheIntermediates: false, .workingColorSpace: space])
```

其中 `space` 定义在同文件 L12：

```swift
private static let space = CGColorSpace(name: CGColorSpace.sRGB)!
```

**这段注释给出了三件事：**

1. **Core Image 的默认工作空间是线性的** —— 原文第一句 "works in a linear space **unless told otherwise**"
2. **线性空间是错的** —— Color Dodge 在 40% 灰底 + 80% 灰图层上会算成 **62%**，Photoshop 是 **100%**；
   Color Burn 会算成 **0%**，Photoshop 是 **25%**
3. **原项目把工作空间强制改回编码 sRGB** —— `.workingColorSpace: space` = `CGColorSpace.sRGB`

### 4.2 release notes 佐证

> "Color Dodge and Color Burn were blending in the wrong color space"

对应的修复就是上面那段 `.workingColorSpace` 覆盖。

**即：Mac 版当年修的 bug，恰恰是「从 Core Image 的默认线性空间，改回编码 sRGB」。**

### 4.3 与 `00b` 补充通知 §四 的分歧（已解决）

`00b` §四 曾建议「按扩展线性 sRGB 实现（CoreImage 的默认行为，理论上最接近 Mac 版）」。

| | `00b` §四 的说法 | 源码证据 |
|---|---|---|
| Core Image 默认空间 | 线性 | ✅ 线性（`SeparableBlend.swift:14` 确认） |
| 哪个最接近 Mac 版 | 线性 | ❌ **编码 sRGB**。Mac 版**主动把默认线性改掉了** |
| 依据 | 「理论上最接近」 | Mac 版注释给出了线性下的具体错值：62% vs 100%、0% vs 25% |

`00b` 把「CoreImage 的默认行为」当成了「最接近 Mac 版」的依据；
但 Mac 版正是因为**不**用 CoreImage 默认行为，才修掉了那个 bug —— **默认行为恰恰是被否定的那一个**。

**✅ `00d` §2 已撤回该建议并采纳本结论。** 分歧关闭。

### 4.4 落地要求

- `ColorSpace` 枚举保留 `Srgb = 0` 与 `LinearSrgb = 1`（序列化顺序不能动，见 `LayerAppearance.swift:4` 的 `CaseIterable`）
- 混合模式的**工作空间选择**按 `ColorSpace.Srgb` 实现
- 若后续 Tier 2 实测推翻此结论，改的是混合模式内部的开关，**不动枚举值**，其他模块不受影响

---

## 五、仍然需要 Mac 的只剩一处

原项目还有一条 **GPU 路径**（`GPUCanvas.swift` + Metal 着色器 + CoreImage）。

- 我们首版只实现 **CPU 路径**（`Rendering/*.c` + 手写混合模式）
- Mac 版自身可能存在「CPU 路径 vs GPU 路径」的差异
- → **导出结果可能与 Mac 版某些预览画面不一致，这是已知且可接受的差异，不是缺陷**
  （`00d` §2 原文确认）

按 Tier 分层：

| 层 | 内容 | 状态 |
|---|---|---|
| **Tier 1** | 像素调整管线（§二） | ✅ **已照抄 C 源码定稿**，无需 Mac |
| **Tier 2** | 混合模式（§四） | ⚠️ 结论为编码 sRGB，但**未经 Mac 实测**，需 `macos-26` runner 复核 |
| **Tier 3** | CPU 与 GPU 路径差异 | 🚫 **原理上无法验证**，不假装对齐，明确声明为已知差异 |

---

## 六、待补验证（Tier 2 步骤）

按 `00b` §三，Mac 通道走 GitHub Actions `macos-26` runner
（原项目 `verify.yml` 注释明证该 runner 能真实跑起 macOS GUI 应用）。就绪后：

1. 在 Mac 版做标定图：12×12 灰阶作底层 + 一个 50% 灰图层，
   分别用 Multiply / Screen / Overlay / ColorDodge / ColorBurn / LinearDodge 叠加，导出 PNG
2. Windows 端手写混合模式用同一输入跑一遍，**逐像素 diff**
3. 若 Windows 结果 ≠ Mac，则反过来把 `CIContext` 的 `workingColorSpace` 改成线性再比
4. 把结论回填本文件第 1 节的 ⚠️ 横幅，删掉「未经 Mac 实测标定」

> 验收容差建议 **≤1/255**（任务书 §5 第 6 项）。
> 另注：`.NET` 的 `Math.Cos/Sin/Pow` 与 macOS libm 不保证逐位相同，
> 手写混合公式时落在 `.5` 边界的值理论上可能差 1 —— 跨平台无法消除，**容差定 0 会永不稳定**。

---

*编制于 2026-10-07 · AI-1 · §二 依据 `AdjustPixels.c` 全文件检索穷举（L320–L325 为唯一转换点），
§四 依据 `SeparableBlend.swift:12-17` 与 `LayerAppearance.swift:4` 源码注释。混合模式部分未经 Mac 实测标定。*
