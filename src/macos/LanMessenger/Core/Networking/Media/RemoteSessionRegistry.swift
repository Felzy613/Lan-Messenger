import Foundation

// The ~10 s accept window, one-in-flight-session-per-peer, and single-shot
// session_id lookup.
//
// The clock is injected, so the entire window-expiry behaviour is testable in
// zero wall-clock time. Without that seam the only way to test a 10 s window is
// to wait 10 s, which means in practice it never gets tested.

/// An invite that has been accepted and is waiting for its media connection.
struct RemoteAcceptWindow: Equatable {
    let sessionID: String
    let peerPublicKeyB64: String
    let peerIP: String
    let role: RemoteSessionRole
    let keys: RemoteSessionKeys
    let openedAt: Date
    fileprivate(set) var attached: Bool = false

    static func == (lhs: RemoteAcceptWindow, rhs: RemoteAcceptWindow) -> Bool {
        lhs.sessionID == rhs.sessionID && lhs.attached == rhs.attached
    }
}

final class RemoteSessionRegistry {

    enum OpenResult: Equatable {
        case opened
        case busy(existingSessionID: String)
    }

    enum AdmitResult: Equatable {
        case admitted(RemoteAcceptWindow)
        /// No window with that id, or it was opened by a different peer.
        case unknown
        case expired
        /// The window has already been used. A session_id is single-shot: a
        /// second media_attach on the same id is either a duplicate or an
        /// attacker racing the legitimate viewer, and neither should win.
        case alreadyAttached
    }

    private let now: () -> Date
    private let acceptWindow: TimeInterval
    private let lock = NSLock()

    private var windows: [String: RemoteAcceptWindow] = [:]      // keyed by sessionID
    private var inFlightByPeer: [String: String] = [:]           // peerKey → sessionID
    private var liveSessions: [String: MediaSession] = [:]       // sessionID → session

    init(now: @escaping () -> Date = Date.init, acceptWindow: TimeInterval = 10.0) {
        self.now = now
        self.acceptWindow = acceptWindow
    }

    // MARK: - Accept windows

    /// Opens a window after an invite is accepted. One session in flight per
    /// peer: a peer that can open windows without bound can make a host hold
    /// derived key material for every one of them.
    @discardableResult
    func open(sessionID: String,
              peerPublicKeyB64: String,
              peerIP: String,
              role: RemoteSessionRole,
              keys: RemoteSessionKeys) -> OpenResult {
        lock.lock(); defer { lock.unlock() }
        pruneLocked()

        if let existing = inFlightByPeer[peerPublicKeyB64], existing != sessionID {
            return .busy(existingSessionID: existing)
        }
        windows[sessionID] = RemoteAcceptWindow(
            sessionID: sessionID, peerPublicKeyB64: peerPublicKeyB64, peerIP: peerIP,
            role: role, keys: keys, openedAt: now())
        inFlightByPeer[peerPublicKeyB64] = sessionID
        return .opened
    }

    /// Consumes a window for an inbound `media_attach`.
    ///
    /// `peerPublicKeyB64` is checked as well as the id: a session id travels in
    /// plaintext on the wire, so any LAN host that sees one could otherwise
    /// attach to a window it did not open.
    func admit(sessionID: String, peerPublicKeyB64: String) -> AdmitResult {
        lock.lock(); defer { lock.unlock() }
        pruneLocked()

        guard var window = windows[sessionID],
              window.peerPublicKeyB64 == peerPublicKeyB64 else { return .unknown }
        guard !window.attached else { return .alreadyAttached }
        guard now().timeIntervalSince(window.openedAt) <= acceptWindow else {
            windows[sessionID] = nil
            inFlightByPeer[window.peerPublicKeyB64] = nil
            return .expired
        }
        window.attached = true
        windows[sessionID] = window
        return .admitted(window)
    }

    func cancel(sessionID: String) {
        lock.lock(); defer { lock.unlock() }
        if let window = windows.removeValue(forKey: sessionID) {
            inFlightByPeer[window.peerPublicKeyB64] = nil
        }
    }

    func hasWindow(sessionID: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return windows[sessionID] != nil
    }

    func inFlightSessionID(forPeer peerPublicKeyB64: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        pruneLocked()
        return inFlightByPeer[peerPublicKeyB64]
    }

    /// Drops windows that were never used. Called on every registry operation
    /// rather than from a timer — an expired window costs nothing to hold for a
    /// few extra seconds, and a timer here would be one more thing to starve.
    private func pruneLocked() {
        let cutoff = now().addingTimeInterval(-acceptWindow)
        for (id, window) in windows where !window.attached && window.openedAt < cutoff {
            windows[id] = nil
            inFlightByPeer[window.peerPublicKeyB64] = nil
        }
    }

    // MARK: - Live sessions

    func register(_ session: MediaSession) {
        lock.lock(); defer { lock.unlock() }
        liveSessions[session.sessionID] = session
    }

    func remove(sessionID: String) {
        lock.lock()
        let window = windows.removeValue(forKey: sessionID)
        if let key = window?.peerPublicKeyB64 { inFlightByPeer[key] = nil }
        liveSessions[sessionID] = nil
        lock.unlock()
    }

    func session(id: String) -> MediaSession? {
        lock.lock(); defer { lock.unlock() }
        return liveSessions[id]
    }

    var liveSessionCount: Int {
        lock.lock(); defer { lock.unlock() }
        return liveSessions.count
    }

    /// Closes everything. Reached from `NetworkCoordinator.stop()` so a detached
    /// socket can never outlive the network stack that produced it.
    func closeAll() {
        lock.lock()
        let sessions = Array(liveSessions.values)
        liveSessions.removeAll()
        windows.removeAll()
        inFlightByPeer.removeAll()
        lock.unlock()
        for session in sessions { session.stop() }
    }
}
