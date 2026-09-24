import CryptoKit
import Foundation

// Handles outgoing file transfers (one at a time per peer) and incoming file reassembly.
// Outgoing: open a fresh TCP connection per file, send file_start / file_chunks / file_end.
// Incoming: receive chunks via the shared TCP listener, write to temp, finalize on file_end.
//
// Threading model
// ───────────────
//  • All public API, callbacks, and queue-management run on @MainActor.
//  • Outgoing I/O (socket connect, Darwin.send loop) runs on `sendQueue` — a dedicated
//    serial DispatchQueue designed for blocking syscalls. This keeps blocking work off
//    Swift's cooperative thread pool so other async work stays responsive.
//  • Incoming chunk decrypt + disk write runs on `chunkQueue` (serial). The serial
//    discipline preserves TCP chunk ordering without a sequence number.
//  • Per-chunk byte counting and throttle checks run on `chunkQueue` via ChunkQueueState.
//    Only throttled progress callbacks hop to the main thread (~12 Hz), eliminating the
//    per-chunk main-thread dispatch that previously caused UI freezes on large transfers.
//  • Progress/complete/error callbacks are always delivered on the main thread via
//    DispatchQueue.main.async — fire-and-forget so they never block the send loop.

@MainActor
final class FileTransferService {

    static let shared = FileTransferService()

    // Every callback names the peer by identity key — the conversation the
    // transfer belongs to — never by the address it travelled over.
    var onProgress:     ((String, String, Int64, Int64) -> Void)?          // peer, label, bytes, total
    var onComplete:     ((String, String, String, URL?) -> Void)?          // peer, label, transferId, localURL (non-nil on sender)
    var onError:        ((String, String) -> Void)?                        // peer, message
    var onIncomingFile: ((String, String, String, String, URL) -> Void)?   // peer, fromAddress, sender, transferId, finalURL

    /// Whether `key` is known to be at `ip` — set by AppModel. Consulted only
    /// for an empty file: every chunk proves the sender's key by decrypting
    /// under it, and a transfer with no chunks proves nothing, so it is taken
    /// only from an address that key already lives at.
    var isBoundAddress: ((_ key: String, _ ip: String) -> Bool)?

    private let chunkSize = 64 * 1024   // 64 KiB per chunk
    private let tcpPort   = 54232

    // Coalesces incoming progress callbacks to ~12 Hz per transfer.
    // Lives on chunkQueue; no main-thread access after init.
    private let chunkState = ChunkQueueState()
    private let progressInterval: TimeInterval = 0.08

    // Wall-clock start time per active incoming/outgoing transfer.  Used solely
    // to compute duration_ms and bytes_per_sec for the structured "complete"
    // log event.  Cleared on completion / failure.
    private var incomingStartTimes: [FileTransferStore.TransferKey: Date] = [:]

    // Serial queue: preserves TCP chunk ordering during decrypt + write.
    // handleFileEnd is routed through here too so finalization always
    // happens after the last chunk write completes.
    private let chunkQueue = DispatchQueue(label: "com.dave.lanmessenger.file-chunks", qos: .utility)

    // Dedicated queue for blocking outgoing I/O.  Blocking Darwin.send() here is
    // intentional and safe; it must never be called from the cooperative thread pool.
    private let sendQueue  = DispatchQueue(label: "com.dave.lanmessenger.file-send", qos: .userInitiated)

    // Last failed outgoing attempt per peer. retryQueue is invoked on every
    // discovery heartbeat (~1.5 s) via deliverPendingFiles; without a cooldown,
    // a peer that accepts UDP discovery but rejects/times out TCP would be
    // re-attempted back-to-back with a 10 s connect-timeout each time.
    private var lastSendFailureAt: [String: Date] = [:]
    private let sendRetryCooldown: TimeInterval = 15

    private init() {}

    // MARK: - Receive (called from NetworkCoordinator via AppModel)

    func handlePacket(_ packet: ValidatedPacket) {
        switch packet {
        case .fileStart(let pkt, let ip): handleFileStart(pkt, fromIP: ip)
        case .fileChunk(let pkt, let ip): handleFileChunk(pkt, fromIP: ip)
        case .fileEnd(let pkt, let ip):   handleFileEnd(pkt, fromIP: ip)
        default: break
        }
    }

