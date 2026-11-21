import AVFoundation
import CoreImage
import CoreGraphics
import ImageIO
import ScreenCaptureKit
import UniformTypeIdentifiers

private struct CaptureError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

private func fail(_ error: Error, code: Int32 = 1) -> Never {
    FileHandle.standardError.write(Data((error.localizedDescription + "\n").utf8))
    exit(code)
}

// Every writer callback runs on the stream's serial sample queue.
private final class MovieOutput: NSObject, SCStreamOutput, SCStreamDelegate {
    let writer: AVAssetWriter
    let video: AVAssetWriterInput
    let audio: AVAssetWriterInput?
    let adaptor: AVAssetWriterInputPixelBufferAdaptor
    let context = CIContext(options: [.cacheIntermediates: false])
    let outputSize: CGSize
    let screenRect: CGRect
    let focusEnabled: Bool
    let firstScreenFrame = DispatchSemaphore(value: 0)
    var sessionStart: CMTime?
    var lastVideoTime: CMTime?
    private var latestFrame: CIImage?
    private var lastClick = -Double.infinity
    private var lastFrame = 0.0
    private var zoom = 1.0
    private var center = CGPoint(x: 0.5, y: 0.5)
    private var target = CGPoint(x: 0.5, y: 0.5)
    private var wasPressed = false

    init(url: URL, width: Int, height: Int, screenRect: CGRect, focus: Bool, sound: Bool) throws {
        writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        outputSize = CGSize(width: width, height: height)
        self.screenRect = screenRect
        focusEnabled = focus
        video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264, AVVideoWidthKey: width, AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: min(12_000_000, max(750_000, width * height * 4)),
                                              AVVideoExpectedSourceFrameRateKey: 30]
        ])
        video.expectsMediaDataInRealTime = true
        adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: video, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width, kCVPixelBufferHeightKey as String: height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:] as [String: Any]
        ])
        audio = sound ? AVAssetWriterInput(mediaType: .audio, outputSettings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000,
            AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 128_000
        ]) : nil
        super.init()
        guard writer.canAdd(video) else { throw CaptureError("Der H.264-Encoder ist nicht verfügbar.") }
        writer.add(video)
        if let audio {
            guard writer.canAdd(audio) else { throw CaptureError("Der AAC-Encoder ist nicht verfügbar.") }
            audio.expectsMediaDataInRealTime = true
            writer.add(audio)
        }
        guard writer.startWriting() else { throw writer.error ?? CaptureError("Der MP4-Export konnte nicht starten.") }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) { fail(error) }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sampleBuffer.isValid else { return }
        let time = sampleBuffer.presentationTimeStamp
        if type == .audio {
            if let start = sessionStart, time >= start, let audio, audio.isReadyForMoreMediaData,
               !audio.append(sampleBuffer) { fail(writer.error ?? CaptureError("Audio konnte nicht gespeichert werden.")) }
            return
        }
        guard type == .screen, let buffer = sampleBuffer.imageBuffer,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let status = attachments.first?[.status] as? Int, status == SCFrameStatus.complete.rawValue else { return }
        let frame = CIImage(cvPixelBuffer: buffer)
        if focusEnabled { latestFrame = frame }
        if !focusEnabled || lastVideoTime == nil {
            do { try append(image: frame, at: time, announce: true) }
            catch { fail(error) }
        }
    }

    func animateFocus() {
        guard let latestFrame, let last = lastVideoTime else { return }
        let now = CMClockGetTime(CMClockGetHostTimeClock())
        guard now > last else { return }
        do { try append(image: latestFrame, at: now, announce: true) }
        catch { fail(error) }
    }

    func append(image: CIImage, at time: CMTime, announce: Bool) throws {
        let first = sessionStart == nil
        if first {
            writer.startSession(atSourceTime: time)
            sessionStart = time
        }
        guard video.isReadyForMoreMediaData else {
            if let error = writer.error { throw error }
            return
        }
        guard let pool = adaptor.pixelBufferPool else { throw CaptureError("Der Videopuffer fehlt.") }
        var pixelBuffer: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &pixelBuffer) == kCVReturnSuccess, let pixelBuffer else {
            throw CaptureError("Der Videopuffer konnte nicht erstellt werden.")
        }
        let viewport = focusEnabled ? focusRect(in: image.extent, at: time.seconds) : image.extent
        let frame = image.cropped(to: viewport)
            .transformed(by: CGAffineTransform(translationX: -viewport.minX, y: -viewport.minY))
            .transformed(by: CGAffineTransform(scaleX: outputSize.width / viewport.width, y: outputSize.height / viewport.height))
        context.render(frame, to: pixelBuffer, bounds: CGRect(origin: .zero, size: outputSize),
                       colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
        guard adaptor.append(pixelBuffer, withPresentationTime: time) else {
            throw writer.error ?? CaptureError("Das Videobild konnte nicht gespeichert werden.")
        }
        if lastVideoTime == nil && announce {
            FileHandle.standardOutput.write(Data("READY\n".utf8))
            firstScreenFrame.signal()
        }
        lastVideoTime = time
    }

    private func focusRect(in extent: CGRect, at seconds: Double) -> CGRect {
        let pressed = CGEventSource.buttonState(.combinedSessionState, button: .left)
        if pressed && !wasPressed, let point = CGEvent(source: nil)?.location, screenRect.contains(point) {
            target = CGPoint(x: (point.x - screenRect.minX) / screenRect.width,
                             y: 1 - (point.y - screenRect.minY) / screenRect.height)
            lastClick = seconds
        }
        wasPressed = pressed
        let smoothing = 1 - exp(-7 * max(0, seconds - lastFrame))
        lastFrame = seconds
        zoom += ((seconds - lastClick < 1.7 ? 1.55 : 1) - zoom) * smoothing
        center.x += (target.x - center.x) * smoothing
        center.y += (target.y - center.y) * smoothing
        let width = extent.width / zoom, height = extent.height / zoom
        return CGRect(x: min(extent.maxX - width, max(extent.minX, extent.minX + center.x * extent.width - width / 2)),
                      y: min(extent.maxY - height, max(extent.minY, extent.minY + center.y * extent.height - height / 2)),
                      width: width, height: height)
    }

    func finish(at time: CMTime) async throws {
        guard let last = lastVideoTime else { throw CaptureError("macOS hat kein Bildschirmbild geliefert.") }
        writer.endSession(atSourceTime: CMTimeMaximum(time, last + CMTime(value: 1, timescale: 30)))
        video.markAsFinished()
        audio?.markAsFinished()
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? CaptureError("Der MP4-Export ist fehlgeschlagen.") }
    }
}

