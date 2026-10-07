# Compositor Windows · Tier 2 黄金样本通道

在 GitHub Actions 的 **`macos-26`** runner 上真跑 macOS，用上游
[Compositor](https://github.com/robbietilton/Compositor)（MIT）产出 Windows 版需要对照的黄金样本。

## 为什么需要它

YG 没有 Mac 电脑。这份仓库存在的唯一原因是：**在有 macOS 的地方跑一次真实的 Mac 版代码**，
把它的实际输出固化下来，让 Windows 版的重写有可对账的依据。

它不能解决所有问题。已知原理性无解的仍然是 5%（见总管的验收策略），
但它能把「24 个混合模式可能不一致」这种模糊说法，变成逐像素的差值数字。

## 本仓库刻意不包含 Windows 代码

只有两样东西：

| 路径 | 内容 |
|---|---|
| `.github/workflows/dump-goldens.yml` | workflow：checkout 上游 → 注入测试 → 跑 → 收集产物 |
| `CompositorTests/CompositorBlendGoldenDumpTests.swift` | 要拷进上游测试目录的 Swift 文件 |

Windows 版的代码在 YG 本地，不进这个仓库。

## 它是怎么绕过"改不了 Xcode 工程"的

上游 `Compositor.xcodeproj` 是 `objectVersion = 77`，用
`PBXFileSystemSynchronizedRootGroup` 管理 `CompositorTests/` 目录。
意思是：**把 `.swift` 文件丢进那个目录，它就自动进测试 target**，不需要编辑
`project.pbxproj`，不需要加 target，不需要改 scheme。

这也是为什么 workflow 只做一次 `cp`。

## 产出什么

每次运行上传一个 artifact：

| 文件 | 内容 |
|---|---|
| `blend-golden.json` | 24 个混合模式各自的实际输出像素（32×32×4 = 4096 字节 RGBA8 预乘 hex），加输入图案 |
| `blend-golden-summary.tsv` | 人可读的归类清单：每个模式走 CoreImage 还是 CoreGraphics |
| `CompositorBlendGoldenDumpTests.swift` | 生成器源码快照，便于日后追查 |
| `provenance.txt` | 上游 commit SHA、runner 镜像版本、生成时间 |

### 为什么每条记录都带 `path` 字段

上游 `Document/LayerAppearance.swift` 的两处：

- `:54-68` `coreImageFilter` —— **11 个**模式由闭源 CoreImage 计算
- `:28-49` `cgMode` —— 其余 **13 个**走闭源 CoreGraphics

这 13 个里 `softLight` / `colorDodge` / `colorBurn` 虽有 `cgMode` 条目，
但 `SeparableBlend.swift:12` 的 `needsSurface` 让它们走 CoreImage，所以 `cgMode` 对它们是死代码。

于是 24 个模式分成两类，本仓库的 JSON 直接把它标出来：

- `path = "coreImage"`（11 个）→ 与手写 W3C **确定不等价**，有闭源实现
- `path = "coreGraphics"`（13 个）→ 能否与 W3C 对齐**无法从源码判定**，需要这份像素来量化

## 手动跑一次

```bash
# 本地 macOS，需要 Xcode 26.6
xcode-select -s /Applications/Xcode_26.6.app
COMPOSITOR_FIXTURE_DIR=/tmp/compositor-fixtures \
xcodebuild test \
  -project Compositor.xcodeproj -scheme Compositor \
  -destination 'platform=macOS,arch=arm64' \
  -only-testing:CompositorTests/CompositorBlendGoldenDumpTests
```

## 仓库设置要求

**必须是 public。** GitHub 对公开仓库的 macOS runner 免费，私有仓库按 10x 计价。

## License

本仓库的 workflow 与 Swift 生成器：随 Compositor for Windows 项目。
上游 Compositor 为 MIT 版权，见 robbietilton/Compositor 的 LICENSE。
