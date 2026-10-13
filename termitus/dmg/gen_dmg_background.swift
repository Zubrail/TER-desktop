// Generates the TER .dmg window background (app icon on the left, arrow,
// Applications on the right). Run: swift gen_dmg_background.swift <outdir>
// Produces dmg-background.png (600x400) and dmg-background@2x.png (1200x800);
// package-ter-osx.sh merges them into a retina .tiff for create-dmg.
// Icon positions used by package-ter-osx.sh: app at (150,190), Applications at (450,190).
import AppKit

let outDir = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "."

func render(scale: CGFloat, to path: String) {
    let w = 600 * scale, h = 400 * scale
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(w), pixelsHigh: Int(h),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    ctx.scaleBy(x: scale, y: scale)

    // Background: soft warm vertical gradient (TER design D, light).
    let bg = NSGradient(starting: NSColor(calibratedRed: 1.0, green: 0.976, blue: 0.95, alpha: 1),
                        ending: NSColor(calibratedRed: 0.98, green: 0.93, blue: 0.89, alpha: 1))!
    bg.draw(in: NSRect(x: 0, y: 0, width: 600, height: 400), angle: 90)

    // Arrow between the two icons (Finder y=190 from top -> 210 from bottom).
    let amber = NSColor(calibratedRed: 0.96, green: 0.62, blue: 0.20, alpha: 1)
    let coral = NSColor(calibratedRed: 0.93, green: 0.36, blue: 0.33, alpha: 1)
    let y: CGFloat = 210
    let arrow = NSBezierPath()
    arrow.move(to: NSPoint(x: 245, y: y - 7))
    arrow.line(to: NSPoint(x: 330, y: y - 7))
    arrow.line(to: NSPoint(x: 330, y: y - 20))
    arrow.line(to: NSPoint(x: 360, y: y))
    arrow.line(to: NSPoint(x: 330, y: y + 20))
    arrow.line(to: NSPoint(x: 330, y: y + 7))
    arrow.line(to: NSPoint(x: 245, y: y + 7))
    arrow.close()
    NSGradient(starting: amber, ending: coral)!.draw(in: arrow, angle: 0)

    // Captions.
    let para = NSMutableParagraphStyle(); para.alignment = .center
    let title: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 20, weight: .semibold),
        .foregroundColor: NSColor(calibratedWhite: 0.15, alpha: 1), .paragraphStyle: para]
    let hint: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 13, weight: .regular),
        .foregroundColor: NSColor(calibratedWhite: 0.40, alpha: 1), .paragraphStyle: para]
    ("Установка TER" as NSString).draw(in: NSRect(x: 0, y: 340, width: 600, height: 30), withAttributes: title)
    ("Перетащите значок TER в папку «Программы»" as NSString)
        .draw(in: NSRect(x: 0, y: 70, width: 600, height: 20), withAttributes: hint)
    ("Drag TER to Applications" as NSString)
        .draw(in: NSRect(x: 0, y: 50, width: 600, height: 20), withAttributes: hint)

    NSGraphicsContext.restoreGraphicsState()
    let data = rep.representation(using: .png, properties: [:])!
    try! data.write(to: URL(fileURLWithPath: path))
}

render(scale: 1, to: "\(outDir)/dmg-background.png")
render(scale: 2, to: "\(outDir)/dmg-background@2x.png")
print("ok")
