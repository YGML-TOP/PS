# 契约缺口导致的保存有损

> 归属：`src/Compositor.Project/`
> 维护者：AI-2
> 最后更新：2026-10-08

契约 v1.2 的 `LayerNode` 与 `ProjectSnapshot` 比 macOS 的 `ProjectManifest` /
`ProjectLayerRecord` **少**几个字段。这些字段在 manifest 里都存在，
所以**读得进来、存不回去**——一次覆盖保存就会永久丢失。

本文件是给总管和契约维护者的**待办清单**，不是实现说明。

---

## 现状：读的时候就会警告

`ProjectStore.WarnAboutUnrepresentableFields` 在 **`load` 阶段**（不是 save）发出警告，
经由 `ProjectJson.WarningSink` 输出。

选在 load 而不是 save 的理由：等到 save 再报，用户的旧包已经被覆盖了。
读的时候报，用户至少有机会先导出。

---

## 缺口 1：`LayerNode` 缺 shape / effects / text

| manifest 字段 | Mac 侧类型 | 契约 v1.2 |
|---|---|---|
| `shape` | `LayerShapeStyle?` | ❌ 无 |
| `effects` | `LayerEffects?` | ❌ 无 |
| `text` | `LayerTextStyle?` | ❌ 无 |

**Mac 侧位置**：`ProjectStore.swift:52-55`。

**后果**：形状图层、描边/投影特效、文字图层的样式在覆盖保存后全部消失。
图层本身、像素、蒙版都还在，只是「以后怎么重新绘制」的信息没了。

**建议**：契约 v1.3 给 `LayerNode` 补三个属性。若沿用 manifest 的宽松形状，
可用 `JsonObject?`，与 `ProjectLayerRecord` 现状一致。

---

## 缺口 2：`ProjectSnapshot` 缺 guides

| manifest 字段 | Mac 侧类型 | 契约 v1.2 |
|---|---|---|
| `guides` | `[CanvasGuide]?`（v8 起） | ❌ 无 |

**Mac 侧位置**：`ProjectStore.swift:30`；校验在 `ProjectStore.swift:249-262`。

**后果**：参考线在覆盖保存后消失。

**依赖**：契约层连 `CanvasGuide` 这个类型都没有，
`ProjectManifest.CanvasGuide` 是 AI-2 自己定义的。补字段时要一并把类型提到契约层。

---

## 缺口 3：`ProjectSnapshot` 缺 resolution

| manifest 字段 | Mac 侧类型 | 契约 v1.2 |
|---|---|---|
| `resolution` | `Double?` | ❌ 无 |

**Mac 侧位置**：`ProjectStore.swift:23`；校验在 `ProjectStore.swift:202-204`
（缺省按 72 ppi，v1 尤其重要）。

**后果**：保存后 `resolution` 变成 `null`。v1 工程按 Mac 的规则读回来还是 72 ppi，
看起来「没事」；但 v3+ 工程的物理尺寸会悄悄改变。

---

## 为什么没有直接抛异常

一种更保守的做法是：只要 manifest 里出现契约承载不了的字段，保存就抛错。

没有这么做，理由：

1. **会让正常流程卡死**。v11 夹具带 2 条参考线 + `resolution`，
   抛错等于「v11 工程在 Windows 上无法保存」。
2. **错误发生在最后一刻**。用户已经改了半天，一保存就失败，且原包已被换掉。
3. **可恢复性更好**。警告 + 继续保存，用户能立刻看到结果并决定是否导入备份。

如果总管认为「静默丢失」不可接受，可选方案是在 UI 层把警告升级为**确认对话框**
（要求用户显式确认），而不是在存储层硬拦。

---

## 相关测试

| 测试 | 覆盖 |
|---|---|
| `ProjectStoreTests.LoadAsync_契约承载不了的字段会在读取时就报警` | v11 的参考线 + 分辨率都会报 |
| `ProjectStoreTests.LoadAsync_v1夹具只报分辨率_不报参考线与形状特效` | 精确到「只报该报的」 |
| `ProjectStoreTests.SaveAsync_Load再Save_manifest字节完全一致` | 定点往返（但**抓不到**本文件的缺口） |

⚠️ 注意最后一条的局限：定点往返只证明「写两次结果一致」。
映射一旦漏字段（本轮就漏了 `ParentId`），两次会**一致地错**。
真正兜住缺口的是前两条警告测试。
