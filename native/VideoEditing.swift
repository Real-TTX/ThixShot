import AVFoundation
import CoreImage
import ImageIO
import UniformTypeIdentifiers

struct VideoZoom: Codable {
    let startSeconds: Double
    let durationSeconds: Double
    let x: Double
    let y: Double
    let scale: Double
}

// A click or a keystroke of a recording (InputEvent in C#): kind "Click" or "Typing", position 0..1 from the upper left.
struct InputRecord: Codable {
    let seconds: Double
    let kind: String
    let x: Double
    let y: Double
}

// A click whose ring the export draws.
struct ClickMark: Codable {
    let seconds: Double
    let x: Double
    let y: Double
}

// What MacVideoEditor.ExportAsync sends: the zooms and the clicks.
private struct ExportPlan: Codable {
    let zooms: [VideoZoom]
    let clicks: [ClickMark]
}

// ClickEffect in AutoZoom.cs: the same timing, size and colours.
private enum ClickEffect {
    static let duration = 0.6, radius = 0.03
    static let red = 0x2F / 255.0, green = 0x6F / 255.0, blue = 0xED / 255.0
    static let fillAlpha = 70 / 255.0, ringAlpha = 235 / 255.0, haloAlpha = 210 / 255.0
    static let ringWidth = 0.16, haloWidth = 0.07

    static func at(_ seconds: Double, _ clicks: [ClickMark]) -> [(x: Double, y: Double, radius: Double, opacity: Double)] {
        clicks.compactMap { click in
            let age = seconds - click.seconds
            guard age >= 0, age < duration else { return nil }
            let progress = age / duration
            let grow = 1 - pow(1 - progress, 3)
            return (click.x, click.y, radius * (0.35 + 0.65 * grow), pow(1 - progress, 1.5))
        }
    }
}

private struct MovieInfo: Codable {
    let durationSeconds: Double
    let width: Int
    let height: Int
}

enum VideoEditing {
    static func run(_ args: [String], source: URL) async throws {
        let asset = AVURLAsset(url: source)
        switch args[0] {
        case "inspect":
            let info = try await inspect(asset)
            FileHandle.standardOutput.write(try JSONEncoder().encode(info))
        case "frame":
            guard args.count == 4, let seconds = Double(args[2]), seconds.isFinite, seconds >= 0 else {
                throw CaptureError("Ungültiger Zeitpunkt.")
            }
            let generator = AVAssetImageGenerator(asset: asset)
            generator.appliesPreferredTrackTransform = true
            generator.requestedTimeToleranceBefore = .zero
            generator.requestedTimeToleranceAfter = .zero
            let length = try await asset.load(.duration).seconds
            let time = CMTime(seconds: min(seconds, max(0, length - 1 / 600)), preferredTimescale: 60_000)
            let frame = try await generator.image(at: time).image
            guard let destination = CGImageDestinationCreateWithURL(URL(fileURLWithPath: args[3]) as CFURL, UTType.png.identifier as CFString, 1, nil)
                else { throw CaptureError("Das Vorschaubild konnte nicht geöffnet werden.") }
            CGImageDestinationAddImage(destination, frame, nil)
            guard CGImageDestinationFinalize(destination) else { throw CaptureError("Das Vorschaubild konnte nicht gespeichert werden.") }
        case "export":
            guard args.count == 3 else { throw CaptureError("Das Exportziel fehlt.") }
            let plan = try JSONDecoder().decode(ExportPlan.self, from: FileHandle.standardInput.readDataToEndOfFile())
            try await export(asset: asset, target: URL(fileURLWithPath: args[2]), points: plan.zooms, clicks: plan.clicks, report: true)
        default: throw CaptureError("Unbekannter Videobefehl.")
        }
    }

    private static func inspect(_ asset: AVAsset) async throws -> MovieInfo {
        guard let video = try await asset.loadTracks(withMediaType: .video).first else { throw CaptureError("Die Datei enthält kein Video.") }
        let naturalSize = try await video.load(.naturalSize)
        let transform = try await video.load(.preferredTransform)
        let bounds = CGRect(origin: .zero, size: naturalSize).applying(transform)
        let duration = try await asset.load(.duration).seconds
        guard duration.isFinite, duration > 0, bounds.width >= 1, bounds.height >= 1 else { throw CaptureError("Ungültige Videodaten.") }
        return MovieInfo(durationSeconds: duration, width: Int(bounds.width.rounded()), height: Int(bounds.height.rounded()))
    }

