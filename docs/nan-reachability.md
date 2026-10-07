# NaN 可达性排查结论

> **结论：9 个 C 文件 2284 行中不存在可达的 NaN 路径。**
> 据此，`CSemantics.FMin` / `FMax` **不予添加**（第二批指令 `00d` §3 第 3 条：此项关闭）。
>
> 回归测试：`tests/Compositor.Core.Tests/Pixels/NanReachabilityTests.cs`。
> ⚠️ **该测试与本文件都未经编译/运行验证**（本机无 .NET SDK）。

---

## 一、为什么要查这件事

C99 与 .NET 在 `fmin` / `fmax` 上语义相反：

| 表达式 | C99（`math.h`） | .NET（`System.Math`） |
|---|---|---|
| `fmin(NaN, 1.0)` | **1.0**（忽略 NaN，返回另一个操作数） | `Math.Min(NaN, 1.0)` = **NaN**（传播） |
| `fmax(NaN, 1.0)` | **1.0** | `Math.Max(NaN, 1.0)` = **NaN** |

`fmin`/`fmax` 在 `AdjustPixels.c` 里出现约 100 次。

**风险链条**：若某条输入路径能产生 NaN → C 侧 `fmin` 会静默"治好"它 → C# 侧 `Math.Min` 会传播 →
像素值变 NaN → 落到 `CSemantics.U8` 的 `(byte)v` 上。
而 **C# 规范规定越界的浮点→整型转换是未指定行为**，实测取决于运行时的截断指令，
**既不抛异常也不产生编译告警**，表现为"某些像素莫名其妙变成 0"。

所以这不是"理论上可能"的问题，值得查实。而 `00d` §3 明确禁止为可能性做 ~100 处全量替换。

---

## 二、NaN 的四种来源，逐类排除

NaN 只能由这四类算式产生。逐类扫描全部 9 个 `.c` 文件：

| 来源 | 需要什么前提 | 全库检索结果 |
|---|---|---|
| `0 / 0` | 分母可能为 0，且分子也为 0 | 找到 3 处**看似**满足，**全部被显式判据或循环边界挡住**（见 §三） |
| `√(负数)` | `sqrt` 参数可能为负 | 全部 `sqrt` 调用共 4 处：2 处除以常量 `sqrt(2.0)`；1 处 `sqrtf(clamp01(t))`（已钳非负）；1 处 `sqrtf(dx*dx+dy*dy)`（平方和 ≥ 0）。**无一处可为负** |
| `log(≤0)` | `log` 参数可能 ≤ 0 | **全库仅 1 处** `logf`（`NoisePixels.c:39`），参数 `1.0f - u1` 恒 ∈ (0, 1]，**恒为正** |
| `∞ − ∞` / `∞ × 0` | 先产生 Inf 再参与运算 | 找到 3 处会返回 `INFINITY`/`DBL_MAX` 哨兵的函数，**三者都只把哨兵用于比较，从不进入算术**（见 §四） |

补充：全库 `pow` / `powf` 共 2 处（`AdjustPixels.c:206, 213`），都在 sRGB 传递函数内，
底数恒正且入参已被 `camera_clamp` 钳到 [0,1]。

---

## 三、三个「看似能产生 0/0」的地方，全部不可达

### 3.1 `DitherPixels.c:37` 与 `:84` —— `/ steps`，`steps = levels - 1`

```c
// DitherPixels.c:35-38
static inline float quantize(float v, int levels) {
    float steps = (float)(levels - 1);
    return roundf(clamp01(v) * steps) / steps;   // levels == 1 → 0/0
}
```

**这是全库最可疑的一处。** `levels` 若为 1，`steps = 0`，分子 `roundf(clamp01(v) * 0) = 0`，
于是 `0 / 0 = NaN`。`ordered()`(L81-85) 同理。

**防护在入口**：

```c
// DitherPixels.c:166
int levels = p->levels < 2 ? 2 : p->levels > 16 ? 16 : p->levels;
```

`levels` 被钳到 **[2, 16]**，`steps ≥ 1`。且 L172 `local.levels = levels` 后才把钳位后的值传进 `diffuse`。
`quantize` / `ordered` 都是 `static`，外部无法绕过这个入口。

> ⚠️ **护栏很薄**：整个文件的正确性只靠 `dither_apply` 开头这一行。
> 已由 `NanReachabilityTests.路径1_Levels为1与0的结果必须与Levels为2逐字节相同` 锁住 ——
> 该测试断言 `levels ∈ {1, 0, -7}` 的输出与 `levels == 2` **逐字节相同**。

### 3.2 `LensPixels.c:12` —— `/ halfDiagonal2`

```c
// LensPixels.c:5-6, 12
double cx = width * 0.5, cy = height * 0.5;
double halfDiagonal2 = cx * cx + cy * cy;   // 仅 width==0 && height==0 时为 0
...
double scale = 1.0 - k * (dx * dx + dy * dy) / halfDiagonal2;
```

**不可达**：`halfDiagonal2 == 0` 要求 `width` 与 `height` 同时为 0，
而 `for (x = 0; x < width; ++x)` 一次都不执行，除法不会发生。

### 3.3 `AdjustPixels.c:1051` —— `/ maxR`

```c
// AdjustPixels.c:1041-1051
double cx = width * 0.5, cy = height * 0.5;
double maxR = hypot(cx, cy);                  // 仅 width==0 && height==0 时为 0
...
double radial = hypot(dx, dy) / maxR;
```

