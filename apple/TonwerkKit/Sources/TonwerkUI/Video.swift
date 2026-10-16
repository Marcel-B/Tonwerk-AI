import CoreGraphics
import CoreText
import Foundation
import ImageIO
import TonwerkKit
import UniformTypeIdentifiers

/// A music video of a song, the web page's `video.ts`: the app draws the still layers (blurred background, cover,
/// title, a field of particles or the frames of a plasma ball, the timed lyrics) as PNGs, the server animates them with
/// ffmpeg (`Video/FfmpegVideoRenderer.cs`) and encodes the MP4. A photo video takes a folder of the Mac's photos and
/// clips instead of the blurred cover, cut to the beat on the server; the app then draws only a shade over them, the
/// cover if wanted and the title. The drawing follows `video.ts` step by step, so a video looks the same from both.
enum VideoFormat: String, CaseIterable, Identifiable, Codable, Sendable {
    case landscape
    case portrait

    var id: String { rawValue }

    var name: String {
        self == .landscape ? String(localized: "Quer (YouTube)") : String(localized: "Hochkant (Reels)")
    }
}

enum VideoEffect: String, CaseIterable, Identifiable, Codable, Sendable {
    case bars
    case wave
    case none

    var id: String { rawValue }

    var name: String {
        switch self {
        case .bars: String(localized: "Balken")
        case .wave: String(localized: "Welle")
        case .none: String(localized: "Keine")
        }
    }
}

enum VideoMotion: String, CaseIterable, Identifiable, Codable, Sendable {
    case particles
    case plasma
    case none

    var id: String { rawValue }

    var name: String {
        switch self {
        case .particles: String(localized: "Partikel")
        case .plasma: String(localized: "Plasma")
        case .none: String(localized: "Keine")
        }
    }
}

/// What fills the picture: the song's cover, blurred, or a folder of photos and clips cut to the beat.
enum VideoSource: String, CaseIterable, Identifiable, Codable, Sendable {
    case cover
    case photos

    var id: String { rawValue }

    var name: String {
        self == .cover ? String(localized: "Cover") : String(localized: "Fotos vom Mac")
    }
}

/// Where things go; width, height and the band must match the server's `VideoLayout`, which puts the analyzer or the
/// waveform into the band. Cover and title are the app's own, as on the web page.
struct VideoLayout: Sendable, Equatable {
    let width: Int
    let height: Int
    let bandY: Double
    let bandHeight: Double
    let coverX: Double
    let coverY: Double
    let coverSize: Double
    /// Baseline of the title's first line, and its largest size.
    let titleY: Double
    let titleSize: Double
    /// The plasma ball's square behind a cover (the server's `VideoLayout.PlasmaArea`); without one the whole frame.
    let plasmaX: Double
    let plasmaY: Double
    let plasmaSize: Double

    static func of(_ format: VideoFormat) -> VideoLayout {
        switch format {
        case .landscape:
            VideoLayout(
                width: 1920, height: 1080, bandY: 850, bandHeight: 180, coverX: 680, coverY: 90, coverSize: 560,
                titleY: 735, titleSize: 64, plasmaX: 460, plasmaY: -130, plasmaSize: 1000)
        case .portrait:
            // Everything above the bottom fifth, which the apps cover with caption and buttons.
            VideoLayout(
                width: 1080, height: 1920, bandY: 1270, bandHeight: 220, coverX: 160, coverY: 230, coverSize: 760,
                titleY: 1100, titleSize: 68, plasmaX: 0, plasmaY: 70, plasmaSize: 1080)
        }
    }
}

/// The last choice, kept like the web page's `yue-ui.video`: a channel tends to post the same kind of video.
struct VideoSettings: Codable, Equatable, Sendable {
    var format = VideoFormat.landscape
    var effect = VideoEffect.bars
    /// Only for a cover video; the photos move themselves.
    var motion = VideoMotion.particles
    var showCover = true
    var showTitle = true
    var source = VideoSource.cover
    /// The photo video's folder, by name.
    var media = ""
    /// What its pictures should show, for CLIP; empty takes the sharpest and best exposed.
    var motif = ""
    /// The sung line in the band, where the song's lyrics have times.
    var showLyrics = true

    static let key = "videoSettings"

    static func load(from defaults: UserDefaults = .standard) -> VideoSettings {
        guard let data = defaults.data(forKey: key),
              let stored = try? JSONDecoder().decode(VideoSettings.self, from: data)
        else { return VideoSettings() }
        return stored
    }

    func save(to defaults: UserDefaults = .standard) {
        if let data = try? JSONEncoder().encode(self) {
            defaults.set(data, forKey: Self.key)
        }
    }

    var photos: Bool { source == .photos }

    /// What the picture looks like; the motif only matters on the server, so typing it draws nothing again.
    var drawn: VideoSettings {
        var copy = self
        copy.motif = ""
        return copy
    }
}

/// The layers, ready to be sent (``VideoArt/pngs(of:)``) or put together for the preview.
struct VideoLayers: @unchecked Sendable {
    /// The analyzer's colour, picked from the cover (`#rrggbb`).
    let color: String
    /// A photo video: `background` is only the shade laid over its pictures.
    let photos: Bool
    let background: CGImage
    let cover: CGImage?
    let title: CGImage?
    let particles: CGImage?
    /// The plasma ball's frames and where they go in the picture.
    let plasma: (frames: [CGImage], x: Double, y: Double)?
    /// The timed lyrics' strips.
    let lyrics: [CGImage]?
}

