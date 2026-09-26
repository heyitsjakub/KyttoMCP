// Draws Kytto's icon and writes every size the app needs.
//
// The design came out of an image generator (`AppIcon-source.png`, kept beside
// this file as the reference). It is redrawn here rather than resampled, because
// a 1024px picture is the wrong source for a 16px icon: the corners have to be
// genuinely transparent, the squircle has to be Apple's continuous curve rather
// than an approximation of it, and every cell has to sit exactly on the grid —
// none of which survives a downscale of a raster that was only nearly aligned.
//
// Code is also the honest source for geometry this simple. Change a number here,
// run it, and the app icon and the menu bar glyph both follow:
//
//     swiftc -O Design/MakeIcons.swift -o /tmp/makeicons && /tmp/makeicons
//
// Run from the repository root; it writes into KyttoMCP/Assets.xcassets.

import AppKit
import CoreGraphics
import Foundation

// MARK: - Design

enum Design {
    /// Apple's macOS grid: an 824pt body centred in a 1024pt canvas, corner
    /// radius 185.4. The margin is not padding to be trimmed — the system
    /// expects the icon to sit inside it.
    static let canvas: CGFloat = 1024
    static let body: CGFloat = 824
    static let bodyRadius: CGFloat = 185.4

    static let faceTop = CGColor(red: 0.118, green: 0.118, blue: 0.145, alpha: 1) // #1E1E25
    static let faceBottom = CGColor(red: 0.059, green: 0.059, blue: 0.075, alpha: 1) // #0F0F13
    static let green = CGColor(red: 0.247, green: 0.725, blue: 0.314, alpha: 1) // #3FB950
    static let outline = CGColor(red: 0.290, green: 0.290, blue: 0.333, alpha: 1) // #4A4A55

    /// The 3×3 grid, as fractions of the body.
    static let gridSpan: CGFloat = 0.60
    static let cellFraction: CGFloat = 165.0 / 635.0 // measured off the reference
    static let cellRadiusFraction: CGFloat = 0.24
    static let strokeFraction: CGFloat = 0.065

    /// Filled cells: the diagonal, top-left to bottom-right.
    static let filled: Set<Int> = [0, 4, 8]
}

// MARK: - Drawing

/// Apple's continuous corner curvature, taken from the real implementation
/// rather than approximated: a layer knows the shape, and rendering it into a
/// grey context turns it into a mask everything else can be clipped to.
func squircleMask(side: CGFloat, radius: CGFloat) -> CGImage {
    let pixels = Int(side.rounded())
    let context = CGContext(
        data: nil,
        width: pixels,
        height: pixels,
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpaceCreateDeviceGray(),
        bitmapInfo: CGImageAlphaInfo.none.rawValue
    )!
    context.setFillColor(gray: 0, alpha: 1)
    context.fill(CGRect(x: 0, y: 0, width: side, height: side))

    let layer = CALayer()
    layer.frame = CGRect(x: 0, y: 0, width: side, height: side)
    layer.cornerRadius = radius
    layer.cornerCurve = .continuous
    layer.backgroundColor = CGColor(gray: 1, alpha: 1)
    layer.render(in: context)

    return context.makeImage()!
}

/// A mask that is opaque at the top and gone by `fadesBy` of the way down.
/// Clipping the edge highlight to the top half with a rectangle instead leaves
/// the stroke cut off mid-edge, which reads as a nick in the silhouette.
func topFadeMask(side: CGFloat, fadesBy: CGFloat) -> CGImage {
    let pixels = Int(side.rounded())
    let context = CGContext(
        data: nil,
        width: pixels,
        height: pixels,
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpaceCreateDeviceGray(),
        bitmapInfo: CGImageAlphaInfo.none.rawValue
    )!
    let gradient = CGGradient(
        colorsSpace: CGColorSpaceCreateDeviceGray(),
        colors: [CGColor(gray: 0, alpha: 1), CGColor(gray: 1, alpha: 1)] as CFArray,
        locations: [1 - fadesBy, 1]
    )!
    context.drawLinearGradient(
        gradient,
        start: CGPoint(x: 0, y: 0),
        end: CGPoint(x: 0, y: side),
        options: [.drawsBeforeStartLocation, .drawsAfterEndLocation]
    )
    return context.makeImage()!
}

