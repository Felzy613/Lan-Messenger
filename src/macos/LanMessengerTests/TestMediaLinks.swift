import Foundation
@testable import LanMessenger

// In-memory MediaLink doubles. These are what make everything above the socket
// exercisable without binding a port — the media transport's whole layering
// exists so that only SocketMediaLink is untestable.

/// A link fed from memory. `feed` appends readable bytes; `written` is whatever
/// the code under test wrote.
final class LoopbackMediaLink: MediaLink {

    let peerIP: String
    private let condition = NSCondition()
    private var inbox = Data()
    private var outbox = Data()
    private var finished = false
    private var closed = false

    init(peerIP: String = "10.0.0.9") { self.peerIP = peerIP }

    var isClosed: Bool {
        condition.lock(); defer { condition.unlock() }
        return closed
    }

    var written: Data {
        condition.lock(); defer { condition.unlock() }
        return outbox
    }

    func feed(_ bytes: Data) {
        condition.lock()
        inbox.append(bytes)
        condition.broadcast()
        condition.unlock()
    }

    /// Subsequent reads that cannot be satisfied return `.closed`.
    func finish() {
        condition.lock()
        finished = true
        condition.broadcast()
        condition.unlock()
    }

    func readExact(into buffer: inout [UInt8], count: Int) -> MediaLinkRead {
        guard count > 0 else { return .ok }
        condition.lock()
        while inbox.count < count && !finished && !closed {
            condition.wait()
        }
        if closed { condition.unlock(); return .closed }
        guard inbox.count >= count else { condition.unlock(); return .closed }
        let chunk = inbox.prefix(count)
        inbox.removeFirst(count)
        condition.unlock()
        for (i, byte) in chunk.enumerated() { buffer[i] = byte }
        return .ok
    }

    func writeAll(_ bytes: Data) -> Bool {
        condition.lock(); defer { condition.unlock() }
        guard !closed else { return false }
        outbox.append(bytes)
        return true
    }

    func shutdownAndClose() {
        condition.lock()
        closed = true
        condition.broadcast()
        condition.unlock()
    }
}

/// A link whose reads block until released. Models a peer that has connected and
/// then gone quiet — which is the normal state of a media session whose host
/// screen is static, and the state in which a shared queue starves everything.
final class BlockingMediaLink: MediaLink {

    let peerIP = "10.0.0.10"
    private let condition = NSCondition()
    private var released = false
    private var closed = false

    var isClosed: Bool {
        condition.lock(); defer { condition.unlock() }
        return closed
    }

    func readExact(into buffer: inout [UInt8], count: Int) -> MediaLinkRead {
        condition.lock()
        while !released && !closed { condition.wait() }
        let wasClosed = closed
        condition.unlock()
        return wasClosed ? .closed : .closed
    }

    func writeAll(_ bytes: Data) -> Bool {
        condition.lock(); defer { condition.unlock() }
        return !closed
    }

    func release() {
        condition.lock(); released = true; condition.broadcast(); condition.unlock()
    }

    func shutdownAndClose() {
        condition.lock(); closed = true; condition.broadcast(); condition.unlock()
    }
}