enum VideoArt {
    /// The server's `VideoLayout.PlasmaFrames`: the plasma ball comes as this many pictures, which it flickers between.
    static let plasmaFrames = 8
    /// The server's `VideoLayout.LyricsPerStrip`: a whole song in one picture would be taller than iOS allows.
    static let lyricsPerStrip = 24
    /// The server's `FfmpegVideoRenderer.Accent`, and the bars' pitch and width (`VideoLayout`), for the preview.
    static let accent = "#34d399"
    static let barPitch = 40.0
    static let barWidth = 32.0

    /// Every layer for the song, as `drawVideoLayers`. The cover is the song's own, else the drawn one the export uses;
    /// a line under the title names the artist when the share dialog has one.
    static func layers(
        cover image: CGImage, songId: String, title: String, subtitle: String, lyrics: [String]?,
        settings: VideoSettings
    ) -> VideoLayers? {
        let layout = VideoLayout.of(settings.format)
        let photos = settings.photos
        guard let base = photos ? shade(layout) : background(layout, image) else { return nil }
        return VideoLayers(
            color: coverColour(image),
            photos: photos,
            background: base,
            cover: settings.showCover ? coverLayer(layout, image) : nil,
            title: settings.showTitle ? titleLayer(layout, title.isEmpty ? "Tonwerk" : title, subtitle: subtitle) : nil,
            particles: !photos && settings.motion == .particles ? particles(layout, songId: songId) : nil,
            plasma: !photos && settings.motion == .plasma
                ? plasma(layout, songId: songId, behindCover: settings.showCover) : nil,
            lyrics: settings.showLyrics && !(lyrics ?? []).isEmpty ? lyricsStrips(layout, lines: lyrics!) : nil)
    }

    /// The layers by the form names the server reads them under.
    static func pngs(of layers: VideoLayers) -> [(name: String, data: Data)] {
        var images: [(String, CGImage)] = [(layers.photos ? "shade" : "background", layers.background)]
        if let cover = layers.cover { images.append(("cover", cover)) }
        if let title = layers.title { images.append(("title", title)) }
        if let particles = layers.particles { images.append(("particles", particles)) }
        for (index, frame) in (layers.plasma?.frames ?? []).enumerated() {
            images.append(("plasma\(index)", frame))
        }
        for (index, strip) in (layers.lyrics ?? []).enumerated() {
            images.append(("lyrics\(index)", strip))
        }
        return images.compactMap { name, image in png(image).map { (name, $0) } }
    }

    /// One still of the video as it will look: the layers on top of each other and, in the band, bars or a wave of
    /// made-up heights where ffmpeg will draw the music. Scaled down, since a phone shows it a few hundred points wide.
    static func preview(of layers: VideoLayers, settings: VideoSettings) -> CGImage? {
        let layout = VideoLayout.of(settings.format)
        let scale = 0.3
        guard let canvas = VideoCanvas(width: Int(Double(layout.width) * scale), height: Int(Double(layout.height) * scale))
        else { return nil }
        let context = canvas.context
        context.scaleBy(x: scale, y: scale)
        context.interpolationQuality = .high
        let full = CGRect(x: 0, y: 0, width: layout.width, height: layout.height)
        if layers.photos {
            photoPlaceholder(context, layout)
        }
        canvas.draw(layers.background, in: full)
        if let particles = layers.particles {
            canvas.draw(particles, in: full)
        }
        if let plasma = layers.plasma, let first = plasma.frames.first {
            canvas.draw(first, in: CGRect(x: plasma.x, y: plasma.y, width: Double(first.width), height: Double(first.height)))
        }
        let colour = cgColor(hex: layers.color)
        if settings.effect != .none {
            let random = seeded(7)
            if settings.effect == .bars {
                context.setFillColor(colour)
                var x = 0.0
                while x + barWidth <= Double(layout.width) {
                    // Louder in the lows, like a song's spectrum.
                    let height = layout.bandHeight * (0.15 + 0.75 * random() * (1 - 0.7 * x / Double(layout.width)))
                    context.fill(CGRect(x: x, y: layout.bandY + layout.bandHeight - height, width: barWidth, height: height))
                    x += barPitch
                }
            } else {
                let middle = layout.bandY + layout.bandHeight / 2
                context.beginPath()
                context.move(to: CGPoint(x: 0, y: middle))
                var x = 0
                while x <= layout.width {
                    let amplitude = layout.bandHeight / 2
                        * (0.2 + 0.6 * abs(sin(Double(x) / 37) * sin(Double(x) / 211)))
                    context.addLine(to: CGPoint(x: Double(x), y: middle - amplitude * (x % 12 == 0 ? 1 : -1)))
                    x += 6
                }
                context.setLineWidth(3)
                context.setStrokeColor(colour)
                context.strokePath()
            }
        }
        if let strip = layers.lyrics?.first {
            // The first line, as it shows while it is sung.
            let slot = Int(layout.bandHeight)
            if let line = strip.cropping(to: CGRect(x: 0, y: slot, width: layout.width, height: slot)) {
                canvas.draw(line, in: CGRect(x: 0, y: layout.bandY, width: Double(layout.width), height: layout.bandHeight))
            }
        }
        if let cover = layers.cover {
            canvas.draw(cover, in: full)
        }
        if let title = layers.title {
            canvas.draw(title, in: full)
        }
        return canvas.image
    }