    private func handleFileStart(_ pkt: FileStartPacket, fromIP ip: String) {
        let inboxDir = ConfigStore.shared.inboxDirectory
        let safe     = PacketValidator.sanitizeFilename(pkt.filename)
        let peer     = pkt.senderPublicKeyB64
        guard PeerID.isKey(peer) else {
            NetLogger.fileTransfer(
                event: "failed", transferId: pkt.transferId, peer: ip,
                direction: "incoming", filename: safe, size: pkt.size,
                reason: "no usable sender key"
            )
            return
        }

        NetLogger.fileTransfer(
            event: "start", transferId: pkt.transferId, peer: ip,
            direction: "incoming", filename: safe, size: pkt.size,
            mime: Self.mimeFromFilename(safe)
        )

        guard FileTransferStore.shared.beginIncoming(
            transferId:         pkt.transferId,
            filename:           pkt.filename,
            size:               pkt.size,
            senderIP:           ip,
            senderPublicKeyB64: pkt.senderPublicKeyB64,
            inboxDir:           inboxDir
        ) != nil else {
            NetLogger.fileTransfer(
                event: "failed", transferId: pkt.transferId, peer: ip,
                direction: "incoming", filename: safe, size: pkt.size,
                reason: "cannot create temp file — disk full or permission denied"
            )
            onError?(peer, "Cannot save incoming file — check disk space and inbox permissions")
            return
        }
        incomingStartTimes[FileTransferStore.TransferKey(ip: ip, transferId: pkt.transferId)] = Date()
        onProgress?(peer, "Receiving \(safe)", 0, pkt.size)
    }

