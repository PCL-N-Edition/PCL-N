#!/usr/bin/env swift

import AppKit
import Foundation

private let arguments = CommandLine.arguments
guard arguments.count == 2 else {
    FileHandle.standardError.write(
        Data("usage: generate-macos-dmg-background.swift <output.png>\n".utf8))
    exit(64)
}

let outputPath = arguments[1]
let canvasSize = NSSize(width: 660, height: 400)
let canvas = NSImage(size: canvasSize, flipped: false) { bounds in
    let gradient = NSGradient(colors: [
        NSColor(calibratedRed: 0.965, green: 0.976, blue: 0.992, alpha: 1),
        NSColor(calibratedRed: 0.925, green: 0.953, blue: 0.988, alpha: 1)
    ])
    gradient?.draw(in: bounds, angle: -90)

    let titleStyle = NSMutableParagraphStyle()
    titleStyle.alignment = .center
    let titleAttributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 20, weight: .semibold),
        .foregroundColor: NSColor(calibratedWhite: 0.12, alpha: 1),
        .paragraphStyle: titleStyle
    ]
    let subtitleAttributes: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 13, weight: .regular),
        .foregroundColor: NSColor(calibratedWhite: 0.33, alpha: 1),
        .paragraphStyle: titleStyle
    ]

    NSString(string: "拖动 PCL N 到“应用程序”即可安装").draw(
        in: NSRect(x: 40, y: 338, width: 580, height: 28),
        withAttributes: titleAttributes)
    NSString(string: "Drag PCL N to Applications to install").draw(
        in: NSRect(x: 40, y: 314, width: 580, height: 20),
        withAttributes: subtitleAttributes)

    let arrowColor = NSColor(calibratedRed: 0.23, green: 0.55, blue: 0.91, alpha: 0.82)
    arrowColor.setStroke()
    arrowColor.setFill()

    let arrow = NSBezierPath()
    arrow.lineWidth = 5
    arrow.lineCapStyle = .round
    arrow.lineJoinStyle = .round
    arrow.move(to: NSPoint(x: 274, y: 188))
    arrow.line(to: NSPoint(x: 386, y: 188))
    arrow.stroke()

    let head = NSBezierPath()
    head.move(to: NSPoint(x: 386, y: 188))
    head.line(to: NSPoint(x: 366, y: 202))
    head.line(to: NSPoint(x: 366, y: 174))
    head.close()
    head.fill()

    return true
}

guard let tiff = canvas.tiffRepresentation,
      let bitmap = NSBitmapImageRep(data: tiff),
      let png = bitmap.representation(using: .png, properties: [:]) else {
    FileHandle.standardError.write(Data("cannot encode DMG background\n".utf8))
    exit(70)
}

let outputUrl = URL(fileURLWithPath: outputPath)
try FileManager.default.createDirectory(
    at: outputUrl.deletingLastPathComponent(),
    withIntermediateDirectories: true)
try png.write(to: outputUrl, options: .atomic)
