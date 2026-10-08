// macOS `.comp` dump —— 阶段二互通验证的采集端
//
// 这个文件**不在 Windows 构建里**，只在 macOS 上跑。用途：
// 用真正的 macOS 版 Compositor 产出一批 `.comp`，交给 Windows 侧读，
// 证明两端接受的是同一套规则。
//
// 为什么必须真跑而不是手搓夹具：手搓夹具只能证明"Windows 接受一份符合 Mac 规则的包"，
// 证不了"Windows 接受的恰好等于 Mac 接受的"。后者是互通的全部意义，
// 而且只有真机能证——ImageIO 的解码细节、FileWrapper 的包布局、
// JSONEncoder 的具体输出，都只能在 macOS 上观察。
//
// ─────────────────────────────────────────────────────────────────────
// 怎么跑（GitHub Actions，macos-26 runner）
// ─────────────────────────────────────────────────────────────────────
//
//   - uses: actions/checkout@v4
//   - uses: swift-actions/setup-swift@v2
//     with:
//       swift-version: '6.0'
//   - name: 生成 .comp 样本
//     run: |
//       swiftc -O \
//         -o "$RUNNER_TEMP/compositor-tests" \
//         -parse-as-library \
//         Package.swift 的测试目标
//       "$RUNNER_TEMP/compositor-tests" --dump-comp "$GITHUB_WORKSPACE/dump"
//
// 本文件要挂进 CompositorTests 目标里作为
// `CompositorTests.DumpFixtures`（或任何可执行入口）调用。
// 具体接法见文件末尾的 TODO 标记。
//
// ─────────────────────────────────────────────────────────────────────
// 产出物
// ─────────────────────────────────────────────────────────────────────
//
//   dump/
//     v01-groups-pending.comp/          ← 与 Windows 侧夹具同名
//     ...
//     v11-everything.comp/
//     digest.txt                        ← 每个包的 SHA256 + 字节数
//
// 同名是关键：Windows 侧的 AllVersionsFixtureTests 直接按名字读 dump/，
// 两边用同一份清单，diff 才有意义。

import AppKit
import Foundation
import CoreGraphics
import CryptoKit
import Testing
@testable import Compositor

// ⚠️ 本文件要拷进 CompositorTests/ 目标（Compositor.xcodeproj，不是 SwiftPM ——
// 上游没有 Package.swift）。测试风格必须与同目录其它文件一致：
// swift-testing 的 `@Test`，不是 XCTest，也**不能**是顶层脚本。
// 入口在文件末尾的 `DumpCompFixturesTests.dumpFixturesToDisk`。

/// 一个样本版本的定义。字段集必须与 Windows 侧的
/// `make-version-fixtures.ps1` 里的 `$Plans` 逐项对应。
struct VersionPlan {
    let version: Int
    let name: String
    /// 是否带分组（v2 起）
    let groups: Bool
    /// 是否带非默认不透明度 / 混合模式（v3 起）
    let opacityBlend: Bool
    /// 是否带图层蒙版（v4 起）
    let layerMask: Bool
    /// 是否带活蒙版链（v5 起）
    let liveMask: Bool
    /// 是否带分组蒙版（v6 起）
    let groupMask: Bool
    /// 是否带调整层（v7 起）
    let adjustment: Bool
    /// 是否带分组非 1 不透明度 + 参考线（v8 起）
    let guidesGroupOpacity: Bool
    /// 是否带模糊调整（v9 起）
    let blur: Bool
    /// 是否带文字颜色（v10 起）
    let textColors: Bool
    /// 是否带文字字体 + maskLinked + shape/effects（v11）
    let textFonts: Bool
}