    // MARK: Colour

    /// The cover's most vivid hue as a light colour that stands out on the darkened background: the hues of a small
    /// copy are counted weighted by chroma squared, so a few bright pixels outweigh a large grey area. A grey cover
    /// keeps Tonwerk's emerald.
    static func coverColour(_ image: CGImage) -> String {
        let size = 24
        guard let context = CGContext(
            data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: size * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return accent }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: size, height: size))
        guard let data = context.data else { return accent }
        let pixels = data.bindMemory(to: UInt8.self, capacity: size * size * 4)
        var bins = [Double](repeating: 0, count: 36)
        var sums = [Double](repeating: 0, count: 36)
        var total = 0.0
        for i in stride(from: 0, to: size * size * 4, by: 4) {
            let r = Double(pixels[i]) / 255, g = Double(pixels[i + 1]) / 255, b = Double(pixels[i + 2]) / 255
            let high = max(r, g, b)
            let chroma = high - min(r, g, b)
            if chroma < 0.08 { continue }
            let sector: Double
            if high == r {
                sector = ((g - b) / chroma + 6).truncatingRemainder(dividingBy: 6)
            } else if high == g {
                sector = (b - r) / chroma + 2
            } else {
                sector = (r - g) / chroma + 4
            }
            let hue = sector * 60
            // Chroma is saturation times brightness; squared, vivid pixels count far more than dull ones.
            let weight = chroma * chroma
            let bin = Int(hue / 10) % 36
            bins[bin] += weight
            sums[bin] += weight * hue
            total += weight
        }
        guard total >= 0.5, let best = bins.indices.max(by: { bins[$0] < bins[$1] }) else { return accent }
        return hex(hsl(hue: sums[best] / bins[best], saturation: 0.75, lightness: 0.65))
    }

    static func hsl(hue: Double, saturation: Double, lightness: Double) -> (Double, Double, Double) {
        let a = saturation * min(lightness, 1 - lightness)
        func channel(_ n: Double) -> Double {
            let k = (n + hue / 30).truncatingRemainder(dividingBy: 12)
            return lightness - a * max(-1, min(k - 3, 9 - k, 1))
        }
        return (channel(0), channel(8), channel(4))
    }

    static func hex(_ rgb: (Double, Double, Double)) -> String {
        let parts = [rgb.0, rgb.1, rgb.2].map { value -> String in
            let digits = String(Int((value * 255).rounded()), radix: 16)
            return digits.count < 2 ? "0" + digits : digits
        }
        return "#" + parts.joined()
    }

    static func cgColor(hex: String) -> CGColor {
        let digits = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        guard digits.count == 6, let value = UInt32(digits, radix: 16) else { return cgColor(hex: accent) }
        return CGColor(
            srgbRed: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255, alpha: 1)
    }

    // MARK: Layers

    /// The cover blown up to fill the frame, blurred and darkened, so every song gets a background in its own colours.
    /// Blurred by drawing it tiny and scaling it up, as the web page does.
    static func background(_ layout: VideoLayout, _ image: CGImage) -> CGImage? {
        guard let small = VideoCanvas(width: 12, height: Int((12 * Double(layout.height) / Double(layout.width)).rounded())),
              let canvas = VideoCanvas(width: layout.width, height: layout.height)
        else { return nil }
        small.context.interpolationQuality = .high
        small.drawCovering(image, in: CGRect(x: 0, y: 0, width: small.width, height: small.height))
        guard let tiny = small.image else { return nil }
        let context = canvas.context
        let full = CGRect(x: 0, y: 0, width: layout.width, height: layout.height)
        context.interpolationQuality = .high
        canvas.draw(tiny, in: full)
        context.setFillColor(black(0.45))
        context.fill(full)
        // Darker towards the bottom, where the band and the title need contrast.
        canvas.linear(from: 0, to: Double(layout.height), stops: [(0, black(0)), (0.55, black(0.1)), (1, black(0.55))])
        vignette(canvas, layout, inner: 0.35, alpha: 0.45)
        return canvas.image
    }

    /// A photo video's shade: lighter in the middle so the photos keep their colours, darker at the bottom where the
    /// band and the title sit.
    static func shade(_ layout: VideoLayout) -> CGImage? {
        guard let canvas = VideoCanvas(width: layout.width, height: layout.height) else { return nil }
        canvas.linear(
            from: 0, to: Double(layout.height),
            stops: [(0, black(0.15)), (0.5, black(0)), (0.75, black(0.25)), (1, black(0.6))])
        vignette(canvas, layout, inner: 0.4, alpha: 0.35)
        return canvas.image
    }

    private static func vignette(_ canvas: VideoCanvas, _ layout: VideoLayout, inner: Double, alpha: Double) {
        let width = Double(layout.width), height = Double(layout.height)
        let centre = CGPoint(x: width / 2, y: height / 2)
        canvas.radial(
            centre, from: min(width, height) * inner, to: max(width, height) * 0.75,
            stops: [(0, black(0)), (1, black(alpha))], extend: true)
    }

    /// Where the preview cannot show the photos (they are on the Mac and picked only while the video is made): a few
    /// tiles in muted colours that stand for them.
    private static func photoPlaceholder(_ context: CGContext, _ layout: VideoLayout) {
        let columns = layout.width > layout.height ? 4 : 2
        let rows = layout.width > layout.height ? 2 : 4
        let tileWidth = Double(layout.width) / Double(columns)
        let tileHeight = Double(layout.height) / Double(rows)
        let random = seeded(11)
        for row in 0..<rows {
            for column in 0..<columns {
                let hue = (random() * 360).rounded()
                let lightness = (28 + random() * 22).rounded() / 100
                let (r, g, b) = hsl(hue: hue, saturation: 0.25, lightness: lightness)
                context.setFillColor(CGColor(srgbRed: r, green: g, blue: b, alpha: 1))
                context.fill(CGRect(
                    x: Double(column) * tileWidth, y: Double(row) * tileHeight,
                    width: tileWidth + 1, height: tileHeight + 1))
            }
        }
    }

    static func coverLayer(_ layout: VideoLayout, _ image: CGImage) -> CGImage? {
        guard let canvas = VideoCanvas(width: layout.width, height: layout.height) else { return nil }
        let context = canvas.context
        let size = layout.coverSize
        let box = CGRect(x: layout.coverX, y: layout.coverY, width: size, height: size)
        let path = CGPath(roundedRect: box, cornerWidth: size * 0.04, cornerHeight: size * 0.04, transform: nil)
        context.saveGState()
        canvas.shadow(offsetY: size * 0.03, blur: size * 0.1, colour: black(0.55))
        context.setFillColor(black(1))
        context.addPath(path)
        context.fillPath()
        context.restoreGState()
        context.saveGState()
        context.addPath(path)
        context.clip()
        context.interpolationQuality = .high
        canvas.drawCovering(image, in: box)
        context.restoreGState()
        return canvas.image
    }

    static func titleLayer(_ layout: VideoLayout, _ title: String, subtitle: String) -> CGImage? {
        guard let canvas = VideoCanvas(width: layout.width, height: layout.height) else { return nil }
        let width = Double(layout.width - 2 * 100)
        var size = layout.titleSize
        var lines: [String] = []
        // As large as fits in two lines.
        while size >= 36 {
            let font = VideoCanvas.font(size: size, bold: true)
            lines = wrap(title, width: width) { VideoCanvas.measure($0, font: font) }
            if lines.count <= 2 && lines.allSatisfy({ VideoCanvas.measure($0, font: font) <= width }) { break }
            size -= 4
        }
        size = max(size, 36)
        canvas.shadow(offsetY: 3, blur: 18, colour: black(0.6))
        let font = VideoCanvas.font(size: size, bold: true)
        var y = layout.titleY
        for line in lines.prefix(2) {
            canvas.text(line, centredAt: Double(layout.width) / 2, baseline: y, font: font, colour: white(0.97), maxWidth: width)
            y += size * 1.15
        }
        if !subtitle.isEmpty {
            canvas.text(
                subtitle, centredAt: Double(layout.width) / 2, baseline: y - size * 0.15 + size * 0.45,
                font: VideoCanvas.font(size: (size * 0.55).rounded(), bold: false), colour: white(0.75), maxWidth: width)
        }
        return canvas.image
    }

    /// The sung lines for the band, as the server's renderer picks them by time: strips of up to `lyricsPerStrip` slots
    /// of the band's size under an empty one, each line centred in its slot, as large as fits in two lines, with a
    /// shadow so it stays readable over the analyzer.
    static func lyricsStrips(_ layout: VideoLayout, lines: [String]) -> [CGImage] {
        let slot = layout.bandHeight
        let width = Double(layout.width - 2 * 80)
        var strips: [CGImage] = []
        for first in stride(from: 0, to: lines.count, by: lyricsPerStrip) {
            let chunk = lines[first..<min(first + lyricsPerStrip, lines.count)]
            guard let canvas = VideoCanvas(width: layout.width, height: Int(slot) * (chunk.count + 1)) else { continue }
            canvas.shadow(offsetY: 2, blur: 16, colour: black(0.85))
            for (index, text) in chunk.enumerated() {
                var size = (slot * 0.32).rounded()
                var wrapped: [String] = []
                while size >= 28 {
                    let font = VideoCanvas.font(size: size, bold: true)
                    wrapped = wrap(text, width: width) { VideoCanvas.measure($0, font: font) }
                    if wrapped.count <= 2 && wrapped.allSatisfy({ VideoCanvas.measure($0, font: font) <= width }) { break }
                    size -= 2
                }
                size = max(size, 28)
                let font = VideoCanvas.font(size: size, bold: true)
                // VideoCanvas's `middle` baseline: the em box's middle on the slot's.
                let middle = slot * Double(index + 1) + slot / 2
                let centring = (CTFontGetAscent(font) - CTFontGetDescent(font)) / 2
                let shown = Array(wrapped.prefix(2))
                for (i, line) in shown.enumerated() {
                    let offset = (Double(i) - Double(shown.count - 1) / 2) * size * 1.15
                    canvas.text(
                        line, centredAt: Double(layout.width) / 2, baseline: middle + offset + centring, font: font,
                        colour: white(0.97), maxWidth: width)
                }
            }
            if let image = canvas.image { strips.append(image) }
        }
        return strips
    }

    /// Specks of light and a few out-of-focus ones, in the app's mint and white. The server scrolls two copies of the
    /// picture stacked, so whatever crosses the top or bottom edge is drawn on the other side as well. Seeded by the
    /// song, so the same song gets the same sky as on the web page.
    static func particles(_ layout: VideoLayout, songId: String) -> CGImage? {
        guard let canvas = VideoCanvas(width: layout.width, height: layout.height) else { return nil }
        let width = Double(layout.width), height = Double(layout.height)
        let random = seeded(hashOf(songId))
        let count = Int((width * height / 9000).rounded())
        let colours: [(Double, Double, Double)] = [(255, 255, 255), (209, 250, 229), (167, 243, 208)]
        for _ in 0..<count {
            let bokeh = random() < 0.12
            let radius = bokeh ? 6 + random() * 12 : 1 + random() * 2.5
            let alpha = bokeh ? 0.12 + random() * 0.2 : 0.45 + random() * 0.5
            let colour = colours[min(Int(random() * Double(colours.count)), colours.count - 1)]
            let x = random() * width
            let y = random() * height
            let reach = radius * 3
            for shift in [-height, 0, height] {
                let cy = y + shift
                if cy + reach < 0 || cy - reach > height { continue }
                canvas.radial(
                    CGPoint(x: x, y: cy), from: 0, to: reach,
                    stops: [
                        (0, rgb(colour, alpha)),
                        (bokeh ? 0.3 : 0.25, rgb(colour, alpha * (bokeh ? 0.8 : 0.6))),
                        (1, rgb(colour, 0)),
                    ], extend: false)
            }
        }
        return canvas.image
    }

    /// A plasma ball without its glass: a glowing core in the cover's middle and lightning from it, drawn
    /// `plasmaFrames` times with different bolts; the server flickers between the frames and lets the bass flare
    /// them. Behind a cover the bolts reach out around it in a square and end in a spark; without one they run to the
    /// frame's edges. Seeded by the song.
    static func plasma(_ layout: VideoLayout, songId: String, behindCover: Bool) -> (frames: [CGImage], x: Double, y: Double)? {
        let area = behindCover
            ? CGRect(x: layout.plasmaX, y: layout.plasmaY, width: layout.plasmaSize, height: layout.plasmaSize)
            : CGRect(x: 0, y: 0, width: layout.width, height: layout.height)
        let random = seeded(hashOf(songId) ^ 0x5bd1_e995)
        let centre = CGPoint(
            x: layout.coverX + layout.coverSize / 2 - area.minX, y: layout.coverY + layout.coverSize / 2 - area.minY)
        let square = layout.plasmaSize
        // Where each bolt starts its life, so neighbouring frames keep a family likeness while the shapes change.
        let roots = (0..<(behindCover ? 12 : 10)).map { _ in random() * .pi * 2 }
        var frames: [CGImage] = []
        for _ in 0..<plasmaFrames {
            guard let canvas = VideoCanvas(width: Int(area.width), height: Int(area.height)) else { return nil }
            for root in roots {
                if random() < 0.15 { continue } // a bolt that is out this frame
                let angle = root + (random() - 0.5) * 0.7
                let full = behindCover
                    ? square * 0.48 : distanceToEdge(centre, angle: angle, width: area.width, height: area.height) + 20
                // Most reach the end, some die out on the way.
                let whole = random() < 0.75
                let reach = whole ? full : full * (0.55 + random() * 0.35)
                let end = CGPoint(x: centre.x + cos(angle) * reach, y: centre.y + sin(angle) * reach)
                let path = bolt(from: centre, to: end, random: random)
                drawBolt(canvas, path, strength: 1)
                // A branch or two off the bolt's outer half.
                for _ in 0..<2 {
                    if random() < 0.5 { continue }
                    let from = path[Int(Double(path.count) * (0.4 + random() * 0.4))]
                    let turn = angle + (random() < 0.5 ? -1 : 1) * (0.35 + random() * 0.5)
                    let length = min(full, square * 0.48) * (0.15 + random() * 0.25)
                    let to = CGPoint(x: from.x + cos(turn) * length, y: from.y + sin(turn) * length)
                    drawBolt(canvas, bolt(from: from, to: to, random: random), strength: 0.6)
                }
                if whole && behindCover {
                    glow(canvas, end, radius: square * 0.03, colour: (255, 230, 255), alpha: 0.9)
                }
            }
            if !behindCover {
                let pulse = 0.85 + random() * 0.3
                glow(canvas, centre, radius: square * 0.1 * pulse, colour: (250, 232, 255), alpha: 1)
                glow(canvas, centre, radius: square * 0.2 * pulse, colour: (192, 132, 252), alpha: 0.35)
            }
            if let image = canvas.image { frames.append(image) }
        }
        return (frames, Double(area.minX), Double(area.minY))
    }

    /// How far a ray from the point at this angle runs inside the box before it leaves it.
    static func distanceToEdge(_ from: CGPoint, angle: Double, width: Double, height: Double) -> Double {
        let dx = cos(angle), dy = sin(angle)
        let alongX = dx > 0 ? (width - from.x) / dx : dx < 0 ? -from.x / dx : .infinity
        let alongY = dy > 0 ? (height - from.y) / dy : dy < 0 ? -from.y / dy : .infinity
        return min(alongX, alongY)
    }

    /// A jagged line by midpoint displacement: each half bends sideways by a share of its length.
    static func bolt(from: CGPoint, to: CGPoint, random: () -> Double) -> [CGPoint] {
        var points = [from, to]
        var roughness = 0.22
        for _ in 0..<6 {
            var next = [points[0]]
            for i in 1..<points.count {
                let a = points[i - 1], b = points[i]
                let dx = b.x - a.x, dy = b.y - a.y
                let shift = (random() - 0.5) * roughness
                next.append(CGPoint(x: (a.x + b.x) / 2 - dy * shift, y: (a.y + b.y) / 2 + dx * shift))
                next.append(b)
            }
            points = next
            roughness *= 0.85
        }
        return points
    }

    /// A bolt as a plasma ball shows it: a wide violet haze, a pink body and a white-hot core.
    private static func drawBolt(_ canvas: VideoCanvas, _ path: [CGPoint], strength: Double) {
        let strokes: [(width: Double, colour: CGColor, blur: Double)] = [
            (14 * strength, rgb((168, 85, 247), 0.22), 30),
            (5 * strength, rgb((232, 121, 249), 0.7), 12),
            (1.8 * strength, rgb((255, 255, 255), 0.95), 0),
        ]
        let context = canvas.context
        context.saveGState()
        context.setLineCap(.round)
        context.setLineJoin(.round)
        for stroke in strokes {
            context.saveGState()
            if stroke.blur > 0 {
                canvas.shadow(offsetY: 0, blur: stroke.blur, colour: rgb((192, 132, 252), 0.9))
            }
            context.beginPath()
            context.addLines(between: path)
            context.setLineWidth(stroke.width)
            context.setStrokeColor(stroke.colour)
            context.strokePath()
            context.restoreGState()
        }
        context.restoreGState()
    }

    private static func glow(
        _ canvas: VideoCanvas, _ centre: CGPoint, radius: Double, colour: (Double, Double, Double), alpha: Double
    ) {
        canvas.radial(
            centre, from: 0, to: radius,
            stops: [(0, rgb(colour, alpha)), (0.35, rgb(colour, alpha * 0.5)), (1, rgb(colour, 0))], extend: false)
    }

    // MARK: Helpers

    static func wrap(_ text: String, width: Double, measure: (String) -> Double) -> [String] {
        var lines: [String] = []
        var line = ""
        for word in text.split(whereSeparator: \.isWhitespace).map(String.init) {
            let candidate = line.isEmpty ? word : "\(line) \(word)"
            if !line.isEmpty && measure(candidate) > width {
                lines.append(line)
                line = word
            } else {
                line = candidate
            }
        }
        if !line.isEmpty { lines.append(line) }
        return lines
    }

    /// Mulberry32, as `video.ts` `seeded`: the same seed gives the same numbers as in the browser.
    static func seeded(_ seed: UInt32) -> () -> Double {
        var state = seed
        return {
            state = state &+ 0x6D2B_79F5
            var value = state
            value = (value ^ (value >> 15)) &* (value | 1)
            value ^= value &+ ((value ^ (value >> 7)) &* (value | 61))
            return Double(value ^ (value >> 14)) / 4_294_967_296
        }
    }

    /// FNV-1a over UTF-16 units, as `video.ts` `hashOf` (JavaScript's `charCodeAt`).
    static func hashOf(_ text: String) -> UInt32 {
        var hash: UInt32 = 0x811C_9DC5
        for unit in text.utf16 {
            hash ^= UInt32(unit)
            hash = hash &* 0x0100_0193
        }
        return hash
    }

    static func png(_ image: CGImage) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil)
        else { return nil }
        CGImageDestinationAddImage(destination, image, nil)
        return CGImageDestinationFinalize(destination) ? data as Data : nil
    }

    /// The image at the data's place, turned as its EXIF says.
    static func image(from data: Data) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 1600,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    static func black(_ alpha: Double) -> CGColor { CGColor(srgbRed: 0, green: 0, blue: 0, alpha: alpha) }
    static func white(_ alpha: Double) -> CGColor { CGColor(srgbRed: 1, green: 1, blue: 1, alpha: alpha) }
    static func rgb(_ colour: (Double, Double, Double), _ alpha: Double) -> CGColor {
        CGColor(srgbRed: colour.0 / 255, green: colour.1 / 255, blue: colour.2 / 255, alpha: alpha)
    }
}