    // Match VideoEditService.ViewportAtSorted: the weighted mean of the zooms by the sum of their weights (at most 1), so two
    // zooms that overlap by one edge pan. Returns the part of the picture as 0..1 from the upper left.
    private static func viewport(_ seconds: Double, _ points: [VideoZoom]) -> (x: Double, y: Double, size: Double) {
        var weights = 0.0, x = 0.0, y = 0.0, size = 0.0
        for point in points {
            let age = seconds - point.startSeconds
            if age < 0 { break }
            if age >= point.durationSeconds { continue }
            let edge = min(0.5, point.durationSeconds / 2)
            let fraction = min(1, min(age, point.durationSeconds - age) / edge)
            let weight = fraction * fraction * (3 - 2 * fraction)
            let target = 1 / point.scale
            weights += weight
            x += min(1 - target, max(0, point.x - target / 2)) * weight
            y += min(1 - target, max(0, point.y - target / 2)) * weight
            size += target * weight
        }
        if weights <= 0 { return (0, 0, 1) }
        let strength = min(1, weights)
        return (x / weights * strength, y / weights * strength, 1 + (size / weights - 1) * strength)
    }

    // Core Image uses a lower-left origin.
    private static func crop(_ view: (x: Double, y: Double, size: Double), _ extent: CGRect) -> CGRect {
        CGRect(x: extent.minX + extent.width * view.x, y: extent.minY + extent.height * (1 - view.y - view.size),
               width: extent.width * view.size, height: extent.height * view.size)
    }

    // The rings of the clicks visible at this moment, drawn into the rendered BGRA frame (upper-left coordinates flipped).
    private static func drawClicks(_ seconds: Double, _ clicks: [ClickMark], view: (x: Double, y: Double, size: Double),
                                   into pixels: CVPixelBuffer, width: Int, height: Int) {
        let rings = ClickEffect.at(seconds, clicks)
        if rings.isEmpty { return }
        CVPixelBufferLockBaseAddress(pixels, [])
        defer { CVPixelBufferUnlockBaseAddress(pixels, []) }
        guard let base = CVPixelBufferGetBaseAddress(pixels),
              let context = CGContext(data: base, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: CVPixelBufferGetBytesPerRow(pixels), space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return }
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        let shorter = Double(min(width, height))
        for ring in rings {
            let r = ring.radius * shorter / view.size
            let cx = (ring.x - view.x) / view.size * Double(width), cy = (ring.y - view.y) / view.size * Double(height)
            let stroke = max(1.5, r * ClickEffect.ringWidth), halo = max(1, r * ClickEffect.haloWidth)
            let circle = CGRect(x: cx - r, y: cy - r, width: 2 * r, height: 2 * r)
            context.setFillColor(red: ClickEffect.red, green: ClickEffect.green, blue: ClickEffect.blue, alpha: ClickEffect.fillAlpha * ring.opacity)
            context.fillEllipse(in: circle)
            context.setStrokeColor(red: ClickEffect.red, green: ClickEffect.green, blue: ClickEffect.blue, alpha: ClickEffect.ringAlpha * ring.opacity)
            context.setLineWidth(stroke)
            context.strokeEllipse(in: circle)
            let outer = r + stroke / 2 + halo / 2
            context.setStrokeColor(red: 1, green: 1, blue: 1, alpha: ClickEffect.haloAlpha * ring.opacity)
            context.setLineWidth(halo)
            context.strokeEllipse(in: CGRect(x: cx - outer, y: cy - outer, width: 2 * outer, height: 2 * outer))
        }
    }