@main
private enum CaptureHelper {
    static func main() async {
        do { try await run(Array(CommandLine.arguments.dropFirst())) }
        catch { fail(error) }
    }

    static func run(_ args: [String]) async throws {
        guard args.count >= 2 else { throw CaptureError("Befehl und Zieldatei fehlen.") }
        let url = URL(fileURLWithPath: args[1])
        if args[0] == "self-test" {
            let bounds = CGRect(x: 0, y: 0, width: 320, height: 180)
            let output = try MovieOutput(url: url, width: 320, height: 180, screenRect: bounds, focus: false, sound: false)
            let image = CIImage(color: CIColor(red: 0.3, green: 0.5, blue: 0.9)).cropped(to: bounds)
            for i in 0..<6 {
                let deadline = Date().addingTimeInterval(8)
                while !output.video.isReadyForMoreMediaData {
                    if let error = output.writer.error { throw error }
                    if Date() > deadline { throw CaptureError("Der H.264-Encoder antwortet nicht.") }
                    try await Task.sleep(nanoseconds: 10_000_000)
                }
                try output.append(image: image, at: CMTime(value: Int64(i), timescale: 30), announce: false)
            }
            try await output.finish(at: CMTime(value: 6, timescale: 30))
            let decoder = AVAssetImageGenerator(asset: AVURLAsset(url: url))
            let frame = try await decoder.image(at: .zero).image
            guard frame.width == 320, frame.height == 180 else {
                throw CaptureError("Das dekodierte Testvideo hat eine unerwartete Größe.")
            }
            return
        }
        guard args.count >= 3, let parent = Int32(args[2]) else { throw CaptureError("Die App-Prozess-ID fehlt.") }
        guard CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() else {
            fail(CaptureError("Die Berechtigung für die Bildschirmaufnahme fehlt."), code: 77)
        }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) else {
            throw CaptureError("Der Hauptbildschirm ist nicht verfügbar.")
        }
        let excluded = content.applications.filter { $0.processID == parent }
        let filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
        let config = SCStreamConfiguration()
        let scale = CGFloat(filter.pointPixelScale)
        let width = Int((filter.contentRect.width * scale).rounded())
        let height = Int((filter.contentRect.height * scale).rounded())
        if args[0] == "screenshot" {
            config.width = width
            config.height = height
            config.showsCursor = false
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            guard let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)
                else { throw CaptureError("Das PNG konnte nicht erstellt werden.") }
            CGImageDestinationAddImage(destination, image, nil)
            guard CGImageDestinationFinalize(destination) else { throw CaptureError("Das PNG konnte nicht gespeichert werden.") }
            return
        }
        guard args[0] == "record", args.count == 6 || args.count == 10 else { throw CaptureError("Ungültige Aufnahmeeinstellungen.") }
        var region = CGRect(x: 0, y: 0, width: width, height: height)
        if args.count == 10 {
            let values = args.suffix(4).compactMap { Int($0) }
            guard values.count == 4, values[0] >= 0, values[1] >= 0, values[2] >= 2, values[3] >= 2,
                  values[2] <= width, values[3] <= height, values[0] <= width - values[2],
                  values[1] <= height - values[3] else { throw CaptureError("Der Aufnahmebereich liegt außerhalb des Hauptbildschirms.") }
            region = CGRect(x: values[0], y: values[1], width: values[2], height: values[3])
        }
        let inPoints = region.applying(CGAffineTransform(scaleX: 1 / scale, y: 1 / scale))
        let screenRect = inPoints.offsetBy(dx: display.frame.minX, dy: display.frame.minY)
        let outputScale = min(1, min(1920 / region.width, 1080 / region.height))
        config.width = max(2, Int(region.width * outputScale) / 2 * 2)
        config.height = max(2, Int(region.height * outputScale) / 2 * 2)
        config.sourceRect = inPoints
        config.minimumFrameInterval = CMTime(value: 1, timescale: 30)
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.showsCursor = args[4] == "1"
        config.capturesAudio = args[5] == "1"
        config.excludesCurrentProcessAudio = true
        config.channelCount = 2
        config.sampleRate = 48_000
        let movie = try MovieOutput(url: url, width: config.width, height: config.height, screenRect: screenRect,
                                    focus: args[3] == "1", sound: config.capturesAudio)
        let queue = DispatchQueue(label: "de.thixapps.ThixShot.capture")
        let stream = SCStream(filter: filter, configuration: config, delegate: movie)
        try stream.addStreamOutput(movie, type: .screen, sampleHandlerQueue: queue)
        if config.capturesAudio { try stream.addStreamOutput(movie, type: .audio, sampleHandlerQueue: queue) }
        try await stream.startCapture()
        // ScreenCaptureKit sends idle frames without pixels. Render the last image while
        // a click zoom animates, including when the underlying screen is motionless.
        let animation = DispatchSource.makeTimerSource(queue: queue)
        if movie.focusEnabled {
            animation.setEventHandler { movie.animateFocus() }
            animation.schedule(deadline: .now(), repeating: .milliseconds(33))
        }
        animation.resume()
        let firstFrame = movie.firstScreenFrame
        let started = await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
            DispatchQueue.global().async {
                continuation.resume(returning: firstFrame.wait(timeout: .now() + 12) == .success)
            }
        }
        guard started else { throw CaptureError("macOS hat kein Bildschirmbild geliefert.") }
        // EOF also ends capture if the desktop application exits.
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            DispatchQueue.global().async { _ = readLine(); continuation.resume() }
        }
        animation.cancel()
        try await stream.stopCapture()
        queue.sync {}
        try await movie.finish(at: CMClockGetTime(CMClockGetHostTimeClock()))
    }
}
