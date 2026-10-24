import AppKit
import Foundation

guard CommandLine.arguments.count == 2 else {
    fputs("Usage: swift make-icon.swift <directory.iconset>\n", stderr)
    exit(2)
}

let outputURL = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: outputURL, withIntermediateDirectories: true)

func drawIcon(pixels: Int) throws -> Data {
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
        isPlanar: false, colorSpaceName: .deviceRGB,
        bytesPerRow: 0, bitsPerPixel: 0
    ), let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        throw NSError(domain: "IconBuild", code: 1)
    }
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    defer { NSGraphicsContext.restoreGraphicsState() }
    let transform = NSAffineTransform()
    transform.scale(by: CGFloat(pixels) / 1024)
    transform.concat()

    NSColor(srgbRed: 153 / 255, green: 44 / 255, blue: 73 / 255, alpha: 1).setFill()
    NSBezierPath(roundedRect: NSRect(x: 64, y: 64, width: 896, height: 896),
                 xRadius: 202, yRadius: 202).fill()
    NSColor(srgbRed: 1, green: 0.976, blue: 0.957, alpha: 1).setFill()
    NSBezierPath(roundedRect: NSRect(x: 264, y: 222, width: 496, height: 584),
                 xRadius: 64, yRadius: 64).fill()

    NSColor(srgbRed: 153 / 255, green: 44 / 255, blue: 73 / 255, alpha: 1).setFill()
    for y in [640.0, 508.0, 376.0] {
        NSBezierPath(roundedRect: NSRect(x: 332, y: y, width: 58, height: 58),
                     xRadius: 16, yRadius: 16).fill()
        NSBezierPath(roundedRect: NSRect(x: 424, y: y + 12, width: 260, height: 34),
                     xRadius: 17, yRadius: 17).fill()
    }
    guard let png = bitmap.representation(using: .png, properties: [:]) else {
        throw NSError(domain: "IconBuild", code: 2)
    }
    return png
}

for size in [16, 32, 128, 256, 512] {
    try drawIcon(pixels: size).write(to: outputURL.appendingPathComponent("icon_\(size)x\(size).png"))
    try drawIcon(pixels: size * 2).write(to: outputURL.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
