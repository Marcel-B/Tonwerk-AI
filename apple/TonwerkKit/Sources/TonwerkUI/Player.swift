import AVFoundation
import Foundation
import MediaPlayer
import Observation
import TonwerkKit

#if canImport(UIKit)
import UIKit
typealias PlatformImage = UIImage
#elseif canImport(AppKit)
import AppKit
typealias PlatformImage = NSImage
#endif

/// The one player, the native counterpart of `player.ts`. AVPlayer streams the songs' AAC copies; the lock screen,
/// Control Center, AirPods and the Mac's media keys go through the Now Playing info and remote commands, which keeps
/// playing in the background without any of the web page's Web Audio workarounds.
@MainActor
@Observable
public final class Player {
    public internal(set) var server: URL?
    public private(set) var queue: [Song] = []
    public private(set) var index: Int?
    public private(set) var isPlaying = false
    public private(set) var elapsed: Double = 0
    public private(set) var duration: Double = 0

    /// The playing song's cover colour (`coverColour` in `video.ts`), for the full-screen player's analyzer and
    /// glows; nil without a cover of its own.
    public private(set) var coverColour: String?
    /// What the analyzers and effects hear, fed by a tap on the playing song.
    @ObservationIgnored let analyzer = SpectrumAnalyzer()

    public var current: Song? {
        index.flatMap { queue.indices.contains($0) ? queue[$0] : nil }
    }

    public var hasNext: Bool { index.map { $0 + 1 < queue.count } ?? false }

    @ObservationIgnored private let player = AVPlayer()
    @ObservationIgnored private var timeObserver: Any?
    @ObservationIgnored private var endObserver: NSObjectProtocol?
    @ObservationIgnored private var statusObservation: NSKeyValueObservation?
    @ObservationIgnored private var artwork: (id: String, image: MPMediaItemArtwork)?

