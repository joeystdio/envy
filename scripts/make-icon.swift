// Draws the app icon (a glowing hexagon around a keyhole) and writes <out>/AppIcon.icns.
// Usage: swift scripts/make-icon.swift Resources
import AppKit

let outDir = URL(fileURLWithPath: CommandLine.arguments.dropFirst().first ?? "Resources")
let green = NSColor(srgbRed: 0.24, green: 1.0, blue: 0.62, alpha: 1)

func render(_ pixels: Int) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8, samplesPerPixel: 4,
        hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current!.cgContext.scaleBy(x: CGFloat(pixels) / 1024, y: CGFloat(pixels) / 1024)

    // Background on Apple's 824pt icon grid.
    let background = NSBezierPath(roundedRect: NSRect(x: 100, y: 100, width: 824, height: 824), xRadius: 186, yRadius: 186)
    NSGradient(
        starting: NSColor(srgbRed: 0.11, green: 0.14, blue: 0.19, alpha: 1),
        ending: NSColor(srgbRed: 0.03, green: 0.04, blue: 0.06, alpha: 1)
    )!.draw(in: background, angle: -90)

    let glow = NSShadow()
    glow.shadowColor = green.withAlphaComponent(0.75)
    glow.shadowBlurRadius = 42
    glow.shadowOffset = .zero
    glow.set()

    let center = NSPoint(x: 512, y: 512)
    let hexagon = NSBezierPath()
    for i in 0..<6 {
        let angle = CGFloat(i) * .pi / 3 + .pi / 2
        let point = NSPoint(x: center.x + 272 * cos(angle), y: center.y + 272 * sin(angle))
        i == 0 ? hexagon.move(to: point) : hexagon.line(to: point)
    }
    hexagon.close()
    hexagon.lineWidth = 46
    hexagon.lineJoinStyle = .round
    green.setStroke()
    hexagon.stroke()

    let keyhole = NSBezierPath(ovalIn: NSRect(x: 512 - 64, y: 552 - 64, width: 128, height: 128))
    let stem = NSBezierPath()
    stem.move(to: NSPoint(x: 512 - 30, y: 560))
    stem.line(to: NSPoint(x: 512 + 30, y: 560))
    stem.line(to: NSPoint(x: 512 + 58, y: 404))
    stem.line(to: NSPoint(x: 512 - 58, y: 404))
    stem.close()
    green.setFill()
    // Filled separately: as one path the opposite windings of the two shapes would cancel where they overlap.
    keyhole.fill()
    stem.fill()

    NSGraphicsContext.restoreGraphicsState()
    return rep
}

let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("Envy-\(UUID().uuidString).iconset")
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = scale == 1 ? "icon_\(points)x\(points).png" : "icon_\(points)x\(points)@2x.png"
        try render(points * scale).representation(using: .png, properties: [:])!.write(to: iconset.appendingPathComponent(name))
    }
}
try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", outDir.appendingPathComponent("AppIcon.icns").path]
try iconutil.run()
iconutil.waitUntilExit()
try render(1024).representation(using: .png, properties: [:])!.write(to: outDir.appendingPathComponent("AppIcon.png"))
try? FileManager.default.removeItem(at: iconset)
print("Wrote \(outDir.path)/AppIcon.icns")