/// A rounded rectangle with the same continuous curvature as the body, so the
/// cells are the icon in miniature rather than a different shape inside it.
func continuousRoundedPath(in rect: CGRect, radius: CGFloat) -> CGPath {
    // The control-point offset that turns a circular corner into Apple's
    // continuous one. Below ~1/2 of the side the difference is all in the
    // shoulders, which is exactly where the eye reads a rounded square.
    let r = min(radius, min(rect.width, rect.height) / 2)
    let c = r * 1.28
    let path = CGMutablePath()

    path.move(to: CGPoint(x: rect.minX + r, y: rect.minY))
    path.addLine(to: CGPoint(x: rect.maxX - r, y: rect.minY))
    path.addCurve(
        to: CGPoint(x: rect.maxX, y: rect.minY + r),
        control1: CGPoint(x: rect.maxX - r + c * 0.55, y: rect.minY),
        control2: CGPoint(x: rect.maxX, y: rect.minY + r - c * 0.55)
    )
    path.addLine(to: CGPoint(x: rect.maxX, y: rect.maxY - r))
    path.addCurve(
        to: CGPoint(x: rect.maxX - r, y: rect.maxY),
        control1: CGPoint(x: rect.maxX, y: rect.maxY - r + c * 0.55),
        control2: CGPoint(x: rect.maxX - r + c * 0.55, y: rect.maxY)
    )
    path.addLine(to: CGPoint(x: rect.minX + r, y: rect.maxY))
    path.addCurve(
        to: CGPoint(x: rect.minX, y: rect.maxY - r),
        control1: CGPoint(x: rect.minX + r - c * 0.55, y: rect.maxY),
        control2: CGPoint(x: rect.minX, y: rect.maxY - r + c * 0.55)
    )
    path.addLine(to: CGPoint(x: rect.minX, y: rect.minY + r))
    path.addCurve(
        to: CGPoint(x: rect.minX + r, y: rect.minY),
        control1: CGPoint(x: rect.minX, y: rect.minY + r - c * 0.55),
        control2: CGPoint(x: rect.minX + r - c * 0.55, y: rect.minY)
    )
    path.closeSubpath()
    return path
}

func bitmapContext(side: Int) -> CGContext {
    let context = CGContext(
        data: nil,
        width: side,
        height: side,
        bitsPerComponent: 8,
        bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    )!
    context.setAllowsAntialiasing(true)
    context.setShouldAntialias(true)
    context.interpolationQuality = .high
    return context
}

/// Grid rectangles for a body of `side`, in a context whose origin is bottom
/// left. Index 0 is top left, so the filled diagonal runs the way it is read.
func cellRects(bodySide: CGFloat, origin: CGPoint) -> [CGRect] {
    let span = bodySide * Design.gridSpan
    let cell = span * Design.cellFraction
    let gap = (span - cell * 3) / 2
    let left = origin.x + (bodySide - span) / 2
    let top = origin.y + (bodySide + span) / 2

    return (0..<9).map { index in
        let column = CGFloat(index % 3)
        let row = CGFloat(index / 3)
        return CGRect(
            x: left + column * (cell + gap),
            y: top - row * (cell + gap) - cell,
            width: cell,
            height: cell
        )
    }
}

