import AppKit
import CoreGraphics
import Foundation
import Testing
@testable import Compositor

// ════════════════════════════════════════════════════════════════════════════
// 🔴 Compositor for Windows · Tier 2 黄金样本生成器 · 波次 A（混合模式）
//
// 用途：把 Mac 版 24 个混合模式的**实际输出像素**固化成 JSON，供 Windows 手写
// W3C 实现逐像素对账。这是 Tier 3「原理性无法验证」里唯一还能被量化的部分。
//
// 本文件**不属于上游仓库**，由本项目的 GitHub Actions workflow 在 checkout 之后
// 拷入 `CompositorTests/`。上游 `Compositor.xcodeproj` 是 objectVersion 77 +
// PBXFileSystemSynchronizedRootGroup，所以拷进去即自动纳入测试 target，
// **不需要改 project.pbxproj**。
//
// 只写文件、不断言。测试失败只应发生在"环境坏了"的时候。
//
// ⚠️ Swift Testing 的 @Test 跑在**实例**上，所以下面全部是实例成员，不是 static。
//    写成 static 会得到 "static member cannot be used on instance"。
// ════════════════════════════════════════════════════════════════════════════

@MainActor
struct CompositorBlendGoldenDumpTests {
    /// 32×32。够大到能区分 pinLight / hardMix 这类非线性模式，
    /// 又小到 24 个模式加起来 hex 后只有约 200 KB。
    let side = 32

    /// 输出目录。workflow 用环境变量指到 artifact 目录；本地跑则落到相对路径。
    var outputDirectory: URL {
        if let raw = ProcessInfo.processInfo.environment["COMPOSITOR_FIXTURE_DIR"], !raw.isEmpty {
            return URL(fileURLWithPath: raw, isDirectory: true)
        }
        return URL(fileURLWithPath: "Fixtures", isDirectory: true)
            .appendingPathComponent("compositor-blend-golden", isDirectory: true)
    }

    // MARK: - 测试图案（确定性，无随机）