    init() {
        timeObserver = player.addPeriodicTimeObserver(forInterval: CMTime(value: 1, timescale: 2), queue: .main) {
            [weak self] time in
            MainActor.assumeIsolated { self?.tick(time) }
        }
        endObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification, object: nil, queue: .main
        ) { [weak self] notification in
            let item = notification.object as? AVPlayerItem
            MainActor.assumeIsolated { self?.ended(item) }
        }
        statusObservation = player.observe(\.timeControlStatus, options: [.new]) { [weak self] player, _ in
            let playing = player.timeControlStatus != .paused
            Task { @MainActor in self?.playingChanged(playing) }
        }
        setUpRemoteCommands()
    }

    // MARK: Playing

    /// Plays `song` and queues the songs after it in `list`, as a tap in the web page's library does.
    public func play(_ song: Song, in list: [Song]) {
        let playable = list.filter(\.hasAudio)
        queue = playable.isEmpty ? [song] : playable
        index = queue.firstIndex(of: song) ?? 0
        start()
    }

    public func toggle() {
        isPlaying ? pause() : resume()
    }

    public func resume() {
        guard current != nil else { return }
        if player.currentItem == nil {
            start()
        } else {
            activateSession()
            player.play()
        }
    }

    public func pause() {
        player.pause()
    }

    public func next() {
        guard let index, index + 1 < queue.count else { return }
        self.index = index + 1
        start()
    }

    /// Back to the song's start, or to the one before within its first seconds, like every music app.
    public func previous() {
        guard let index else { return }
        if elapsed > 3 || index == 0 {
            seek(to: 0)
        } else {
            self.index = index - 1
            start()
        }
    }

    public func seek(to seconds: Double) {
        elapsed = seconds
        player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero)
        updateNowPlaying()
    }

    public func stop() {
        player.pause()
        player.replaceCurrentItem(with: nil)
        queue = []
        index = nil
        elapsed = 0
        duration = 0
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
    }

    /// New ratings and titles from the library, for the songs already queued.
    func refresh(from songs: [Song]) {
        guard !queue.isEmpty else { return }
        let byId = Dictionary(songs.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        queue = queue.map { old in byId[old.id].map { $0.with(version: .some(old.version)) } ?? old }
        updateNowPlaying()
    }

    private func start() {
        guard let song = current, let server else { return }
        elapsed = 0
        duration = song.seconds ?? 0
        // The demo server answers through URLSession, which AVPlayer bypasses: its songs play from the bundle.
        let item = AVPlayerItem(url: DemoServer.playable(song.streamURL(on: server)))
        player.replaceCurrentItem(with: item)
        listen(to: item)
        activateSession()
        player.play()
        updateNowPlaying()
        loadArtwork(for: song)
    }

    /// Taps the song for the analyzer once its audio track is known; the song plays meanwhile, the bars wait.
    private func listen(to item: AVPlayerItem) {
        let analyzer = self.analyzer
        Task {
            guard let track = try? await item.asset.loadTracks(withMediaType: .audio).first,
                  let tap = SpectrumTap.make(feeding: analyzer)
            else { return }
            let parameters = AVMutableAudioMixInputParameters(track: track)
            parameters.audioTapProcessor = tap
            let mix = AVMutableAudioMix()
            mix.inputParameters = [parameters]
            item.audioMix = mix
        }
    }

    private func activateSession() {
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        try? session.setCategory(.playback, mode: .default)
        try? session.setActive(true)
        #endif
    }

    private func tick(_ time: CMTime) {
        elapsed = time.seconds.isFinite ? time.seconds : 0
        if let length = player.currentItem?.duration.seconds, length.isFinite, length > 0 {
            duration = length
        }
    }

    private func ended(_ item: AVPlayerItem?) {
        guard item === player.currentItem else { return }
        if hasNext {
            next()
        } else {
            isPlaying = false
            seek(to: 0)
            player.pause()
        }
    }

    private func playingChanged(_ playing: Bool) {
        isPlaying = playing
        updateNowPlaying()
    }

    // MARK: Lock screen and media keys

    private func setUpRemoteCommands() {
        let center = MPRemoteCommandCenter.shared()
        center.playCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.resume() }
            return .success
        }
        center.pauseCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.pause() }
            return .success
        }
        center.togglePlayPauseCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.toggle() }
            return .success
        }
        center.nextTrackCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.next() }
            return .success
        }
        center.previousTrackCommand.addTarget { [weak self] _ in
            Task { @MainActor in self?.previous() }
            return .success
        }
        center.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else { return .commandFailed }
            let position = event.positionTime
            Task { @MainActor in self?.seek(to: position) }
            return .success
        }
    }

    private func updateNowPlaying() {
        let center = MPNowPlayingInfoCenter.default()
        guard let song = current else {
            center.nowPlayingInfo = nil
            return
        }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: song.displayTitle,
            MPMediaItemPropertyArtist: "Tonwerk-AI",
            MPNowPlayingInfoPropertyElapsedPlaybackTime: elapsed,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? 1.0 : 0.0,
            MPNowPlayingInfoPropertyMediaType: MPNowPlayingInfoMediaType.audio.rawValue,
        ]
        if duration > 0 {
            info[MPMediaItemPropertyPlaybackDuration] = duration
        }
        if let artwork, artwork.id == song.id {
            info[MPMediaItemPropertyArtwork] = artwork.image
        }
        center.nowPlayingInfo = info
        #if os(macOS)
        center.playbackState = isPlaying ? .playing : .paused
        #endif
    }

    /// The song's own cover for the lock screen; songs without one show none there, as on the web page.
    private func loadArtwork(for song: Song) {
        coverColour = nil
        guard let server, let url = song.coverURL(on: server) else { return }
        Task {
            guard let (data, _) = try? await URLSession.shared.data(from: url),
                  let image = PlatformImage(data: data),
                  current?.id == song.id
            else { return }
            artwork = (song.id, MPMediaItemArtwork(boundsSize: image.size) { _ in image })
            #if canImport(UIKit)
            let picture = image.cgImage
            #else
            let picture = image.cgImage(forProposedRect: nil, context: nil, hints: nil)
            #endif
            coverColour = picture.map(VideoArt.coverColour)
            updateNowPlaying()
        }
    }
}
