# `.comp` 工程格式

> 归属：`src/Compositor.Project/`
> 维护者：AI-2
> 最后更新：2026-10-08

本文是 Windows 移植侧的格式说明。**权威来源是 macOS 源码**
（`_eval_Compositor/Compositor-main/Compositor/IO/ProjectStore.swift`），
每条都给了行号；本文只做整理与补充。

---

## 包形态

`.comp` 是**目录**，不是 zip、也不是单文件。

**依据**：`ProjectStore.swift:147` 要求
`url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true`，
否则抛 `.invalid`。

Windows 允许目录带任意扩展名，所以「名为 `foo.comp` 的目录」在两边都成立。
**写成 zip 会让 Mac 版直接拒收这个工程。**

## 目录结构

对照 `ProjectStore.swift:112-121`：

```
foo.comp/
  manifest.json
  images/
    <图层UUID大写>.png
    <图层UUID大写>.mask.png
  QuickLook/          ← 读时忽略；本移植不产出
```

| 常量 | 值 | 依据 |
|---|---|---|
| manifest 文件名 | `manifest.json` | `ProjectStore.swift:113` |
| 资源目录 | `images` | `ProjectStore.swift:114` |
| 资源编码 | **仅 PNG** | `ProjectStore.swift:98,174` |
| manifest 上限 | 4 MiB | `ProjectStore.swift:111,149` |
| 单资源上限 | 512 MiB | `ProjectStore.swift:167` |
| 文档像素预算 | 800 MP | `DocumentLimits.documentPixelBudget` |

---

## 🔴 UUID 必须大写

Swift 的 `UUID.uuidString` 输出**大写**，.NET 的 `Guid.ToString()` 输出**小写**。

Mac 在两处用 `uuidString` 校验文件名：

- `ProjectStore.swift:223` — `layer.maskFile == "\(layer.id.uuidString).mask.png"`
- `ProjectStore.swift:243` — `layer.imageFile == "\(layer.id.uuidString).png"`

因此：

- 文件名一律经 `ProjectManifest.ImageFileName` / `MaskFileName` 生成，**永不**直接用
  `Guid.ToString()`。
- manifest 里的 UUID 字段（`documentID` / `id` / `parentID` / `maskSourceID` …）
  由 `UppercaseGuidConverter` 按大写写出；读取忽略大小写，兼容小写历史文件。

少一个大写，Mac 就拒收**整个工程**，而且本地零报错。

---

## 资源解码的三条格式级守卫

这三条在 Windows 上**没有等价物**，必须自己判：

| 守卫 | Mac 依据 | 为什么 Skia 顶不上 |
|---|---|---|
| 必须是 PNG | `:174` | `SKCodec` 也会解 JPEG |
| **必须是单帧** | `:175` `CGImageSourceGetCount == 1` | `SKCodec` 只给第一帧，APNG 会被静默当成静态图 |
| **位深 ≤ 8** | `:179` `(depth ?? 8) <= 8` | `SKCodec` 会把 16 位静默降成 8 位 |

后两条由 `PngHeader.cs` 直接解析 IHDR 实现——颜色类型与位深是
**PNG 格式的属性**，等 Skia 解完就再也看不出来了。

放行任一条的后果都不是「读错」，而是**「Windows 打开正常、Mac 打不开」**。

### 蒙版的额外约束

`ProjectStore.swift:189` → `LayerMask.swift:23-26`：
蒙版必须是 monochrome + 8bpc + `alphaInfo == .none`（无 alpha 通道）。

**反向不成立**：Mac 对**图层像素**没有任何 alpha / 颜色类型守卫
（`:168-190` 只在 `isMask` 分支查）。一张无 alpha 的 RGB PNG 作为图层是合法的。

---

## 保存

1. 先按 `Images` / `Masks` 字典的键**推导**文件名，不读 `LayerNode.ImageFile`。
   Mac 的 save 会把 manifest 里的 `imageFile` 原样写出去，而它的 validator 又强制
   该值必须等于 `"<id>.png"`——也就是说 Mac 版也只能写推导值。
2. 蒙版在**存之前**就拒非 `Gray8`。Mac 是在 `ProjectStore.swift:93` 存完才知道，
   那样会产出「现在能打开、发给同事打不开」的包。
3. 默认值一律写 `null`（`opacity == 1` → 不写 `opacity`），保持 Mac 的最简形态。
4. 先写同级暂存目录，换名成功后再删除旧包；换名失败会把旧包改回原位。
   对应 `ProjectStore.swift:125-131` 的 `NSFileCoordinator` + `write(options: .atomic)`。

---

## 与契约层的有损缺口

`shape` / `effects` / `text` / `guides` / `resolution` 在契约 v1.2 里没有承载字段，
覆盖保存会丢。清单与处理方式见 [contract-gaps.md](contract-gaps.md)。

已知的格式级与性能级差异见 [tier3-known-differences.md](tier3-known-differences.md)。
