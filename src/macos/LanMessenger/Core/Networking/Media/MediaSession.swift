import Foundation
import CryptoKit

// Owns the three execution contexts of a live media session and nothing else of
// substance.
//
// THREADING — this is the part that has already gone wrong twice in this
// codebase, in DiscoveryService, and the rule is not negotiable:
//
//   readQueue   the blocking readFrame() loop. It never returns while running.
//               Nothing else may ever be scheduled here.
//   writeQueue  drains the scheduler. Owns the sequence counter, which is why
//               MediaFrameWriter is documented single-caller.
//   timerQueue  keepalive, stats and the watchdog.
//
// The timers are on their own queue for exactly the reason the discovery health
// summary now has `healthQueue`: a timer scheduled onto a serial queue occupied
// by a never-returning loop is never dequeued, and the resulting silence looks
// like nothing is wrong. See DiscoveryServiceQueueTests and
// RemoteDesktopQueueTests, which assert the same invariant for both.
final class MediaSession {

    let sessionID: String
    let peerPublicKeyB64: String
    let peerIP: String
    let role: RemoteSessionRole

    private let link: MediaLink
    private let reader: MediaFrameReader
    private let writer: MediaFrameWriter
    private let scheduler: MediaWriteScheduler

    private let readQueue: DispatchQueue
    private let writeQueue: DispatchQueue
    private let timerQueue: DispatchQueue

    private var keepaliveTimer: DispatchSourceTimer?
    private var watchdogTimer: DispatchSourceTimer?

    private let stateLock = NSLock()
    private var running = false
    private var writeScheduled = false
    private var lastInboundAt: Date

    // ── tunables. Vars so tests can shrink them; production defaults are the
    // protocol's. Same idiom as DiscoveryService.healthInterval, and for the
    // same reason: at the real intervals these paths are untestable in practice.
    var keepaliveInterval: TimeInterval = 5.0
    var watchdogInterval: TimeInterval = 10.0
    var watchdogTimeout: TimeInterval = 30.0

    // ── counted seams, reachable ONLY from timerQueue.
    //
    // Deliberately not anything the read loop also touches inline: the discovery
    // regression test documents why that distinction decides whether a test can
    // detect starvation at all. A counter the read path also bumps keeps ticking
    // against broken code and the test passes while the bug is live.
    var onKeepaliveTick: (() -> Void)?
    var onWatchdogTick: (() -> Void)?

    /// Delivered from readQueue. Callers that touch UI must hop themselves.
    var onFrame: ((MediaInboundFrame) -> Void)?
    /// Delivered once, from whichever context detected the end.
    var onClosed: ((MediaProtocolError?) -> Void)?

    init(sessionID: String,
         peerPublicKeyB64: String,
         peerIP: String,
         role: RemoteSessionRole,
         keys: RemoteSessionKeys,
         link: MediaLink,
         now: Date = Date()) {
        self.sessionID = sessionID
        self.peerPublicKeyB64 = peerPublicKeyB64
        self.peerIP = peerIP
        self.role = role
        self.link = link
        self.lastInboundAt = now

        let short = sessionID.prefix(8)
        self.readQueue  = DispatchQueue(label: "com.dave.lanmessenger.media.read.\(short)",  qos: .userInitiated)
        self.writeQueue = DispatchQueue(label: "com.dave.lanmessenger.media.write.\(short)", qos: .userInitiated)
        self.timerQueue = DispatchQueue(label: "com.dave.lanmessenger.media.timer.\(short)", qos: .utility)

        self.scheduler = MediaWriteScheduler()
        self.reader = MediaFrameReader(link: link,
                                       openingKey: keys.openingKey(as: role),
                                       openingSalt: keys.openingSalt(as: role))
        self.writer = MediaFrameWriter(link: link,
                                       sealingKey: keys.sealingKey(as: role),
                                       sealingSalt: keys.sealingSalt(as: role))
    }

    var inputArmed: Bool {
        get { reader.inputArmed }
        set { reader.inputArmed = newValue }
    }

    var statistics: (framesRead: UInt64, bytesRead: UInt64,
                     framesWritten: UInt64, bytesWritten: UInt64,
                     droppedVideo: Int, droppedKeyframes: Int) {
        (reader.framesRead, reader.bytesRead,
         writer.framesWritten, writer.bytesWritten,
         scheduler.droppedVideoFrames, scheduler.droppedKeyframes)
    }

    // MARK: - Lifecycle

    func start() {
        stateLock.lock()
        guard !running else { stateLock.unlock(); return }
        running = true
        stateLock.unlock()

        NetLogger.remote(event: "session_start", peer: peerIP,
                         sessionID: sessionID, role: role.rawValue)
        startTimers()
        readQueue.async { [weak self] in self?.readLoop() }
    }