/// The full icon at any size. `simplified` drops the outlined cells, which is
/// what the 16px rendition needs: a cell is two pixels there, and nine of them
/// are a smudge, where three green squares on a dark tile still read as Kytto.
/// From 32px up the grid survives and carries the identity, so it stays.
func drawIcon(side: CGFloat, simplified: Bool) -> CGImage {
    let scale = side / Design.canvas
    let context = bitmapContext(side: Int(side))

    let bodySide = Design.body * scale
    let origin = CGPoint(x: (side - bodySide) / 2, y: (side - bodySide) / 2)
    let bodyRect = CGRect(x: origin.x, y: origin.y, width: bodySide, height: bodySide)

    // The body, drawn once into its own layer so the shadow follows the
    // silhouette instead of every shape inside it. Below 64px the shadow is
    // under a pixel wide and all it contributes is a grey lip around the
    // corners, so the small renditions go without — as Apple's own do.
    let bodyImage = drawBody(bodySide: bodySide, simplified: simplified)
    context.saveGState()
    if side > 32 {
        context.setShadow(
            offset: CGSize(width: 0, height: -10 * scale),
            blur: 26 * scale,
            color: CGColor(gray: 0, alpha: 0.30)
        )
    }
    context.draw(bodyImage, in: bodyRect)
    context.restoreGState()

    return context.makeImage()!
}

func drawBody(bodySide: CGFloat, simplified: Bool) -> CGImage {
    let pixels = Int(bodySide.rounded())
    let context = bitmapContext(side: pixels)
    let side = CGFloat(pixels)
    let scale = side / Design.body

    context.saveGState()
    let mask = squircleMask(side: side, radius: Design.bodyRadius * scale)
    context.clip(to: CGRect(x: 0, y: 0, width: side, height: side), mask: mask)

    // Face: lighter at the top, the way a surface tilted into the light reads.
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    let gradient = CGGradient(
        colorsSpace: space,
        colors: [Design.faceBottom, Design.faceTop] as CFArray,
        locations: [0, 1]
    )!
    context.drawLinearGradient(
        gradient,
        start: CGPoint(x: 0, y: 0),
        end: CGPoint(x: 0, y: side),
        options: []
    )

    // Cells.
    let rects = cellRects(bodySide: side, origin: .zero)
    let cellSide = rects[0].width
    let radius = cellSide * Design.cellRadiusFraction
    let stroke = max(cellSide * Design.strokeFraction, 1)

    for (index, rect) in rects.enumerated() {
        let path = continuousRoundedPath(in: rect, radius: radius)
        if Design.filled.contains(index) {
            context.addPath(path)
            context.setFillColor(Design.green)
            context.fillPath()
        } else if !simplified {
            context.addPath(path.copy(
                strokingWithWidth: stroke,
                lineCap: .round,
                lineJoin: .round,
                miterLimit: 10
            ))
            context.setFillColor(Design.outline)
            context.fillPath()
        }
    }

    // A hairline along the top edge only: the one highlight, where light would
    // actually catch.
    context.saveGState()
    context.clip(
        to: CGRect(x: 0, y: 0, width: side, height: side),
        mask: topFadeMask(side: side, fadesBy: 0.55)
    )
    context.addPath(continuousRoundedPath(
        in: CGRect(x: 0, y: 0, width: side, height: side),
        radius: Design.bodyRadius * scale
    ))
    context.setStrokeColor(CGColor(gray: 1, alpha: 0.12))
    context.setLineWidth(3 * scale)
    context.strokePath()
    context.restoreGState()

    context.restoreGState()
    return context.makeImage()!
}