    /// 前景图案。alpha 沿 X 轴递增 —— 这一点是必须的：
    /// `LayerAppearance.swift:5-7` 说 Core Graphics 的 Color Burn / Color Dodge
    /// "ignore how transparent the source is"，只有前景带 alpha 梯度才测得出这个差异。
    func sourcePixels() -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: side * side * 4)
        for y in 0..<side {
            for x in 0..<side {
                let o = (y * side + x) * 4
                bytes[o + 0] = UInt8(x * 255 / (side - 1))             // R ← X
                bytes[o + 1] = UInt8(y * 255 / (side - 1))             // G ← Y
                bytes[o + 2] = UInt8((x + y) * 255 / (2 * (side - 1)))  // B ← X+Y
                bytes[o + 3] = UInt8(x * 255 / (side - 1))             // A ← X（梯度）
            }
        }
        return bytes
    }

    /// 背景图案。全不透明，让混合公式本身保持干净可读。
    func backdropPixels() -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: side * side * 4)
        for y in 0..<side {
            for x in 0..<side {
                let o = (y * side + x) * 4
                bytes[o + 0] = UInt8(255 - x * 255 / (side - 1))
                bytes[o + 1] = UInt8(255 - y * 255 / (side - 1))
                bytes[o + 2] = UInt8((x * y) % 256)
                bytes[o + 3] = 255
            }
        }
        return bytes
    }

    // MARK: - 位图工具

    var srgb: CGColorSpace { CGColorSpace(name: CGColorSpace.sRGB)! }

    /// 与 `SeparableBlend.swift:26-28` 完全一致的位图格式：
    /// 8bpc、premultipliedLast、byteOrder32Big —— 即 RGBA8 预乘。
    func makeContext() throws -> CGContext {
        guard let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8,
                                  bytesPerRow: side * 4, space: srgb,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                                    | CGBitmapInfo.byteOrder32Big.rawValue)
        else { throw DumpError.contextCreationFailed }
        return ctx
    }

    func image(fromRgba bytes: [UInt8]) throws -> CGImage {
        let provider = CGDataProvider(data: Data(bytes) as CFData)!
        guard let image = CGImage(width: side, height: side, bitsPerComponent: 8, bitsPerPixel: 32,
                                  bytesPerRow: side * 4, space: srgb,
                                  bitmapInfo: CGBitmapInfo(rawValue:
                                    CGImageAlphaInfo.premultipliedLast.rawValue
                                    | CGBitmapInfo.byteOrder32Big.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: false,
                                  intent: .defaultIntent)
        else { throw DumpError.imageCreationFailed }
        return image
    }

    func readback(_ ctx: CGContext) throws -> [UInt8] {
        guard let raw = ctx.data else { throw DumpError.contextHasNoBackingStore }
        let ptr = raw.assumingMemoryBound(to: UInt8.self)
        return Array(UnsafeBufferPointer(start: ptr, count: side * side * 4))
    }

    let hexDigits: [Character] = Array("0123456789abcdef")

    func hex(_ bytes: [UInt8]) -> String {
        var out = String()
        out.reserveCapacity(bytes.count * 2)
        for byte in bytes {
            out.append(hexDigits[Int(byte >> 4)])
            out.append(hexDigits[Int(byte & 0x0F)])
        }
        return out
    }

    enum DumpError: Error { case contextCreationFailed, imageCreationFailed, contextHasNoBackingStore }

    // MARK: - 主测试

    /// 对全部 24 个混合模式各跑一遍真实路径，把输出像素写进 JSON。
    @Test func dumpBlendModeGoldenPixels() throws {
        let backdropBase = backdropPixels()
        var records: [BlendRecord] = []

        for mode in LayerBlendMode.allCases {
            // 每次都从**同一份**背景重新开始 —— 上一个模式的输出不能污染下一个。
            let ctx = try makeContext()
            ctx.draw(try image(fromRgba: backdropBase), in: CGRect(x: 0, y: 0, width: side, height: side))

            let source = try image(fromRgba: sourcePixels())
            let viaCoreImage = SeparableBlend.needsSurface(mode)

            if viaCoreImage {
                // 走 Mac 版真实路径：SeparableBlend.blend 直接写回 ctx.data
                let ok = SeparableBlend.blend(source, over: ctx, mode: mode)
                #expect(ok, "SeparableBlend.blend returned false for \(mode.rawValue)")
            } else {
                // 走 Core Graphics：先画前景，关掉 alpha，走 CGBlendMode
                ctx.saveGState()
                ctx.setBlendMode(mode.cgMode)
                ctx.setAlpha(1)
                ctx.draw(source, in: CGRect(x: 0, y: 0, width: side, height: side))
                ctx.restoreGState()
            }

            records.append(BlendRecord(
                name: mode.rawValue,
                caseName: String(describing: mode),
                path: viaCoreImage ? "coreImage" : "coreGraphics",
                coreImageFilter: mode.coreImageFilter,
                cgModeRawValue: Int(mode.cgMode.rawValue),
                pixelsHex: hex(try readback(ctx))
            ))
        }

        #expect(records.count == 24, "LayerBlendMode should have exactly 24 members, got \(records.count)")

        let ciCount = LayerBlendMode.allCases.filter { $0.coreImageFilter != nil }.count
        let document = BlendGoldenDocument(
            schema: "compositor-windows.blend-golden/1",
            producedBy: "CompositorBlendGoldenDumpTests",
            upstreamRef: "Compositor-main (MIT, robbietilton/Compositor)",
            note: """
            rowOrder = "contextBottomLeftOrigin": read straight out of CGContext.data, \
            never through a CGImage, so rows are in Core Graphics' bottom-left origin order, \
            not an image file's top-left origin.
            Each record holds side*side*4 = \(side * side * 4) bytes, RGBA8 premultiplied, sRGB.
            path = "coreImage" (\(ciCount) modes) can never match a hand-written W3C formula: \
            the implementation is closed-source CoreImage.
            path = "coreGraphics" (\(LayerBlendMode.allCases.count - ciCount) modes) is closed-source \
            CoreGraphics, so whether it matches W3C cannot be decided from the source - these pixels \
            are what makes that difference measurable.
            """,
            side: side,
            bytesPerPixel: 4,
            colorSpace: "sRGB",
            premultiplied: true,
            sourcePatternHex: hex(sourcePixels()),
            backdropPatternHex: hex(backdropBase),
            modes: records
        )

        let directory = outputDirectory
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(document)

        let url = directory.appendingPathComponent("blend-golden.json")
        try data.write(to: url, options: .atomic)

        // 同时留一份纯文本清单，方便人不打开 JSON 就看到 24 个模式的归类。
        var summary = "mode | case | path | coreImageFilter | cgMode\n"
        for record in records {
            summary += "\(record.name) | \(record.caseName) | \(record.path) | "
            summary += "\(record.coreImageFilter ?? "-") | \(record.cgModeRawValue)\n"
        }
        try summary.write(
            to: directory.appendingPathComponent("blend-golden-summary.tsv"),
            atomically: true, encoding: .utf8)

        print("FIXTURE_WRITTEN \(url.path) bytes=\(data.count) modes=\(records.count)")
    }

    // MARK: - DTO

    struct BlendRecord: Codable {
        let name: String
        let caseName: String
        let path: String
        let coreImageFilter: String?
        let cgModeRawValue: Int
        let pixelsHex: String
    }

    struct BlendGoldenDocument: Codable {
        let schema: String
        let producedBy: String
        let upstreamRef: String
        let note: String
        let side: Int
        let bytesPerPixel: Int
        let colorSpace: String
        let premultiplied: Bool
        let sourcePatternHex: String
        let backdropPatternHex: String
        let modes: [BlendRecord]
    }
}