enum Ids {
    static let documentID = UUID(uuidString: "0C5E7A91-3B2D-4F6A-8E1C-9D0B7A6F5E4D")!
    static let base   = UUID(uuidString: "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F")!
    static let clip1  = UUID(uuidString: "B1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let clip2  = UUID(uuidString: "C1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let folder = UUID(uuidString: "D1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let child  = UUID(uuidString: "E1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let adjust = UUID(uuidString: "F1B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let noise  = UUID(uuidString: "F5B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let blur   = UUID(uuidString: "F2B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let shape  = UUID(uuidString: "F3B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
    static let title  = UUID(uuidString: "F4B2C3D4-E5F6-4A7B-8C9D-0E1F2A3B4C5D")!
}

/// 11 个版本的清单。字段集必须与 Windows 侧的
/// `make-version-fixtures.ps1` 里的 `$Plans` 逐项对应。
enum SampleSet {
    static let plans: [VersionPlan] = [
    .init(version: 1,  name: "v01-groups-pending",      groups: false, opacityBlend: false, layerMask: false, liveMask: false, groupMask: false, adjustment: false, guidesGroupOpacity: false, blur: false, textColors: false, textFonts: false),
    .init(version: 2,  name: "v02-groups",              groups: true,  opacityBlend: false, layerMask: false, liveMask: false, groupMask: false, adjustment: false, guidesGroupOpacity: false, blur: false, textColors: false, textFonts: false),
    .init(version: 3,  name: "v03-opacity-blend",       groups: false, opacityBlend: true,  layerMask: false, liveMask: false, groupMask: false, adjustment: false, guidesGroupOpacity: false, blur: false, textColors: false, textFonts: false),
    .init(version: 4,  name: "v04-layer-mask",          groups: false, opacityBlend: false, layerMask: true,  liveMask: false, groupMask: false, adjustment: false, guidesGroupOpacity: false, blur: false, textColors: false, textFonts: false),
    .init(version: 5,  name: "v05-live-mask",           groups: false, opacityBlend: false, layerMask: false, liveMask: true,  groupMask: false, adjustment: false, guidesGroupOpacity: false, blur: false, textColors: false, textFonts: false),
    .init(version: 6,  name: "v06-group-mask",          groups: true,  opacityBlend: false, layerMask: true,  liveMask: false, groupMask: true,  adjustment: false, guidesGroupOpacity: false, blur: false, textColors: false, textFonts: false),
    .init(version: 7,  name: "v07-adjustment",          groups: false, opacityBlend: false, layerMask: false, liveMask: false, groupMask: false, adjustment: true,  guidesGroupOpacity: false, blur: false, textColors: false, textFonts: false),
    .init(version: 8,  name: "v08-guides-group-opacity", groups: true, opacityBlend: false, layerMask: true,  liveMask: false, groupMask: true,  adjustment: false, guidesGroupOpacity: true,  blur: false, textColors: false, textFonts: false),
    .init(version: 9,  name: "v09-blur-noise",          groups: false, opacityBlend: false, layerMask: false, liveMask: false, groupMask: false, adjustment: true,  guidesGroupOpacity: true,  blur: true,  textColors: false, textFonts: false),
    .init(version: 10, name: "v10-text-colors",         groups: false, opacityBlend: false, layerMask: false, liveMask: false, groupMask: false, adjustment: false, guidesGroupOpacity: true,  blur: false, textColors: true,  textFonts: false),
    .init(version: 11, name: "v11-everything",          groups: false, opacityBlend: false, layerMask: true,  liveMask: false, groupMask: false, adjustment: false, guidesGroupOpacity: true,  blur: false, textColors: true,  textFonts: true),
    ]
}

// ─────────────────────────────────────────────────────────────────────
// 四个「容易被构造错」的类型：字段来源行号逐个钉住，改之前先回源码核对
// ─────────────────────────────────────────────────────────────────────
//
// 这些都是 macOS 侧的类型，构造时**不得猜字段**。本文件用的每个值都出自：
//
//   ImportedImage(image:thumbnail:name:)      IO/ImageImporter.swift:7-13
//     └ ProjectStore.save 只读 .image（:91-105），thumbnail 不进 .comp 包，
//       所以这里让 thumbnail 复用同一张图：不影响产出字节。
//       ⚠️ 构造签名必须覆盖 raster 之前的所有字段（它是 var + 默认值）。
//
//   LayerAdjustment(kind:)                   Document/LayerAdjustment.swift:45-119
//     └ 唯一无默认值的字段是 kind，其余全部有默认值。
//       isValid（:128-141）**不按 kind 分支**——它一次校验全部 settings，
//       所以默认值必须整体合法，不能只看当前 kind 那几项。
//       .gaussianBlur / .motionBlur / .addNoise 额外要求 version >= 9
//       （ProjectStore.swift:217-219）。
//
//   LayerTextStyle()                          Document/TypeTool.swift:7-34
//     └ 全部字段有默认值。colorRuns（逐字颜色，UTF-16 偏移）需 v10、
//       fontRuns（逐字字体）需 v11（ProjectStore.swift:210-213）。
//       ⚠️ text 图层**必须有 imageFile**（:213），否则 save 直接抛。
//
//   LayerShapeStyle(kind:red:green:blue:cornerRadius:)
//                                              Document/ShapeTool.swift:19-32
//     └ 这五个字段无默认值，lineWidth / start / end 为可选。
//
//   LayerEffects(stroke:)                     Document/LayerEffects.swift:6-22, 127-134
//     └ 六个效果全为可选且默认 nil。StrokeEffect 的默认值（size 4、
//       opacity 1）已在 isValid（:18-21）的闭区间内。
//
// 这里**故意不自己造 manifest 字节**：save() 只接受 snapshot + 资产字典，
// manifest 由 ProjectStore.save 从 snapshot 生成 —— 那才是"Mac 实际会写什么"。
// 手写 manifest 再调 JSONEncoder 得到的字节，未必是 Mac 存出来的字节。
//
// ⚠️ save 是**同步**函数（ProjectStore.swift:84），调用处不要写 await。

func makeSolidImage(width: Int, height: Int, alpha: CGFloat = 1) -> CGImage? {
    guard let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
        space: CGColorSpaceCreateDeviceRGB(),
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    ) else { return nil }

    context.setFillColor(CGColor(red: 0.2, green: 0.4, blue: 0.8, alpha: alpha))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    return context.makeImage()
}

/// 8 位无 alpha 的灰度图。蒙版必须是这个形状（LayerMask.swift:23-26）。
func makeMaskImage(width: Int, height: Int) -> CGImage? {
    guard let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width,
        space: CGColorSpaceCreateDeviceGray(),
        bitmapInfo: CGImageAlphaInfo.none.rawValue
    ) else { return nil }

    context.setFillColor(gray: 1, alpha: 1)
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    return context.makeImage()
}

func imported(_ image: CGImage, name: String) -> ImportedImage {
    // 缩略图：Mac 侧自己会算（ProjectStore.swift:184-188）。
    // 这里直接复用原图即可 —— 它只进内存，不进 .comp 包。
    ImportedImage(image: image, thumbnail: image, name: name)
}

func transform(_ width: Double, _ height: Double) -> LayerTransform {
    // 🔴 字段名逐字取自 LayerTransform.swift:19-24 —— 是 `rotation` 不是 `rotationDegrees`，
    //    采样档是 `.high`（原始值 "High quality"，LayerTransform.swift:7）不是 `.highQuality`。
    LayerTransform(origin: CGPoint(x: 0, y: 0), size: CGSize(width: width, height: height),
                    rotation: 0, flipX: false, flipY: false, sampling: .high)
}

/// 逐个版本构造 snapshot 并保存。
/// 🔴 ProjectStore 是 **actor**（ProjectStore.swift:77），方法签名虽是同步的
/// （:84），但 actor 隔离要求调用侧写 await —— 少一个 await 编译不过。
func dumpAll(to outputDirectory: URL) async throws {
    try FileManager.default.createDirectory(at: outputDirectory, withIntermediateDirectories: true)

    for plan in SampleSet.plans {
        // manifest 故意留到 layers 建完之后再构造 —— documentID / width / height /
        // activeLayerID 全是 `let`（ProjectStore.swift:24-27），一旦构造就不能再赋值。

        var images: [UUID: ImportedImage] = [:]
        var masks: [UUID: ImportedImage] = [:]
        var layers: [ProjectLayerRecord] = []

        func addBase() {
            guard let image = makeSolidImage(width: 100, height: 80, alpha: 0.5) else { return }
            images[Ids.base] = imported(image, name: "Base")
            layers.append(ProjectLayerRecord(id: Ids.base, name: "Base", isVisible: true,
                                             transform: transform(100, 80),
                                             imageFile: "\(Ids.base.uuidString).png"))
        }

        addBase()

        if plan.groups {
            // 🔴 `let imageFile: String?` 没有 `= nil` 默认值（ProjectStore.swift:38），
            //    所以 Swift 的 memberwise init 仍把它当必填 —— 组图层必须显式传 nil。
            var folder = ProjectLayerRecord(id: Ids.folder, name: "Folder", isVisible: true,
                                            transform: transform(100, 80), imageFile: nil, isGroup: true)
            if plan.groupMask, let mask = makeMaskImage(width: 100, height: 80) {
                masks[Ids.folder] = imported(mask, name: "Folder Mask")
                folder.maskFile = "\(Ids.folder.uuidString).mask.png"
                folder.maskEnabled = true
            }
            if plan.guidesGroupOpacity { folder.opacity = 0.5 }
            layers.append(folder)

            guard let childImage = makeSolidImage(width: 60, height: 60, alpha: 0.75) else { continue }
            images[Ids.child] = imported(childImage, name: "Child")
            var child = ProjectLayerRecord(id: Ids.child, name: "Child", isVisible: true,
                                           transform: transform(60, 60),
                                           imageFile: "\(Ids.child.uuidString).png")
            child.parentID = Ids.folder
            if plan.layerMask, let mask = makeMaskImage(width: 50, height: 50) {
                masks[Ids.child] = imported(mask, name: "Child Mask")
                child.maskFile = "\(Ids.child.uuidString).mask.png"
                child.maskEnabled = true
                if plan.textFonts { child.maskLinked = false }
            }
            layers.append(child)
        } else if plan.layerMask {
            guard let childImage = makeSolidImage(width: 60, height: 60, alpha: 0.75) else { continue }
            images[Ids.child] = imported(childImage, name: "Child")
            var child = ProjectLayerRecord(id: Ids.child, name: "Child", isVisible: true,
                                           transform: transform(60, 60),
                                           imageFile: "\(Ids.child.uuidString).png")
            if plan.textFonts {
                child.maskLinked = false
            }
            layers.append(child)
        }

        if plan.opacityBlend {
            guard let clipImage = makeSolidImage(width: 50, height: 50) else { continue }
            images[Ids.clip2] = imported(clipImage, name: "Clip2")
            layers.append(ProjectLayerRecord(id: Ids.clip2, name: "Clip2", isVisible: true,
                                             transform: transform(50, 50),
                                             imageFile: "\(Ids.clip2.uuidString).png",
                                             opacity: 0.8, blendMode: .multiply))
        }

        if plan.liveMask {
            guard let clipImage = makeSolidImage(width: 50, height: 50) else { continue }
            images[Ids.clip1] = imported(clipImage, name: "Clip1")
            layers.append(ProjectLayerRecord(id: Ids.clip1, name: "Clip1", isVisible: true,
                                             transform: transform(50, 50),
                                             imageFile: "\(Ids.clip1.uuidString).png",
                                             maskSourceID: Ids.base))
        }

        // 调整层（v7 起；模糊/噪声类 v9 起）。字段来源见文件头的索引块。
        //
        // ⚠️ 调整层**没有像素**：ProjectStore.swift:216 要求 adjustment 非 nil 时
        // imageFile 必须为 nil，反之亦然。所以不能给它配图。
        if plan.adjustment {
            func addAdjustment(_ id: UUID, _ kind: AdjustmentKind) {
                var layer = ProjectLayerRecord(id: id, name: kind.rawValue, isVisible: true,
                                               transform: transform(100, 80), imageFile: nil)
                layer.adjustment = LayerAdjustment(kind: kind)
                layers.append(layer)
            }

            // 两边只放**一层** adjustment：kind 由该版本决定。
            // 模糊类 kind 在 v<9 会被拒收（ProjectStore.swift:217-219），
            // 所以 v7 用 .hsv、v9 用 .gaussianBlur。
            // 不放 .addNoise：noise* 五个键在 Mac 侧是 Optional，写不写都合法，
            // 多放一层不带来额外覆盖，却会让两边的层数对不上、diff 噪音变大。
            // v9 的样本名虽然叫 "v09-blur-noise"，等真机跑出 ground-truth
            // 再决定要不要补 noise 层。
            addAdjustment(Ids.adjust, plan.blur ? .gaussianBlur : .hsv)
        }

        // 文字图层（v10 起颜色 run，v11 起字体 run）。
        //
        // ⚠️ text 图层**必须**有像素（ProjectStore.swift:213），与调整层相反。
        // content 用 ASCII：非 ASCII 会撞上 System.Text.Json 与 JSONEncoder 的
        // 转义差异（已记为 T3 已知差异），混进互通基线会掩盖别的差异。
        if plan.textColors {
            guard let textImage = makeSolidImage(width: 100, height: 80, alpha: 0.9) else { continue }
            images[Ids.title] = imported(textImage, name: "Title")
            var style = LayerTextStyle()
            style.content = "Title"
            style.fontSize = 24
            style.red = 0.1
            style.green = 0.2
            style.blue = 0.3

            // UTF-16 偏移；"Title" 有 5 个 UTF-16 单元。
            style.colorRuns = [LayerTextColorRun(location: 0, length: 2, red: 1, green: 0, blue: 0)]

            if plan.textFonts {
                style.fontRuns = [LayerTextFontRun(location: 2, length: 3, fontName: "Helvetica-Bold")]
            }

            var title = ProjectLayerRecord(id: Ids.title, name: "Title", isVisible: true,
                                           transform: transform(100, 80),
                                           imageFile: "\(Ids.title.uuidString).png")
            title.text = style
            layers.append(title)
        }

        // 形状图层 + 描边（v11）。
        //
        // ⚠️ ProjectStore.validate 对 shape / effects 没有 isValid 检查
        // （:207-233 只看 text / adjustment / mask / opacity / blend），
        // 这两个字段能过校验靠的是 Codable 本身。但形状图层在 Mac 里
        // 本来就有像素，这里照实配一张。
        if plan.textFonts {
            guard let shapeImage = makeSolidImage(width: 40, height: 40, alpha: 1) else { continue }
            images[Ids.shape] = imported(shapeImage, name: "Shape")
            var shapeLayer = ProjectLayerRecord(id: Ids.shape, name: "Shape", isVisible: true,
                                                transform: transform(40, 40),
                                                imageFile: "\(Ids.shape.uuidString).png")
            shapeLayer.shape = LayerShapeStyle(kind: .rectangle, red: 0.9, green: 0.4, blue: 0.1, cornerRadius: 6)
            shapeLayer.effects = LayerEffects(stroke: StrokeEffect(size: 3, red: 0, green: 0, blue: 0))
            layers.append(shapeLayer)
        }

        // layers 已经建完，这时才能确定 activeLayerID。四个必填成员一次给全，
        // 之后只碰 version / resolution / guides 这三个 var 属性。
        var manifest = ProjectManifest(documentID: Ids.documentID, width: 100, height: 80,
                                       activeLayerID: layers.last?.id, layers: layers)
        manifest.version = plan.version
        manifest.resolution = plan.version >= 8 ? 300 : 72

        if plan.guidesGroupOpacity {
            manifest.guides = [
                CanvasGuide(id: UUID(uuidString: "11111111-2222-4333-8444-555555555555")!,
                            axis: .horizontal, position: 20),
                CanvasGuide(id: UUID(uuidString: "11111111-2222-4333-8444-666666666666")!,
                            axis: .vertical, position: 40),
            ]
        }

        let destination = outputDirectory.appendingPathComponent("\(plan.name).comp")
        // actor 隔离：签名同步也要 await，见 dumpAll 的注释。
        try await ProjectStore.shared.save(
            ProjectSnapshot(manifest: manifest, images: images, masks: masks),
            to: destination
        )
        print("  v\(plan.version) \(plan.name) -> \(destination.lastPathComponent)")
    }
}

/// 写一份摘要，便于 Windows 侧快速比对字节是否一致。
func writeDigest(of directory: URL) throws {
    var lines: [String] = []

    for package in try FileManager.default.contentsOfDirectory(
        at: directory, includingPropertiesForKeys: nil).sorted(by: { $0.path < $1.path }) {

        var entries: [(String, Int, String)] = []
        let enumerator = FileManager.default.enumerator(at: package, includingPropertiesForKeys: nil)

        while let file = enumerator?.nextObject() as? URL {
            let attrs = try FileManager.default.attributesOfItem(atPath: file.path)
            let size = (attrs[.size] as? Int) ?? 0
            let data = try Data(contentsOf: file)
            let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            let name = file.path.replacingOccurrences(of: package.path + "/", with: "")
            entries.append((name, size, hash))
        }

        lines.append(package.lastPathComponent)
        for (name, size, hash) in entries.sorted(by: { $0.0 < $1.0 }) {
            lines.append("  \(name)  \(size)  \(hash)")
        }
    }

    try lines.joined(separator: "\n").write(
        to: directory.appendingPathComponent("digest.txt"), atomically: true, encoding: .utf8)
}

// ─────────────────────────────────────────────────────────────────────
// 入口点
// ─────────────────────────────────────────────────────────────────────
//
// 上游是 **Xcode 项目**（Compositor.xcodeproj），不是 SwiftPM —— 没有
// Package.swift。所以本文件不是 `swift run` 能跑的主程序，必须挂进
// CompositorTests 目标，由 swift-testing 的 @Test 调起。
// 拷进 CompositorTests/ 后跑：
//
//   xcodebuild test -project Compositor.xcodeproj -scheme Compositor \
//     -destination 'platform=macOS' -only-testing:CompositorTests/DumpCompFixturesTests
//
// 产物目录：CI 用 COMPOSITOR_DUMP_DIR 指定（artifact 要从这里取）；
// 不给就落到临时目录，本地调试不会污染仓库。

@MainActor
struct DumpCompFixturesTests {
    /// 产出目录。优先环境变量，回落临时目录。
    static func outputDirectory() -> URL {
        if let raw = ProcessInfo.processInfo.environment["COMPOSITOR_DUMP_DIR"], !raw.isEmpty {
            return URL(fileURLWithPath: raw, isDirectory: true)
        }
        return URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("compositor-comp-dump", isDirectory: true)
    }

    /// 产出 11 个版本的 .comp + digest.txt。
    ///
    /// 这一步是整条互通验证链的起点：**Windows 侧那批夹具是手搓的**，
    /// 这里产出的才是「Mac 实际会写什么」的 ground-truth。两者的逐字段
    /// 差异表反过来就是 Windows 实现的规格书。
    @Test func dumpFixturesToDisk() async throws {
        let directory = Self.outputDirectory()

        // 先清空：上一轮的残留会让 digest 里出现幽灵条目。
        if FileManager.default.fileExists(atPath: directory.path) {
            try FileManager.default.removeItem(at: directory)
        }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        try await dumpAll(to: directory)
        try writeDigest(of: directory)

        // 自检：11 个包必须都在，且每个都带 manifest.json。
        // 少一个就让 Windows 侧的比对静默少一项，那比直接失败更难查。
        let packages = try FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "comp" }
        #expect(packages.count == SampleSet.plans.count,
                "应产出 \(SampleSet.plans.count) 个 .comp，实际 \(packages.count)")

        for package in packages {
            let manifest = package.appendingPathComponent("manifest.json")
            #expect(FileManager.default.fileExists(atPath: manifest.path),
                    "\(package.lastPathComponent) 缺 manifest.json")
        }

        print("dump -> \(directory.path)")
    }
}