    private static func export(asset: AVAsset, target: URL, points: [VideoZoom], clicks: [ClickMark], report: Bool) async throws {
        let info = try await inspect(asset)
        guard let videoTrack = try await asset.loadTracks(withMediaType: .video).first else { throw CaptureError("Die Videospur fehlt.") }
        let transform = try await videoTrack.load(.preferredTransform)
        let width = max(2, info.width / 2 * 2), height = max(2, info.height / 2 * 2)
        let reader = try AVAssetReader(asset: asset)
        let frames = AVAssetReaderTrackOutput(track: videoTrack, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA
        ])
        frames.alwaysCopiesSampleData = false
        guard reader.canAdd(frames) else { throw CaptureError("Der Videodecoder ist nicht verfügbar.") }
        reader.add(frames)
        let writer = try AVAssetWriter(outputURL: target, fileType: .mp4)
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: width, AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: max(750_000, min(12_000_000, width * height * 4)),
                                              AVVideoExpectedSourceFrameRateKey: 30]
        ])
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: video, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA, kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height, kCVPixelBufferIOSurfacePropertiesKey as String: [:] as [String: Any]
        ])
        guard writer.canAdd(video) else { throw CaptureError("Der H.264-Encoder ist nicht verfügbar.") }
        writer.add(video)
        let audioReader: AVAssetReaderTrackOutput?
        let audioWriter: AVAssetWriterInput?
        if let track = try await asset.loadTracks(withMediaType: .audio).first, let format = try await track.load(.formatDescriptions).first {
            let from = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
            let to = AVAssetWriterInput(mediaType: .audio, outputSettings: nil, sourceFormatHint: format)
            guard reader.canAdd(from), writer.canAdd(to) else { throw CaptureError("Die Audiospur kann nicht übernommen werden.") }
            reader.add(from); writer.add(to)
            audioReader = from; audioWriter = to
        } else {
            audioReader = nil; audioWriter = nil
        }
        guard reader.startReading() else { throw reader.error ?? CaptureError("Das Video konnte nicht geöffnet werden.") }
        guard writer.startWriting() else { throw writer.error ?? CaptureError("Der Export ist fehlgeschlagen.") }
        writer.startSession(atSourceTime: .zero)
        let context = CIContext(options: [.cacheIntermediates: false])
        let outputBounds = CGRect(x: 0, y: 0, width: width, height: height)
        let duration = try await asset.load(.duration)
        var next = frames.copyNextSampleBuffer()
        var current: CIImage?
        async let audioResult: Void = copyAudio(audioReader, audioWriter, writer)
        var number: Int64 = 0
        while CMTime(value: number, timescale: 30) < duration {
            let time = CMTime(value: number, timescale: 30)
            while let sample = next, current == nil || sample.presentationTimeStamp <= time {
                guard let pixels = sample.imageBuffer else { throw CaptureError("Ungültiges Videobild.") }
                current = CIImage(cvPixelBuffer: pixels).transformed(by: transform)
                next = frames.copyNextSampleBuffer()
            }
            guard let current else { throw reader.error ?? CaptureError("Die Videospur ist leer.") }
            try await waitForInput(video, writer)
            try autoreleasepool {
                var pixels: CVPixelBuffer?
                guard let pool = adaptor.pixelBufferPool, CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pixels) == kCVReturnSuccess, let pixels else {
                    throw CaptureError("Der Exportpuffer ist nicht verfügbar.")
                }
                let view = viewport(time.seconds, points)
                let area = crop(view, current.extent)
                let image = current.cropped(to: area).transformed(by: CGAffineTransform(translationX: -area.minX, y: -area.minY))
                    .transformed(by: CGAffineTransform(scaleX: outputBounds.width / area.width, y: outputBounds.height / area.height))
                context.render(image, to: pixels, bounds: outputBounds, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
                drawClicks(time.seconds, clicks, view: view, into: pixels, width: width, height: height)
                guard adaptor.append(pixels, withPresentationTime: time) else { throw writer.error ?? CaptureError("Ein Videobild konnte nicht gespeichert werden.") }
            }
            if report && number % 6 == 0 {
                FileHandle.standardOutput.write(Data("PROGRESS \(time.seconds / info.durationSeconds)\n".utf8))
            }
            number += 1
        }
        video.markAsFinished()
        try await audioResult
        if let error = reader.error { throw error }
        writer.endSession(atSourceTime: duration)
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? CaptureError("Der Export ist fehlgeschlagen.") }
    }

    // Supply each writer input independently. In particular a source may start playing
    // system audio much later than its first video frame.
    private static func copyAudio(_ from: AVAssetReaderTrackOutput?, _ to: AVAssetWriterInput?, _ writer: AVAssetWriter) async throws {
        guard let from, let to else { return }
        defer { to.markAsFinished() }
        while let sample = from.copyNextSampleBuffer() {
            try await waitForInput(to, writer)
            guard to.append(sample) else { throw writer.error ?? CaptureError("Die Audiospur konnte nicht kopiert werden.") }
        }
    }

    private static func waitForInput(_ input: AVAssetWriterInput, _ writer: AVAssetWriter) async throws {
        while !input.isReadyForMoreMediaData {
            if writer.status != .writing { throw writer.error ?? CaptureError("Der Export wurde unterbrochen.") }
            try await Task.sleep(nanoseconds: 2_000_000)
        }
    }

    static func verify(source: URL) async throws {
        let directory = source.deletingLastPathComponent()
        let id = UUID().uuidString
        let output = directory.appendingPathComponent("zoom-\(id).mp4")
        let tone = directory.appendingPathComponent("audio-\(id).m4a")
        let original = directory.appendingPathComponent("original-\(id).mp4")
        defer { for file in [output, tone, original] { try? FileManager.default.removeItem(at: file) } }
        try writeTestTone(to: tone)
        let combined = AVMutableComposition()
        let testVideo = AVURLAsset(url: source), testAudio = AVURLAsset(url: tone)
        let duration = try await testVideo.load(.duration)
        for (asset, kind) in [(testVideo, AVMediaType.video), (testAudio, AVMediaType.audio)] {
            guard let track = try await asset.loadTracks(withMediaType: kind).first,
                  let target = combined.addMutableTrack(withMediaType: kind, preferredTrackID: kCMPersistentTrackID_Invalid)
                else { throw CaptureError("Die Testspur fehlt.") }
            try target.insertTimeRange(CMTimeRange(start: .zero, duration: duration), of: track, at: .zero)
        }
        guard let mux = AVAssetExportSession(asset: combined, presetName: AVAssetExportPresetPassthrough) else {
            throw CaptureError("Der Testclip konnte nicht erstellt werden.")
        }
        if #available(macOS 15, *) {
            try await mux.export(to: original, as: .mp4)
        } else {
            mux.outputURL = original
            mux.outputFileType = .mp4
            await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
                mux.exportAsynchronously { done.resume() }
            }
            guard mux.status == .completed else { throw mux.error ?? CaptureError("Der Testclip konnte nicht geöffnet werden.") }
        }
        let input = AVURLAsset(url: original)
        try await export(asset: input, target: output,
                         points: [VideoZoom(startSeconds: 0, durationSeconds: 0.2, x: 0.5, y: 0, scale: 2)], clicks: [], report: false)
        let asset = AVURLAsset(url: output)
        let generator = AVAssetImageGenerator(asset: asset)
        generator.requestedTimeToleranceAfter = .zero
        generator.requestedTimeToleranceBefore = .zero
        let image = try await generator.image(at: CMTime(value: 3, timescale: 30)).image
        var pixel = [UInt8](repeating: 0, count: 4)
        CIContext().render(CIImage(cgImage: image), toBitmap: &pixel, rowBytes: 4,
                           bounds: CGRect(x: 160, y: 15, width: 1, height: 1), format: .RGBA8,
                           colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
        guard pixel[2] > pixel[0] else { throw CaptureError("Der Export enthält den gewählten Zoom nicht.") }
        let originalAudio = try await audioBytes(input), exportedAudio = try await audioBytes(asset)
        guard !originalAudio.isEmpty, originalAudio == exportedAudio else { throw CaptureError("AAC-Audiodaten beim Export verändert.") }
    }

    private static func writeTestTone(to url: URL) throws {
        let settings: [String: Any] = [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000,
                                       AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 128_000]
        let file = try AVAudioFile(forWriting: url, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: 24_000),
              let channels = buffer.floatChannelData else { throw CaptureError("Der Audio-Testpuffer fehlt.") }
        buffer.frameLength = 24_000
        for i in 0..<Int(buffer.frameLength) {
            let value = Float(0.4 * sin(2 * Double.pi * 440 * Double(i) / 48_000))
            channels[0][i] = value
            channels[1][i] = value
        }
        try file.write(from: buffer)
    }

    private static func audioBytes(_ asset: AVAsset) async throws -> Data {
        guard let track = try await asset.loadTracks(withMediaType: .audio).first else { throw CaptureError("Die Test-Audiospur fehlt.") }
        let reader = try AVAssetReader(asset: asset)
        let samples = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
        reader.add(samples)
        guard reader.startReading() else { throw reader.error ?? CaptureError("Testaudio ist nicht lesbar.") }
        var result = Data()
        while let sample = samples.copyNextSampleBuffer(), let block = sample.dataBuffer {
            let length = CMBlockBufferGetDataLength(block)
            var bytes = [UInt8](repeating: 0, count: length)
            guard CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: &bytes) == kCMBlockBufferNoErr
                else { throw CaptureError("Ungültige AAC-Testdaten.") }
            result.append(contentsOf: bytes)
        }
        return result
    }
}
