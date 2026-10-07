# Tier 3 已知差异（Project / Codec 层）

> 归属：`src/Compositor.Project/`、`src/Compositor.Imaging/`
> 维护者：AI-2
> 最后更新：2026-10-08

本文只记**已经确认存在、且当前不做**的差异。每条都写清楚：现象、依据、有没有测试兜住、
以及为什么现在不修。

不在本文里的东西：

- 已实现的行为 → 见 `project-format.md`
- 契约层缺字段导致的有损保存 → 见 `contract-gaps.md`
- 还没做的功能 → 见任务书，不在这里占位

---

## T3-01 HEIC / TIFF 首版不支持

**现象**：导入面板拒绝 HEIC、TIFF（以及 Photoshop 的 PSD/PSB、相机 RAW、SVG）。

**依据**：

- Mac 侧 `IO/ProjectStore.swift:10` 的 `importableImages` 包含 `.heic` / `.tiff` /
  `.photoshopImage` / `.photoshopLargeImage` / `.rawImage` / `.svg`。
- 本移植 `Compositor.Imaging/ImageFormatGate.cs` 的显式白名单**只有 PNG 与 JPEG**。

**影响面**：只影响**导入**，不影响 `.comp` 读写——`.comp` 包内资源本来就只接受 PNG
（`ProjectStore.swift:174` 硬判 `UTType.png.identifier`）。

**为什么现在不修**：SkiaSharp 4.x 官方构建不含 HEIC/TIFF 解码器，要支持得引入
libheif / libtiff 原生依赖并为三种平台分别打包。收益（相机 RAW、PSD 分层导入）
与 M2 的目标（读写 `.comp`）无关。

**测试**：`ImageFormatGate` 对未列入白名单的扩展名逐个断言拒绝。

---

## T3-02 EXIF 方向未处理

**现象**：带 EXIF `Orientation` 的 JPEG 导入后按存储方向显示，不按拍摄方向。

**依据**：Mac 侧导入路径走 `CGImageSourceCreateThumbnailAtIndex`，ImageIO 会自动
应用 `kCGImagePropertyOrientation`；SkiaSharp 需要显式读 `SKCodec.EncodedOrigin`
并自行做矩阵变换。

**为什么现在不修**：`EncodedOrigin` → 像素变换的映射要按 8 个 EXIF 值逐一实现并逐值测试，
而 M2 范围内没有 JPEG 导入入口（`.comp` 包内只可能是 PNG，PNG 本身没有 EXIF）。

**解除条件**：接入导入面板时必须一并处理，不能沿用现状。

---

## T3-03 Display P3 未转换到 sRGB

**现象**：以 P3 色彩空间标记打开的图，按 sRGB 解读，颜色偏艳。

**依据**：

- `ProjectValidator` 的 G03 强制 `colorSpace == "sRGB"`（对应
  `ProjectStore.swift:201`），所以**工程层面**不存在 P3 文档。
- 但**单张图**可以带 P3 的 ICC 配置文件；Mac 侧由 CoreGraphics 在解码时转，
  本移植目前直接采信 PNG 里已转换好的 sRGB 数据。

**为什么现在不修**：`.comp` 包内的 PNG 是本应用自己写出去的（全部 sRGB）；
要支持 P3 源图，需要在**导入**链路做 ICC 转换，属于 T3-01/T3-02 同一批工作。

---

## T3-04 BMP 外字符被 JSON 转义

**现象**：图层名里的 emoji、某些生僻字（如 `🌤`）在写出的 manifest 里变成
`\uD83C\uDF24` 这样的代理对转义，而不是直接写 UTF-8。

**依据**：`ProjectJson.BuildOptions` 已配 `UnsafeRelaxedJsonEscaping`，
但 `System.Text.Json` 的编码器**对 BMP 外字符（需要代理对的）一律转义**，
与 `Encoder` 设置无关。这是平台行为，不是本层的配置疏漏。

**影响**：只有**字节**不同，字符串值完全相同。Mac 的 `JSONDecoder` 读回同一个名字，
**不影响互通**。受影响的只有「两个平台产出的 manifest 逐字节相同」这一条期望——
BMP 内的中文等字符仍然原样写出（见 `ProjectStoreTests.SaveAsync_BMP非ASCII按UTF8原样写出`）。

**测试**：`ProjectStoreTests.SaveAsync_BMP外字符会被转义但读回来不变`
把这条行为钉死并验证语义无损。

---

## T3-05 缩略图不缓存

**现象**：打开工程时不生成 96px 缩略图。

**依据**：Mac 的 `ImportedImage` 带 `thumbnail`（`ProjectStore.swift:184-188` 生成），
契约 v1.2 的 `IProjectAsset` 只有 `Width / Height / Format / Pixels`，没有缩略图。

**影响**：**性能差异，不是格式差异**。`.comp` 包里本来就不存缩略图，
Mac 侧每次 `load` 也是现算的；差别只在 Windows 侧要不要在 UI 层缓存。

**解除条件**：UI 层按需从 `IProjectAsset.Pixels` 现算，或契约 v1.3 增补缩略图字段。

---

## T3-06 缺文件归 MissingImage 而非 TooLarge

**现象**：`.comp` 包内某个 PNG 不存在时，本移植报「工程内的一张图丢失或损坏」，
Mac 版报「超出限制」。

**依据**：`ProjectStore.swift:271-277` 的 `checkFile` 对不存在的文件也走
`values.isRegularFile == true` 这条判定，落进 `.tooLarge` 分支。
本移植在 `ProjectStore.ReadAsset` 里先判存在性，归到 `MissingImage`。

**为什么这是有意偏离**：两边都**拒绝**，工程兼容性完全一致；
差别只在给用户看的文案，而 Mac 的分类在这里是错的。

**测试**：`ProjectStoreTests.LoadAsync_声明了像素但文件缺失时报missingImage`。