    private func handleFileChunk(_ pkt: FileChunkPacket, fromIP ip: String) {
        let key = FileTransferStore.TransferKey(ip: ip, transferId: pkt.transferId)
        guard let transfer   = FileTransferStore.shared.incoming[key],
              let fileHandle = transfer.fileHandle else { return }

        // Capture everything needed before leaving the main actor — including
        // our own private key, so the background queue never touches a
        // main-actor-owned singleton (data race / actor-isolation violation).
        let nonce        = pkt.nonce
        let ciphertext   = pkt.ciphertext
        let transferId   = pkt.transferId
        let senderKey    = transfer.senderPublicKeyB64
        let filename     = transfer.filename
        let totalSize    = transfer.totalSize
        let interval     = progressInterval
        let myPrivateKey = KeyManager.shared.privateKey  // Curve25519.KeyAgreement.PrivateKey

        // Decrypt and write on the serial background queue so the main thread stays
        // free. The serial queue preserves TCP chunk ordering.
        // ChunkQueueState is only ever accessed from within chunkQueue.async blocks;
        // serial dispatch provides the required mutual exclusion.
        chunkQueue.async { [weak self] in
            guard let self else { return }
            let aad = Data(transferId.utf8)
            guard let plaintext = try? SessionCrypto.decryptFromPeer(
                myPrivate:          myPrivateKey,
                peerPublicKeyB64:   senderKey,
                nonceB64:           nonce,
                ciphertextB64:      ciphertext,
                aad:                aad
            ) else {
                // Remembered, not just logged: a file with a chunk missing is
                // corrupt, and one where no chunk opens was never from the key
                // it named. Either way file_end must not finalize it.
                self.chunkState.markFailed(key)
                NetLogger.fileTransfer(
                    event: "failed", transferId: transferId, peer: ip,
                    direction: "incoming", filename: filename,
                    reason: "chunk decrypt failed"
                )
                return
            }

            fileHandle.write(plaintext)
            let count = Int64(plaintext.count)

            // Update byte counter and check throttle entirely on chunkQueue — no
            // main-thread hop per chunk. Only when the throttle fires do we push a
            // single progress event to main. For a 100 MB file (~1600 chunks at LAN
            // speed) this reduces main-thread dispatches from 1600 to ~96.
            let (received, shouldReport) = self.chunkState.addBytes(
                count,
                forKey: key,
                totalSize: totalSize,
                interval: interval
            )

            guard shouldReport else { return }
            let bytes = received  // copy for main-thread capture
            NetLogger.debug("FileTransfer",
                "progress dir=incoming transfer_id=\(transferId) recv=\(bytes) size=\(totalSize)")
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                FileTransferStore.shared.setBytesReceived(bytes, forKey: key)
                self.onProgress?(senderKey, "Receiving \(filename)", bytes, totalSize)
            }
        }
    }

    private func handleFileEnd(_ pkt: FileEndPacket, fromIP ip: String) {
        let key = FileTransferStore.TransferKey(ip: ip, transferId: pkt.transferId)
        guard let transfer = FileTransferStore.shared.incoming[key] else { return }
        let filename   = transfer.filename
        let sender     = pkt.sender
        let transferId = pkt.transferId
        let peer       = transfer.senderPublicKeyB64

        // Route through chunkQueue so finalization runs only after the last
        // chunk write has completed (serial queue drains in arrival order).
        chunkQueue.async { [weak self] in
            guard let self else { return }
            let outcome = self.chunkState.remove(key)  // clean up tracker before main hop
            NetLogger.debug("FileTransfer",
                "all chunks received transfer_id=\(transferId) — finalizing")
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                let startedAt = self.incomingStartTimes.removeValue(forKey: key)
                let totalSize = FileTransferStore.shared.incoming[key]?.totalSize
                // Chunks that decrypted are what prove the sender's key. A
                // failed chunk means the file is corrupt or was never theirs;
                // no chunks at all proves nothing, so an empty file is taken
                // only from an address that key is already known at.
                let refusal: String? = {
                    if outcome.failed { return "one or more chunks failed to decrypt" }
                    if outcome.received == 0, self.isBoundAddress?(peer, ip) == false {
                        return "empty file from an address \(peer.prefix(8)) is not known at"
                    }
                    return nil
                }()
                if let refusal {
                    FileTransferStore.shared.cancelIncoming(key: key)
                    NetLogger.fileTransfer(
                        event: "failed", transferId: transferId, peer: ip,
                        direction: "incoming", filename: filename, reason: refusal
                    )
                    self.onError?(peer, "Could not receive \(filename) — it arrived damaged")
                    return
                }
                guard let finalURL = FileTransferStore.shared.finalizeIncoming(
                    key:      key,
                    inboxDir: ConfigStore.shared.inboxDirectory
                ) else {
                    NetLogger.fileTransfer(
                        event: "failed", transferId: transferId, peer: ip,
                        direction: "incoming", filename: filename,
                        reason: "finalize failed (missing transfer record)"
                    )
                    return
                }
                let durationMs = startedAt.map { Int(Date().timeIntervalSince($0) * 1000) }
                let bps: Double? = {
                    guard let ms = durationMs, ms > 0, let sz = totalSize else { return nil }
                    return Double(sz) * 1000.0 / Double(ms)
                }()
                NetLogger.fileTransfer(
                    event: "complete", transferId: transferId, peer: ip,
                    direction: "incoming", filename: filename, size: totalSize,
                    mime: Self.mimeFromFilename(filename),
                    durationMs: durationMs, bytesPerSec: bps
                )
                self.onComplete?(peer, "Receiving \(filename)", transferId, nil)
                self.onIncomingFile?(peer, ip, sender, transferId, finalURL)
            }
        }
    }

    // MARK: - Send

    /// Queues a file for the peer with identity key `peer`, and starts it if
    /// nothing else is going to them. `address` is where that device is right
    /// now; the queue itself belongs to the key, so a file waiting out a DHCP
    /// change goes to the same device at its new address, not to whoever
    /// inherited the old one.
    func enqueue(filePath: String, toPeer peer: String, address: String) {
        let url = URL(fileURLWithPath: filePath)
        NetLogger.fileTransfer(
            event: "queued", peer: address, direction: "outgoing",
            filename: url.lastPathComponent, size: Self.fileSize(atPath: filePath),
            mime: Self.mimeFromFilename(url.lastPathComponent)
        )
        FileTransferStore.shared.enqueue(path: filePath, filename: url.lastPathComponent, forPeer: peer)
        startNextIfIdle(peer: peer, address: address)
    }

    // Best-effort file size lookup for log enrichment.  Returns nil when the
    // file has been deleted between enqueue and send.
    nonisolated static func fileSize(atPath path: String) -> Int64? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let size = attrs[.size] as? Int64 else { return nil }
        return size
    }

    // Re-trigger the queue for a peer that has just come back online — covers
    // the case where a previous attempt failed and the file is still queued.
    func retryQueue(toPeer peer: String, address: String) {
        startNextIfIdle(peer: peer, address: address)
    }

    private func startNextIfIdle(peer peerPublicKeyB64: String, address peerIP: String) {
        // No address means the device is not on the LAN; the queue waits for it.
        guard !peerIP.isEmpty,
              !FileTransferStore.shared.activeOutgoing.contains(peerPublicKeyB64),
              let item = FileTransferStore.shared.outgoingQueues[peerPublicKeyB64]?.first else { return }
        if let lastFail = lastSendFailureAt[peerPublicKeyB64],
           Date().timeIntervalSince(lastFail) < sendRetryCooldown { return }

        FileTransferStore.shared.markTransferStarted(peer: peerPublicKeyB64)
        let path     = item.path
        let filename = item.filename

        // Capture all main-actor-isolated values here, before the sendQueue
        // dispatch.  sendFileBlocking is nonisolated and runs on a background
        // serial queue; accessing KeyManager / ConfigStore singletons from that
        // queue without first capturing their values on the main actor is a data
        // race that can corrupt the chunk-encrypt loop and manifest as a sender
        // freeze or crash for large files.
        let myPrivateKey   = KeyManager.shared.privateKey  // Curve25519.KeyAgreement.PrivateKey
        let myPublicKeyB64 = KeyManager.shared.publicKeyB64
        let myName         = ConfigStore.shared.config.username

        let outgoingStartedAt = Date()
        let outgoingSize = Self.fileSize(atPath: path)
        NetLogger.fileTransfer(
            event: "start", peer: peerIP, direction: "outgoing",
            filename: filename, size: outgoingSize,
            mime: Self.mimeFromFilename(filename)
        )

        // Dispatch blocking I/O to sendQueue so Swift's cooperative thread pool stays
        // free for other async work. Progress and completion callbacks are delivered
        // back to main via DispatchQueue.main.async (fire-and-forget).
        sendQueue.async { [weak self] in
            let success = Self.sendFileBlocking(
                path:             path,
                peerIP:           peerIP,
                peerPublicKeyB64: peerPublicKeyB64,
                filename:         filename,
                myPrivateKey:     myPrivateKey,
                myPublicKeyB64:   myPublicKeyB64,
                myName:           myName,
                chunkSize:        65536,
                tcpPort:          54232,
                onProgress: { bytes, total in
                    DispatchQueue.main.async { [weak self] in
                        self?.onProgress?(peerPublicKeyB64, "Sending \(filename)", bytes, total)
                    }
                },
                onComplete: { url, transferId in
                    DispatchQueue.main.async { [weak self] in
                        self?.onComplete?(peerPublicKeyB64, "Sending \(filename)", transferId, url)
                    }
                }
            )

            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                FileTransferStore.shared.markTransferFinished(peer: peerPublicKeyB64, success: success)
                let durationMs = Int(Date().timeIntervalSince(outgoingStartedAt) * 1000)
                let bps: Double? = {
                    guard durationMs > 0, let sz = outgoingSize else { return nil }
                    return Double(sz) * 1000.0 / Double(durationMs)
                }()
                if success {
                    self.lastSendFailureAt.removeValue(forKey: peerPublicKeyB64)
                    NetLogger.fileTransfer(
                        event: "complete", peer: peerIP, direction: "outgoing",
                        filename: filename, size: outgoingSize,
                        mime: Self.mimeFromFilename(filename),
                        bytesSent: outgoingSize,
                        durationMs: durationMs, bytesPerSec: bps
                    )
                    self.startNextIfIdle(peer: peerPublicKeyB64, address: peerIP)
                } else {
                    self.lastSendFailureAt[peerPublicKeyB64] = Date()
                    NetLogger.fileTransfer(
                        event: "failed", peer: peerIP, direction: "outgoing",
                        filename: filename, size: outgoingSize,
                        durationMs: durationMs,
                        reason: "will retry on reconnect"
                    )
                    self.onError?(peerPublicKeyB64, "Failed to send \(filename) — will retry when peer reconnects")
                }
            }
        }
    }

    // Lightweight MIME inference for log enrichment.  Not exhaustive — only
    // returns the common categories the support workflow cares about so the
    // log line stays readable.  Returns nil for unknown extensions.
    nonisolated static func mimeFromFilename(_ filename: String) -> String? {
        let lower = filename.lowercased()
        guard let dot = lower.lastIndex(of: ".") else { return nil }
        let ext = String(lower[lower.index(after: dot)...])
        switch ext {
        case "png":  return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "gif":  return "image/gif"
        case "heic": return "image/heic"
        case "webp": return "image/webp"
        case "mp4":  return "video/mp4"
        case "mov":  return "video/quicktime"
        case "mkv":  return "video/x-matroska"
        case "webm": return "video/webm"
        case "pdf":  return "application/pdf"
        case "zip":  return "application/zip"
        case "txt":  return "text/plain"
        case "md":   return "text/markdown"
        case "json": return "application/json"
        case "csv":  return "text/csv"
        case "doc", "docx": return "application/msword"
        case "xls", "xlsx": return "application/vnd.ms-excel"
        case "ppt", "pptx": return "application/vnd.ms-powerpoint"
        case "mp3":  return "audio/mpeg"
        case "wav":  return "audio/wav"
        case "m4a":  return "audio/mp4"
        default:     return nil
        }
    }

    // Blocking send — must only be called from sendQueue, never from the main actor.
    // Returns true on success; false on any I/O, connection, or crypto error.
    // `nonisolated` removes the implicit @MainActor inheritance so the method can be
    // called safely from the background sendQueue without a concurrency warning.
    //
    // `myPrivateKey`, `myPublicKeyB64`, and `myName` must be captured from the main
    // actor BEFORE this method is called (see startNextIfIdle).  Accessing
    // KeyManager / ConfigStore directly from the sendQueue is a data race.
    nonisolated private static func sendFileBlocking(
        path:             String,
        peerIP:           String,
        peerPublicKeyB64: String,
        filename:         String,
        myPrivateKey:     Curve25519.KeyAgreement.PrivateKey,
        myPublicKeyB64:   String,
        myName:           String,
        chunkSize:        Int,
        tcpPort:          Int,
        onProgress:       @escaping (Int64, Int64) -> Void,
        onComplete:       @escaping (URL, String) -> Void   // url, transferId
    ) -> Bool {
        let url = URL(fileURLWithPath: path)
        guard let attrs     = try? FileManager.default.attributesOfItem(atPath: path),
              let totalSize = attrs[.size] as? Int64,
              totalSize     >= 0 else {
            NetLogger.error("FileTransfer", "outgoing \"\(filename)\": cannot read file attributes at \(path)")
            return false
        }
        guard let handle = try? FileHandle(forReadingFrom: url) else {
            NetLogger.error("FileTransfer", "outgoing \"\(filename)\": cannot open file for reading")
            return false
        }
        defer { handle.closeFile() }

        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else {
            NetLogger.error("FileTransfer", "outgoing \"\(filename)\": socket() failed errno=\(errno)")
            return false
        }
        defer { Darwin.close(fd) }

        // Disable Nagle's algorithm — reduces latency for the final small frame.
        var noDelay: Int32 = 1
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &noDelay, socklen_t(MemoryLayout<Int32>.size))

        // 10-second send timeout — generous for a loaded LAN, still surfaces a
        // stalled or disappeared peer much faster than the OS default (~75 s).
        var tv = timeval(tv_sec: 10, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        var addr = sockaddr_in()
        addr.sin_family      = sa_family_t(AF_INET)
        addr.sin_port        = UInt16(tcpPort).bigEndian
        addr.sin_addr.s_addr = inet_addr(peerIP)

        // Non-blocking connect with a 10-second poll timeout so an unreachable peer
        // doesn't tie up the send queue for the OS default (~75 s).
        let sockFlags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, sockFlags | O_NONBLOCK)
        let connectResult = withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        if connectResult != 0 {
            guard errno == EINPROGRESS else {
                NetLogger.error("FileTransfer", "outgoing \"\(filename)\": connect() failed errno=\(errno) to \(peerIP):\(tcpPort)")
                return false
            }
            var pfd = pollfd()
            pfd.fd     = fd
            pfd.events = Int16(POLLOUT)
            guard Darwin.poll(&pfd, 1, 10_000) > 0 else {
                NetLogger.error("FileTransfer", "outgoing \"\(filename)\": connect timed out to \(peerIP):\(tcpPort)")
                return false
            }
            var sockErr: Int32 = 0
            var errLen = socklen_t(MemoryLayout<Int32>.size)
            getsockopt(fd, SOL_SOCKET, SO_ERROR, &sockErr, &errLen)
            guard sockErr == 0 else {
                NetLogger.error("FileTransfer", "outgoing \"\(filename)\": connect refused/reset to \(peerIP):\(tcpPort) err=\(sockErr)")
                return false
            }
        }
        _ = fcntl(fd, F_SETFL, sockFlags)  // restore blocking mode for send()

        NetLogger.verbose("FileTransfer", "outgoing \"\(filename)\": connected to \(peerIP):\(tcpPort)")

        let transferId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let myKey      = myPublicKeyB64

        // ── file_start ────────────────────────────────────────────────────────────
        let startPkt: [String: Any] = [
            "type": "file_start", "transfer_id": transferId,
            "filename": filename, "size": totalSize,
            "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
        ]
        guard let startFrame = try? FrameCodec.encodeDict(startPkt),
              rawSend(fd: fd, data: startFrame) else {
            NetLogger.error("FileTransfer", "[\(transferId)] failed to send file_start")
            return false
        }

        NetLogger.info("FileTransfer", "[\(transferId)] sending \"\(filename)\" \(totalSize) bytes to \(peerIP)")
        onProgress(0, totalSize)

        // ── file_chunks ───────────────────────────────────────────────────────────
        // Throttle progress updates to ~12 Hz so we don't flood the main thread.
        var sent: Int64           = 0
        var lastReportAt          = Date.distantPast
        let minInterval: TimeInterval = 0.08

        while true {
            let chunk = handle.readData(ofLength: chunkSize)
            if chunk.isEmpty { break }

            let aad = Data(transferId.utf8)
            guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
                myPrivate:        myPrivateKey,
                peerPublicKeyB64: peerPublicKeyB64,
                plaintext:        chunk,
                aad:              aad
            ) else {
                NetLogger.error("FileTransfer", "[\(transferId)] chunk encrypt failed")
                return false
            }

            let chunkPkt: [String: Any] = [
                "type": "file_chunk", "transfer_id": transferId,
                "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
                "nonce": nonceB64, "ciphertext": ctB64,
            ]
            guard let chunkFrame = try? FrameCodec.encodeDict(chunkPkt),
                  rawSend(fd: fd, data: chunkFrame) else {
                NetLogger.error("FileTransfer", "[\(transferId)] send failed at \(sent)/\(totalSize) bytes")
                return false
            }

            sent += Int64(chunk.count)
            let now = Date()
            if now.timeIntervalSince(lastReportAt) >= minInterval {
                lastReportAt = now
                NetLogger.verbose("FileTransfer", "[\(transferId)] sent \(sent)/\(totalSize)")
                onProgress(sent, totalSize)
            }
        }

        // ── file_end ──────────────────────────────────────────────────────────────
        let endPkt: [String: Any] = [
            "type": "file_end", "transfer_id": transferId,
            "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
        ]
        guard let endFrame = try? FrameCodec.encodeDict(endPkt),
              rawSend(fd: fd, data: endFrame) else {
            NetLogger.error("FileTransfer", "[\(transferId)] failed to send file_end")
            return false
        }

        onProgress(totalSize, totalSize)
        onComplete(url, transferId)
        return true
    }

    // Blocking byte-exact send loop with a per-write poll timeout.
    // Returns false if the socket errors, the peer disconnects, or the
    // writability poll times out.
    //
    // Why poll() instead of SO_SNDTIMEO
    // ------------------------------------
    // SO_SNDTIMEO is unreliable on macOS/Darwin for blocking TCP send() when
    // the remote receive window reaches zero (TCP flow-control / zero-window).
    // In that state the kernel's TCP persist timer keeps the connection alive
    // indefinitely and the socket-level SO_SNDTIMEO is silently ignored,
    // causing Darwin.send() to block forever.  poll() is not subject to that
    // limitation: it measures wall-clock time and returns 0 (timeout) whenever
    // the socket has not become writable within `timeoutMs`, regardless of TCP
    // layer state.  This guarantees that a stalled or slow-draining peer can
    // never freeze the send queue indefinitely.
    nonisolated private static func rawSend(fd: Int32, data: Data, timeoutMs: Int32 = 10_000) -> Bool {
        var offset = 0
        while offset < data.count {
            // Wait for writability before attempting send().  On timeout (0)
            // or error (< 0) bail out immediately so sendQueue is unblocked.
            var pfd = pollfd()
            pfd.fd     = fd
            pfd.events = Int16(POLLOUT)
            let ready  = Darwin.poll(&pfd, 1, timeoutMs)
            guard ready > 0 else { return false }   // 0 = timeout, < 0 = error

            // Bail if the peer closed or reset the connection.
            let errMask = Int16(bitPattern: UInt16(POLLERR) | UInt16(POLLHUP) | UInt16(POLLNVAL))
            guard pfd.revents & errMask == 0 else { return false }

            let n = data.withUnsafeBytes { ptr in
                Darwin.send(fd, ptr.baseAddress!.advanced(by: offset), data.count - offset, 0)
            }
            if n <= 0 { return false }
            offset += n
        }
        return true
    }
}

