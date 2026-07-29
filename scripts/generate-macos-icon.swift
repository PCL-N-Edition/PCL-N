#!/usr/bin/env swift

import AppKit
import Foundation

private let arguments = CommandLine.arguments
guard arguments.count == 3 else {
    FileHandle.standardError.write(
        Data("usage: generate-macos-icon.swift <source.png> <output.png>\n".utf8))
    exit(64)
}

let sourcePath = arguments[1]
let outputPath = arguments[2]
guard let source = NSImage(contentsOfFile: sourcePath) else {
    FileHandle.standardError.write(Data("cannot read icon source: \(sourcePath)\n".utf8))
    exit(66)
}

let canvasSize = NSSize(width: 1024, height: 1024)
let canvas = NSImage(size: canvasSize, flipped: false) { bounds in
    NSColor.clear.setFill()
    bounds.fill(using: .copy)

    let tileRect = NSRect(x: 64, y: 64, width: 896, height: 896)
    let tile = NSBezierPath(roundedRect: tileRect, xRadius: 200, yRadius: 200)

    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = NSColor.black.withAlphaComponent(0.16)
    shadow.shadowBlurRadius = 28
    shadow.shadowOffset = NSSize(width: 0, height: -14)
    shadow.set()
    NSColor.white.setFill()
    tile.fill()
    NSGraphicsContext.restoreGraphicsState()

    NSColor(calibratedWhite: 0.82, alpha: 0.55).setStroke()
    tile.lineWidth = 1
    tile.stroke()

    NSGraphicsContext.current?.imageInterpolation = .high
    let logoRect = NSRect(x: 218, y: 218, width: 588, height: 588)
    source.draw(
        in: logoRect,
        from: .zero,
        operation: .sourceOver,
        fraction: 1,
        respectFlipped: true,
        hints: nil)
    return true
}

guard let tiff = canvas.tiffRepresentation,
      let bitmap = NSBitmapImageRep(data: tiff),
      let png = bitmap.representation(using: .png, properties: [:]) else {
    FileHandle.standardError.write(Data("cannot encode generated macOS icon\n".utf8))
    exit(70)
}

let outputUrl = URL(fileURLWithPath: outputPath)
try FileManager.default.createDirectory(
    at: outputUrl.deletingLastPathComponent(),
    withIntermediateDirectories: true)
try png.write(to: outputUrl, options: .atomic)
