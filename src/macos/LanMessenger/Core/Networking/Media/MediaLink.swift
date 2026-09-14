import Foundation
import Darwin

// THE testability seam. Three methods, and they are the entire socket surface of
// the media transport.
//
// Everything above this protocol — framing, mux, demux, sequence enforcement,
// the session state machine — is exercised in tests against an in-memory double.
// Nothing below it needs a test, because `SocketMediaLink` contains no logic
// worth testing. That split is what makes the Windows half reviewable from a Mac
// at all: only ~90 lines per platform are untestable here.
//
// Deliberately has no "read with timeout" method. The inherited socket read
// timeout is CLEARED at detach, so a read can only end in bytes, clean close, or
// error — never in "some bytes consumed, unknown how many", which is the state
// that makes mid-frame resynchronisation necessary. Liveness is the session
// watchdog's job, on its own queue, not the reader's.
enum MediaLinkRead: Equatable {
    case ok
    case closed
    case failed(Int32)
}

protocol MediaLink: AnyObject {
    /// Blocking. Fills exactly `count` bytes or reports why it could not.
    func readExact(into buffer: inout [UInt8], count: Int) -> MediaLinkRead
    /// Blocking. Writes every byte, or returns false.
    func writeAll(_ bytes: Data) -> Bool
    /// Idempotent. Must interrupt a reader parked in `readExact`.
    func shutdownAndClose()
    var isClosed: Bool { get }
    var peerIP: String { get }
}

/// The only untestable code in the media transport: a thin adapter over a
/// descriptor that has already been accepted and validated.
final class SocketMediaLink: MediaLink {

    let peerIP: String
    private var fd: Int32
    private let lock = NSLock()
    private var closed = false

    var isClosed: Bool {
        lock.lock(); defer { lock.unlock() }
        return closed
    }

    /// Takes ownership of an already-accepted descriptor.
    ///
    /// Two socket options matter here and neither is set by the listener:
    ///
    /// * `SO_RCVTIMEO` is CLEARED. Inbound JSON sockets inherit a 30 s read
    ///   timeout, which is right for a request/response frame and catastrophic
    ///   for a media session: a legitimately idle stream — a completely static
    ///   screen produces no frames at all — would be torn down mid-session.
    /// * `TCP_NODELAY` is SET. Without it Nagle plus delayed ACK parks a 20-byte
    ///   input event for tens of milliseconds, which is exactly the latency the
    ///   16 KiB fragmentation exists to avoid.
    ///
    /// `SO_SNDBUF` is also capped. If only the fragmentation is implemented and
    /// this is not, the feature does not meet its latency goal: an input frame
    /// handed to `send()` still sits behind everything already absorbed into an
    /// auto-tuned kernel buffer. 128 KiB stays above the bandwidth-delay product
    /// of every realistic LAN path (1 Gbit × 1 ms ≈ 125 KB), so throughput is
    /// untouched.
    init(adopting descriptor: Int32, peerIP: String) {
        self.fd = descriptor
        self.peerIP = peerIP

        var noTimeout = timeval(tv_sec: 0, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &noTimeout, socklen_t(MemoryLayout<timeval>.size))

        var on: Int32 = 1
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &on, socklen_t(MemoryLayout<Int32>.size))

        var sendBuffer: Int32 = 128 * 1024
        setsockopt(fd, SOL_SOCKET, SO_SNDBUF, &sendBuffer, socklen_t(MemoryLayout<Int32>.size))
    }

    func readExact(into buffer: inout [UInt8], count: Int) -> MediaLinkRead {
        guard count > 0 else { return .ok }
        var total = 0
        while total < count {
            let n = buffer.withUnsafeMutableBytes { ptr -> Int in
                Darwin.recv(fd, ptr.baseAddress!.advanced(by: total), count - total, 0)
            }
            if n == 0 { return .closed }
            if n < 0 {
                if errno == EINTR { continue }
                return .failed(errno)
            }
            total += n
        }
        return .ok
    }

    /// Byte-exact send with a writability poll before every attempt.
    ///
    /// `poll(POLLOUT)` rather than `SO_SNDTIMEO`, for the reason
    /// `FileTransferService.rawSend` already documents at length: on Darwin,
    /// when the peer's receive window reaches zero the kernel's persist timer
    /// keeps the connection alive and `SO_SNDTIMEO` is silently ignored, so
    /// `send()` blocks forever. A viewer that stops draining is precisely the
    /// remote-desktop failure mode, so this must not be left to chance.
    func writeAll(_ bytes: Data) -> Bool {
        guard !bytes.isEmpty else { return true }
        var offset = 0
        while offset < bytes.count {
            var pfd = pollfd()
            pfd.fd = fd
            pfd.events = Int16(POLLOUT)
            let ready = Darwin.poll(&pfd, 1, Self.writeTimeoutMs)
            guard ready > 0 else { return false }      // 0 = timeout, < 0 = error

            let errMask = Int16(bitPattern: UInt16(POLLERR) | UInt16(POLLHUP) | UInt16(POLLNVAL))
            guard pfd.revents & errMask == 0 else { return false }

            let n = bytes.withUnsafeBytes { ptr -> Int in
                Darwin.send(fd, ptr.baseAddress!.advanced(by: offset), bytes.count - offset, 0)
            }
            if n <= 0 {
                if n < 0 && errno == EINTR { continue }
                return false
            }
            offset += n
        }
        return true
    }

    private static let writeTimeoutMs: Int32 = 10_000

    /// `shutdown()` before `close()` so a reader parked in `recv()` on another
    /// queue returns immediately instead of waiting for the fd to be reused.
    func shutdownAndClose() {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { return }
        closed = true
        Darwin.shutdown(fd, SHUT_RDWR)
        Darwin.close(fd)
        fd = -1
    }
}