**不可达**，理由同 3.2。另外直译的 `AdjustPixels.Curve.cs:688` 还有一条**显式**的
`if (width == 0 || height == 0) return;`，双保险。

---

## 四、三个「返回哨兵值」的函数，哨兵从不进算术

### 4.1 `HealPixels.c:36/37/49` —— `INFINITY` 哨兵

```c
static double heal_score(...) {
    if (labs(dx) < ww && labs(dy) < wh) return INFINITY;
    if (...越界...) return INFINITY;
    ...
    return n ? sum / n : INFINITY;
}
```

调用方逐处显式过滤：

| 行 | 代码 | 作用 |
|---|---|---|
| `HealPixels.c:171` | `if (!isfinite(score)) continue;` | 丢弃 Inf |
| `HealPixels.c:173` | `if (score < best)` | **仅比较** |
| `HealPixels.c:176` | `if (isfinite(best)) {` | Inf 时整块跳过 |

**原作者是显式防过 NaN 的。** 另有 `HealPixels.c:108` `if (!n) continue;` 保护 L109 的 `sum[c] / n`，
以及 L214 的 `if (n)` 保护 `around / n`。

### 4.2 `ContentFill.c:21` —— `DBL_MAX` 哨兵

```c
return count ? sum/count : DBL_MAX;
```

调用方 `ContentFill.c:60` `double score=DBL_MAX;`，L71 / L79 只做 `if (s < score)` 比较，
**`score` 从未参与任何算术**。与 4.1 同构。

### 4.3 `NoisePixels.c:39` —— Box–Muller 开方

```c
float u1 = noise_unit(key);
n = sqrtf(-2.0f * logf(1.0f - u1)) * cosf(6.2831853f * u2) * spread * (2.0f / 3.0f);
```

**全库唯一的 `logf` 调用**，也是最像 NaN 的一处（`sqrt` 参数为负即出 NaN）。
但 `noise_unit` 的定义保证了它安全：

```c
// NoisePixels.c:12-13，注释原文 "Uniform in [0, 1)"
static inline float noise_unit(uint32_t key) {
    return (float)(noise_hash(key) >> 8) * (1.0f / 16777216.0f);
}
```

`hash >> 8` 的值域是 **0 … 2²⁴−1**，除以 2²⁴ 后是 **[0, 1)**，严格小于 1。
于是 `1 − u1 ∈ (0, 1]` → `logf(...) ≤ 0` → `−2 × logf(...) ≥ 0` → **开方参数恒非负**。

---

## 五、其余 5 个文件的结论

| 文件 | 浮点除法 / 开方 / 对数 | 结论 |
|---|---|---|
| `WandPixels.c` | 无 | 纯整数阈值比较与查表 |
| `BrushPixels.c` | 无 | 整数预乘 `(c*a + 127)/255`，分母是常量 255 |
| `LevelsPixels.c` | 无 | 查表 + 直方图累加 |
| `NoisePixels.c` | 1 处 `logf`（已证安全） | 见 §4.3 |
| `ContentFill.c` | 1 处 `/count`（有判据） | 见 §4.2 |

---

## 六、三个钳位辅助函数都**不是** NaN 消毒器

值得单独记一笔，因为它们的写法很容易让人误以为能挡住 NaN：

```c
static double camera_clamp(double value) {   // AdjustPixels.c:198-202
    if (value < 0) return 0;
    if (value > 1) return 1;
    return value;                              // NaN 会落到这里，原样返回
}
static inline float clamp255(float value) {  // AdjustPixels.c:62   同样的形状
    return value < 0 ? 0 : value > 255 ? 255 : value;
}
static inline float clamp01(float v) {       // DitherPixels.c:6   同样的形状
    return v < 0 ? 0 : v > 1 ? 1 : v;
}
```

三者的两次比较对 NaN **都为 false**，因此 NaN 会**穿过**它们被原样返回。
它们只是区间钳位，不是 NaN 消毒器。

本项之所以仍然关闭，正是因为**没有任何路径能把 NaN 送进它们**——
一旦有，这些函数会静默放行，NaN 一路走到 `(byte)` 转换的未指定行为上。

---

## 七、裁决与后续

| 项 | 状态 |
|---|---|
| 构造出可达的 NaN 路径 | ❌ **未能构造**（穷举 2284 行，结论见上） |
| 在 `CSemantics` 增加 `FMin`/`FMax` | ❌ **不添加**（`00d` §3 第 3 条：不构造出来即关闭此项） |
| ~100 处 `fmin`/`fmax` 全量替换 | ❌ **明确不做**（`00d` §3：会让 4497 行已直译代码全部重新 review） |
| 回归测试 | ✅ `NanReachabilityTests.cs`（13 个测试，锁定 §三/§四 的全部护栏） |

### 如果将来这个结论被推翻

触发条件是有人改动了 §三 里的三处钳位/边界，或新增了带除法的滤镜。
届时**只需**：

1. 在 `CSemantics` 加 `FMin`/`FMax`（C99 语义：任一为 NaN 时返回另一个操作数）
2. **只替换**确认存在 NaN 风险的那几个函数（不是全量 100 处）
3. 列出替换清单

`NanReachabilityTests` 的存在意义就在这里：它会在护栏被拆掉的那一刻先失败。

---

*编制于 2026-10-07 · AI-1 · 依据对 `Compositor/Rendering/*.c` 全部 9 个文件 2284 行的逐项检索
（`srgb_to_linear|linear_to_srgb|pow|powf|sqrt|sqrtf|exp|log|logf|hypot|fmin|fmax` 与全部除法表达式）。*