// MARK: - ChunkQueueState

// Tracks per-chunk byte counts and throttle timestamps for incoming transfers.
// Accessed exclusively from FileTransferService's serial chunkQueue; the
// serial-dispatch discipline provides the required mutual exclusion.
// @unchecked Sendable suppresses the Swift concurrency checker — safety is
// enforced manually via the serial queue.
private final class ChunkQueueState: @unchecked Sendable {
    private var bytesReceived: [FileTransferStore.TransferKey: Int64] = [:]
    private var lastProgressAt: [FileTransferStore.TransferKey: Date] = [:]
    private var failed: Set<FileTransferStore.TransferKey> = []

    func markFailed(_ key: FileTransferStore.TransferKey) {
        failed.insert(key)
    }

    // Called from chunkQueue. Updates byte counter and decides whether to
    // fire a progress event. Returns (totalReceived, shouldReportToMain).
    func addBytes(
        _ count: Int64,
        forKey key: FileTransferStore.TransferKey,
        totalSize: Int64,
        interval: TimeInterval
    ) -> (Int64, Bool) {
        let received = (bytesReceived[key] ?? 0) + count
        bytesReceived[key] = received

        let now      = Date()
        let lastAt   = lastProgressAt[key] ?? .distantPast
        let finished = totalSize > 0 && received >= totalSize
        let fire     = finished || now.timeIntervalSince(lastAt) >= interval
        if fire { lastProgressAt[key] = now }
        return (received, fire)
    }

    /// Forgets a transfer and reports how it went: the plaintext bytes that
    /// decrypted, and whether any chunk failed to.
    @discardableResult
    func remove(_ key: FileTransferStore.TransferKey) -> (received: Int64, failed: Bool) {
        lastProgressAt.removeValue(forKey: key)
        let received = bytesReceived.removeValue(forKey: key) ?? 0
        let didFail = failed.remove(key) != nil
        return (received, didFail)
    }
}