/// A bitmap drawn with the origin at the top left, as a web canvas is, so the drawing reads like `video.ts`.
final class VideoCanvas {
    let context: CGContext
    let width: Int
    let height: Int

    init?(width: Int, height: Int) {
        guard width > 0, height > 0,
              let context = CGContext(
                data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                space: CGColorSpace(name: CGColorSpace.sRGB)!,
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        self.context = context
        self.width = width
        self.height = height
        context.translateBy(x: 0, y: Double(height))
        context.scaleBy(x: 1, y: -1)
    }

    var image: CGImage? { context.makeImage() }

    /// An image upright in the flipped space; Core Graphics would otherwise draw it upside down.
    func draw(_ image: CGImage, in rect: CGRect) {
        context.saveGState()
        context.translateBy(x: rect.minX, y: rect.maxY)
        context.scaleBy(x: 1, y: -1)
        context.draw(image, in: CGRect(x: 0, y: 0, width: rect.width, height: rect.height))
        context.restoreGState()
    }

    /// The image cut to fill the box, like `object-fit: cover`.
    func drawCovering(_ image: CGImage, in box: CGRect) {
        let scale = max(box.width / Double(image.width), box.height / Double(image.height))
        let sourceWidth = box.width / scale, sourceHeight = box.height / scale
        let crop = CGRect(
            x: (Double(image.width) - sourceWidth) / 2, y: (Double(image.height) - sourceHeight) / 2,
            width: sourceWidth, height: sourceHeight
        ).integral
        draw(image.cropping(to: crop) ?? image, in: box)
    }

    /// A canvas shadow: offset down by `offsetY`. Core Graphics keeps shadows in the unflipped base space, so up is
    /// positive there.
    func shadow(offsetY: Double, blur: Double, colour: CGColor) {
        context.setShadow(offset: CGSize(width: 0, height: -offsetY), blur: blur, color: colour)
    }

    /// A vertical gradient over the whole picture.
    func linear(from top: Double, to bottom: Double, stops: [(Double, CGColor)]) {
        guard let gradient = Self.gradient(stops) else { return }
        context.drawLinearGradient(
            gradient, start: CGPoint(x: 0, y: top), end: CGPoint(x: 0, y: bottom),
            options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    }

    /// A radial gradient; `extend` fills the picture beyond both circles, as a canvas gradient filling the frame does.
    func radial(_ centre: CGPoint, from inner: Double, to outer: Double, stops: [(Double, CGColor)], extend: Bool) {
        guard let gradient = Self.gradient(stops) else { return }
        context.drawRadialGradient(
            gradient, startCenter: centre, startRadius: inner, endCenter: centre, endRadius: outer,
            options: extend ? [.drawsBeforeStartLocation, .drawsAfterEndLocation] : [])
    }

    private static func gradient(_ stops: [(Double, CGColor)]) -> CGGradient? {
        CGGradient(
            colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: stops.map { $0.1 } as CFArray,
            locations: stops.map { CGFloat($0.0) })
    }

    static func font(size: Double, bold: Bool) -> CTFont {
        CTFontCreateUIFontForLanguage(bold ? .emphasizedSystem : .system, size, nil)
            ?? CTFontCreateWithName((bold ? "Helvetica-Bold" : "Helvetica") as CFString, size, nil)
    }

    private static func line(_ text: String, font: CTFont) -> CTLine {
        let attributes: [NSAttributedString.Key: Any] = [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorFromContextAttributeName as String): true,
        ]
        return CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
    }

    static func measure(_ text: String, font: CTFont) -> Double {
        CTLineGetTypographicBounds(line(text, font: font), nil, nil, nil)
    }

    /// One line centred on `x` with its baseline at `baseline`, squeezed like a canvas's `maxWidth` if it is wider.
    func text(_ text: String, centredAt x: Double, baseline: Double, font: CTFont, colour: CGColor, maxWidth: Double) {
        let line = Self.line(text, font: font)
        let width = CTLineGetTypographicBounds(line, nil, nil, nil)
        context.saveGState()
        context.setFillColor(colour)
        context.translateBy(x: x, y: baseline)
        if width > maxWidth {
            context.scaleBy(x: maxWidth / width, y: 1)
        }
        context.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
        context.textPosition = CGPoint(x: -width / 2, y: 0)
        CTLineDraw(line, context)
        context.restoreGState()
    }
}

// MARK: Server

extension AppModel {
    /// The song's videos, newest first, with newer states from the event stream laid over what the server listed.
    func videos(of song: Song, loaded: [Components.Schemas.VideoState]) -> [Components.Schemas.VideoState] {
        var byId = Dictionary(loaded.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        for video in liveVideos.values where video.songId == song.id {
            if let known = byId[video.id], (known.updatedAt ?? .distantPast) > (video.updatedAt ?? .distantPast) { continue }
            byId[video.id] = video
        }
        return byId.values
            .filter { $0.stage != "cancelled" }
            .sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
    }

    /// Videos the server renders and has not finished, oldest first, for the queue page.
    var videoJobs: [Components.Schemas.VideoState] {
        liveVideos.values.filter { $0.finished != true }
            .sorted { ($0.createdAt ?? .distantPast) < ($1.createdAt ?? .distantPast) }
    }

    func listVideos(of song: Song) async throws -> [Components.Schemas.VideoState] {
        let (data, _) = try await call("GET", address("api/songs", song.run, song.name, "videos"))
        return try decode([Components.Schemas.VideoState].self, data)
    }

    /// The Mac's folders of pictures for a photo video, and whether its planner is installed.
    func videoMedia() async throws -> Components.Schemas.MediaInfo {
        let (data, _) = try await call("GET", address("api/videos/media"))
        return try decode(Components.Schemas.MediaInfo.self, data)
    }

    /// The texts of the song's timed lines, in order; nil while the lyrics have no times.
    func timedLines(of song: Song) async -> [String]? {
        guard let url = try? address("api/songs", song.run, song.name, "timing"),
              let reply = try? await call("GET", url),
              let answer = try? decode(Components.Schemas.TimingAnswer.self, reply.0),
              let lines = answer.timing?.lines, !lines.isEmpty
        else { return nil }
        return lines.map(\.text)
    }

    /// The cover the video shows: the song's own, else the drawn one its export would carry.
    func videoCover(of song: Song) async -> CGImage? {
        if let server, let url = song.coverURL(on: server),
           let reply = try? await URLSession.shared.data(from: url), let image = VideoArt.image(from: reply.0) {
            return image
        }
        return ExportCover.jpeg(of: song).flatMap(VideoArt.image(from:))
    }

    /// Sends the layers; the server answers with the video it queued.
    func addVideo(_ song: Song, settings: VideoSettings, layers: VideoLayers) async throws -> Components.Schemas.VideoState {
        var fields: [(String, String)] = [
            ("format", settings.format.rawValue),
            ("effect", settings.effect.rawValue),
            ("motion", settings.photos ? VideoMotion.none.rawValue : settings.motion.rawValue),
            ("showCover", String(settings.showCover)),
            ("showTitle", String(settings.showTitle)),
            ("color", layers.color),
            ("showLyrics", String(layers.lyrics != nil)),
        ]
        if settings.photos {
            fields.append(("media", settings.media))
            fields.append(("motif", settings.motif.trimmingCharacters(in: .whitespacesAndNewlines)))
        }
        let files = await Task.detached { VideoArt.pngs(of: layers) }.value
        let boundary = "tonwerk-\(UUID().uuidString)"
        var body = Data()
        for (name, value) in fields {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        for (name, data) in files {
            body.append(Data(
                "--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"; filename=\"\(name).png\"\r\nContent-Type: image/png\r\n\r\n".utf8))
            body.append(data)
            body.append(Data("\r\n".utf8))
        }
        body.append(Data("--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: try address("api/songs", song.run, song.name, "videos"))
        request.httpMethod = "POST"
        request.timeoutInterval = 300
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let (data, response) = try await URLSession.shared.upload(for: request, from: body)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 202 else {
            throw ServerError(problem: ServerError.text(of: data)
                ?? (status == 501
                    ? String(localized: "Videos brauchen ffmpeg auf dem Mac.")
                    : String(localized: "Der Server antwortete \(status).")))
        }
        let video = try decode(Components.Schemas.VideoState.self, data)
        liveVideos[video.id] = video
        return video
    }

    /// Deletes a finished video or stops one in the works.
    func deleteVideo(_ video: Components.Schemas.VideoState) async throws {
        _ = try await call("DELETE", address("api/videos", video.id), accept: [204, 404])
        liveVideos[video.id] = nil
    }

    /// The MP4 to watch, streamed with range requests.
    func videoURL(_ video: Components.Schemas.VideoState) -> URL? {
        try? address("api/videos", video.id, "file")
    }

    /// The MP4 in a folder of its own under the temporary directory, named as the server names it, to share or save.
    func downloadVideo(_ video: Components.Schemas.VideoState) async throws -> URL {
        var url = try address("api/videos", video.id, "file")
        url.append(queryItems: [URLQueryItem(name: "download", value: "true")])
        let (temporary, response) = try await URLSession.shared.download(from: url)
        let http = response as? HTTPURLResponse
        guard http?.statusCode == 200 else {
            throw ServerError(String(localized: "Das Video gibt es nicht mehr."))
        }
        let name = Self.fileName(http?.value(forHTTPHeaderField: "Content-Disposition")) ?? "\(video.title).mp4"
        let folder = FileManager.default.temporaryDirectory
            .appending(path: "Tonwerk-Video", directoryHint: .isDirectory)
            .appending(path: UUID().uuidString, directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let file = folder.appending(path: name.replacingOccurrences(of: "/", with: "-"))
        try FileManager.default.moveItem(at: temporary, to: file)
        return file
    }

    /// What the video was made with, as the web page's list describes it.
    static func describe(_ video: Components.Schemas.VideoState) -> String {
        var parts: [String] = []
        if let media = video.media { parts.append(String(localized: "Fotos aus „\(media)“")) }
        parts.append(VideoFormat(rawValue: video.format)?.name ?? video.format)
        if let effect = VideoEffect(rawValue: video.effect), effect != .none { parts.append(effect.name) }
        if let motion = video.motion.flatMap(VideoMotion.init(rawValue:)), motion != .none { parts.append(motion.name) }
        if video.showCover == false && video.media == nil { parts.append(String(localized: "ohne Cover")) }
        return parts.joined(separator: " · ")
    }

    static func videoStage(_ video: Components.Schemas.VideoState) -> String {
        switch video.stage ?? "queued" {
        case "rendering": "\(Int(((video.fraction ?? 0) * 100).rounded())) %"
        case "done": String(localized: "Fertig")
        case "failed": String(localized: "Fehlgeschlagen")
        case "cancelled": String(localized: "Abgebrochen")
        default: String(localized: "Wartet")
        }
    }
}
