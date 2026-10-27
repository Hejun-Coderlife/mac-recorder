// 画 录屏.app 的图标：深色圆角底 + 屏幕取景框 + 红色录制圆点，输出 1024px PNG
// 用法：swift tools/make_icon.swift out.png
import AppKit

let S: CGFloat = 1024
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon_1024.png"
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(S), pixelsHigh: Int(S),
                           bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                           colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

// macOS 图标网格：内容区 824px，四周留 100px
let body = CGRect(x: 100, y: 100, width: 824, height: 824)
let shape = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

// 阴影 + 深色渐变底
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: NSColor(white: 0, alpha: 0.35).cgColor)
ctx.addPath(shape); ctx.setFillColor(NSColor.black.cgColor); ctx.fillPath()
ctx.restoreGState()
ctx.saveGState()
ctx.addPath(shape); ctx.clip()
let bg = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                    colors: [NSColor(red: 0.20, green: 0.21, blue: 0.25, alpha: 1).cgColor,
                             NSColor(red: 0.07, green: 0.07, blue: 0.09, alpha: 1).cgColor] as CFArray,
                    locations: [0, 1])!
ctx.drawLinearGradient(bg, start: CGPoint(x: 0, y: 924), end: CGPoint(x: 0, y: 100), options: [])
ctx.restoreGState()

// 取景框四个角
let c = CGPoint(x: S / 2, y: S / 2)
let half: CGFloat = 270, arm: CGFloat = 95
ctx.setStrokeColor(NSColor(white: 1, alpha: 0.9).cgColor)
ctx.setLineWidth(30); ctx.setLineCap(.round); ctx.setLineJoin(.round)
for (sx, sy) in [(-1.0, -1.0), (1, -1), (-1, 1), (1, 1)] as [(CGFloat, CGFloat)] {
    let corner = CGPoint(x: c.x + sx * half, y: c.y + sy * half)
    ctx.move(to: CGPoint(x: corner.x, y: corner.y - sy * arm))
    ctx.addLine(to: corner)
    ctx.addLine(to: CGPoint(x: corner.x - sx * arm, y: corner.y))
}
ctx.strokePath()

// 红色录制圆点（带光晕和高光）
let r: CGFloat = 150
ctx.saveGState()
ctx.setShadow(offset: .zero, blur: 70, color: NSColor(red: 1, green: 0.2, blue: 0.2, alpha: 0.7).cgColor)
ctx.setFillColor(NSColor(red: 1, green: 0.23, blue: 0.19, alpha: 1).cgColor)
ctx.fillEllipse(in: CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r))
ctx.restoreGState()
ctx.saveGState()
ctx.addEllipse(in: CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r)); ctx.clip()
let dot = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                     colors: [NSColor(red: 1, green: 0.45, blue: 0.40, alpha: 1).cgColor,
                              NSColor(red: 0.85, green: 0.10, blue: 0.10, alpha: 1).cgColor] as CFArray,
                     locations: [0, 1])!
ctx.drawRadialGradient(dot, startCenter: CGPoint(x: c.x - 50, y: c.y + 60), startRadius: 0,
                       endCenter: c, endRadius: r, options: [.drawsAfterEndLocation])
ctx.restoreGState()

NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