    func stop(reason: MediaProtocolError? = nil) {
        stateLock.lock()
        guard running else { stateLock.unlock(); return }
        running = false
        stateLock.unlock()

        keepaliveTimer?.cancel(); keepaliveTimer = nil
        watchdogTimer?.cancel(); watchdogTimer = nil
        link.shutdownAndClose()
        scheduler.reset()

        let stats = statistics
        NetLogger.remote(
            event: reason == nil ? "session_end" : "session_fault",
            peer: peerIP, sessionID: sessionID, role: role.rawValue,
            bytes: Int(truncatingIfNeeded: stats.bytesRead &+ stats.bytesWritten),
            reason: reason?.description)
        onClosed?(reason)
    }

    private var isRunning: Bool {
        stateLock.lock(); defer { stateLock.unlock() }
        return running
    }

    // MARK: - Read loop (readQueue only)

    private func readLoop() {
        while isRunning {
            let outcome = reader.readFrame()
            switch outcome {
            case .frame(let frame):
                noteInbound()
                onFrame?(frame)
            case .partial:
                noteInbound()
            case .dropped(let channel, let reason):
                noteInbound()
                NetLogger.remote(event: "frame_dropped", peer: peerIP, sessionID: sessionID,
                                 channel: Int(channel), reason: reason)
            case .closed:
                stop(reason: nil)
                return
            case .fault(let error):
                NetLogger.remote(event: faultEvent(error), peer: peerIP, sessionID: sessionID,
                                 reason: error.description)
                stop(reason: error)
                return
            }
        }
    }

    private func faultEvent(_ error: MediaProtocolError) -> String {
        switch error {
        case .sequenceNotIncreasing: return "sequence_violation"
        case .decryptFailed:         return "handshake_failed"
        default:                     return "error"
        }
    }

    private func noteInbound() {
        stateLock.lock(); lastInboundAt = Date(); stateLock.unlock()
    }

    // MARK: - Write path

    /// Thread-safe. Returns the scheduler's verdict — never discard it; an
    /// overflow means the socket is wedged and the session is already dead.
    @discardableResult
    func submit(_ frame: MediaOutboundFrame) -> MediaWriteScheduler.Submission {
        let verdict = scheduler.submit(frame)
        if case .overflow(let channel) = verdict {
            NetLogger.remote(event: "error", peer: peerIP, sessionID: sessionID,
                             channel: Int(channel.rawValue), reason: "send queue overflow")
            stop(reason: .linkFailed(0))
            return verdict
        }
        scheduleDrain()
        return verdict
    }

    /// Coalesced: a 30 fps producer causes at most one queued drain at a time.
    private func scheduleDrain() {
        stateLock.lock()
        guard running, !writeScheduled else { stateLock.unlock(); return }
        writeScheduled = true
        stateLock.unlock()
        writeQueue.async { [weak self] in self?.drain() }
    }

    private func drain() {
        stateLock.lock(); writeScheduled = false; stateLock.unlock()
        while isRunning, let segment = scheduler.nextSegment() {
            guard writer.writeSegment(segment) else {
                NetLogger.remote(event: "error", peer: peerIP, sessionID: sessionID,
                                 reason: "media write failed")
                stop(reason: .linkFailed(0))
                return
            }
        }
    }

    // MARK: - Timers (timerQueue only)

    private func startTimers() {
        let keepalive = DispatchSource.makeTimerSource(queue: timerQueue)
        keepalive.schedule(deadline: .now() + keepaliveInterval, repeating: keepaliveInterval)
        keepalive.setEventHandler { [weak self] in
            guard let self, self.isRunning else { return }
            self.submit(MediaOutboundFrame(channel: .control, payload: Data(), captureUs: 0))
            self.onKeepaliveTick?()
        }
        keepalive.resume()
        keepaliveTimer = keepalive

        let watchdog = DispatchSource.makeTimerSource(queue: timerQueue)
        watchdog.schedule(deadline: .now() + watchdogInterval, repeating: watchdogInterval)
        watchdog.setEventHandler { [weak self] in
            guard let self, self.isRunning else { return }
            self.onWatchdogTick?()
            self.stateLock.lock()
            let idle = Date().timeIntervalSince(self.lastInboundAt)
            self.stateLock.unlock()
            // A crashed viewer must never leave a host's screen being captured
            // indefinitely. That is the single worst failure this feature can
            // have, so the watchdog is not optional.
            if idle > self.watchdogTimeout {
                NetLogger.remote(event: "watchdog_fired", peer: self.peerIP,
                                 sessionID: self.sessionID,
                                 reason: "no inbound frames for \(Int(idle))s")
                self.stop(reason: .linkFailed(ETIMEDOUT))
            }
        }
        watchdog.resume()
        watchdogTimer = watchdog
    }
}