/// The menu bar glyph. A template image: alpha only, no colour — macOS inverts
/// it for dark and light bars and dims it when the app is inactive, and a
/// full-colour icon up there would do none of that (§7.7).
func drawMenuBarGlyph(side: CGFloat) -> CGImage {
    let context = bitmapContext(side: Int(side))

    // Tighter than the app icon's grid: the glyph has no tile around it, so the
    // padding has to come from inside. The ring is thin on purpose — what makes
    // the diagonal readable at 18pt is the contrast between a solid cell and a
    // hollow one, and a fat ring closes that gap.
    let span = side * 0.82
    let cell = span * Design.cellFraction
    let gap = (span - cell * 3) / 2
    let left = (side - span) / 2
    let top = (side + span) / 2
    let radius = cell * Design.cellRadiusFraction
    let stroke = max(cell * 0.18, 1)

    for index in 0..<9 {
        let column = CGFloat(index % 3)
        let row = CGFloat(index / 3)
        let rect = CGRect(
            x: left + column * (cell + gap),
            y: top - row * (cell + gap) - cell,
            width: cell,
            height: cell
        )
        let path = continuousRoundedPath(in: rect, radius: radius)
        context.setFillColor(CGColor(gray: 0, alpha: 1))
        if Design.filled.contains(index) {
            context.addPath(path)
        } else {
            context.addPath(path.copy(
                strokingWithWidth: stroke,
                lineCap: .round,
                lineJoin: .round,
                miterLimit: 10
            ))
        }
        context.fillPath()
    }

    return context.makeImage()!
}

// MARK: - Output

func write(_ image: CGImage, to url: URL) {
    let rep = NSBitmapImageRep(cgImage: image)
    rep.size = NSSize(width: image.width, height: image.height)
    guard let data = rep.representation(using: .png, properties: [:]) else {
        fatalError("could not encode \(url.lastPathComponent)")
    }
    try! data.write(to: url)
    print("  \(url.lastPathComponent)  \(image.width)×\(image.height)")
}

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let appIconSet = root.appending(path: "KyttoMCP/Assets.xcassets/AppIcon.appiconset")
let menuBarSet = root.appending(path: "KyttoMCP/Assets.xcassets/MenuBarIcon.imageset")
try? FileManager.default.createDirectory(at: menuBarSet, withIntermediateDirectories: true)

/// The ten entries macOS asks for, as (point size, scale).
let appIconSizes: [(Int, Int)] = [
    (16, 1), (16, 2),
    (32, 1), (32, 2),
    (128, 1), (128, 2),
    (256, 1), (256, 2),
    (512, 1), (512, 2),
]

print("App icon")
var appIconEntries: [[String: String]] = []
for (points, scale) in appIconSizes {
    let pixels = points * scale
    let name = scale == 1 ? "icon_\(points)x\(points).png" : "icon_\(points)x\(points)@2x.png"
    write(drawIcon(side: CGFloat(pixels), simplified: pixels <= 16), to: appIconSet.appending(path: name))
    appIconEntries.append([
        "idiom": "mac",
        "size": "\(points)x\(points)",
        "scale": "\(scale)x",
        "filename": name,
    ])
}

print("Menu bar glyph")
for scale in 1...2 {
    let name = scale == 1 ? "menubar.png" : "menubar@2x.png"
    write(drawMenuBarGlyph(side: CGFloat(18 * scale)), to: menuBarSet.appending(path: name))
}

print("Reference render")
write(drawIcon(side: 1024, simplified: false), to: root.appending(path: "Design/AppIcon-1024.png"))

func writeJSON(_ object: Any, to url: URL) {
    let data = try! JSONSerialization.data(
        withJSONObject: object,
        options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
    )
    try! (String(data: data, encoding: .utf8)! + "\n").data(using: .utf8)!.write(to: url)
}

writeJSON(
    ["images": appIconEntries, "info": ["author": "kytto", "version": 1]],
    to: appIconSet.appending(path: "Contents.json")
)

writeJSON(
    [
        "images": [
            ["idiom": "mac", "scale": "1x", "filename": "menubar.png"],
            ["idiom": "mac", "scale": "2x", "filename": "menubar@2x.png"],
        ],
        "info": ["author": "kytto", "version": 1],
        "properties": ["template-rendering-intent": "template"],
    ],
    to: menuBarSet.appending(path: "Contents.json")
)

print("done")
