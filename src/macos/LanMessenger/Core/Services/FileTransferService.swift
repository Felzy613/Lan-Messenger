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
//  • Progress/complete/error callbacks are always delivered on the main thread via
//    DispatchQueue.main.async — fire-and-forget so they never block the send loop.

@MainActor
final class FileTransferService {

    static let shared = FileTransferService()

    var onProgress:     ((String, String, Int64, Int64) -> Void)?   // peerIP, label, bytes, total
    var onComplete:     ((String, String, URL?) -> Void)?           // peerIP, label, localURL (non-nil on sender)
    var onError:        ((String, String) -> Void)?                 // peerIP, message
    var onIncomingFile: ((String, String, URL) -> Void)?            // peerIP, sender, finalURL

    private let chunkSize = 64 * 1024   // 64 KiB per chunk
    private let tcpPort   = 54232

    // Throttle for incoming progress callbacks. A 100 MB file is ~1600 chunks,
    // and pushing a @Published update per chunk floods the main thread and
    // freezes the UI mid-transfer. We coalesce updates to ~12 Hz per peer.
    private struct ProgressTicker {
        var lastReportAt: Date = .distantPast
        var lastReportedBytes: Int64 = 0
    }
    private var incomingTicker: [FileTransferStore.TransferKey: ProgressTicker] = [:]
    private let progressInterval: TimeInterval = 0.08

    // Serial queue: preserves TCP chunk ordering during decrypt + write.
    // handleFileEnd is routed through here too so finalization always
    // happens after the last chunk write completes.
    private let chunkQueue = DispatchQueue(label: "com.dave.lanmessenger.file-chunks", qos: .utility)

    // Dedicated queue for blocking outgoing I/O.  Blocking Darwin.send() here is
    // intentional and safe; it must never be called from the cooperative thread pool.
    private let sendQueue  = DispatchQueue(label: "com.dave.lanmessenger.file-send", qos: .userInitiated)

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

        guard FileTransferStore.shared.beginIncoming(
            transferId:         pkt.transferId,
            filename:           pkt.filename,
            size:               pkt.size,
            senderIP:           ip,
            senderPublicKeyB64: pkt.senderPublicKeyB64,
            inboxDir:           inboxDir
        ) != nil else {
            // Could not create temp file (permissions, disk full, etc.).
            onError?(ip, "Cannot save incoming file — check disk space and inbox permissions")
            return
        }
        onProgress?(ip, "Receiving \(safe)", 0, pkt.size)
    }

    private func handleFileChunk(_ pkt: FileChunkPacket, fromIP ip: String) {
        let key = FileTransferStore.TransferKey(ip: ip, transferId: pkt.transferId)
        guard let transfer   = FileTransferStore.shared.incoming[key],
              let fileHandle = transfer.fileHandle else { return }

        // Capture everything needed before leaving the main actor.
        let nonce      = pkt.nonce
        let ciphertext = pkt.ciphertext
        let transferId = pkt.transferId
        let senderKey  = transfer.senderPublicKeyB64
        let filename   = transfer.filename
        let totalSize  = transfer.totalSize

        // Decrypt and write on the serial background queue so the main thread stays
        // free.  The serial queue preserves TCP chunk ordering.
        chunkQueue.async { [weak self] in
            let aad = Data(transferId.utf8)
            guard let plaintext = try? SessionCrypto.decryptFromPeer(
                myPrivate:          KeyManager.shared.privateKey,
                peerPublicKeyB64:   senderKey,
                nonceB64:           nonce,
                ciphertextB64:      ciphertext,
                aad:                aad
            ) else { return }

            fileHandle.write(plaintext)
            let count = Int64(plaintext.count)

            // Always advance the persisted byte counter (cheap dictionary
            // update); only push a UI progress event when the throttle
            // window has elapsed. The final completion event is fired
            // unconditionally from handleFileEnd, so the bar never gets
            // stuck below 100%.
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                FileTransferStore.shared.addBytesReceived(count, forKey: key)
                let received = FileTransferStore.shared.incoming[key]?.bytesReceived ?? 0

                var ticker = self.incomingTicker[key] ?? ProgressTicker()
                let now = Date()
                let finished = received >= totalSize && totalSize > 0
                if finished || now.timeIntervalSince(ticker.lastReportAt) >= self.progressInterval {
                    ticker.lastReportAt = now
                    ticker.lastReportedBytes = received
                    self.incomingTicker[key] = ticker
                    self.onProgress?(ip, "Receiving \(filename)", received, totalSize)
                } else {
                    self.incomingTicker[key] = ticker
                }
            }
        }
    }

    private func handleFileEnd(_ pkt: FileEndPacket, fromIP ip: String) {
        let key = FileTransferStore.TransferKey(ip: ip, transferId: pkt.transferId)
        guard let transfer = FileTransferStore.shared.incoming[key] else { return }
        let filename = transfer.filename
        let sender   = pkt.sender

        // Route through chunkQueue so finalization runs only after the last
        // chunk write has completed (serial queue drains in arrival order).
        chunkQueue.async { [weak self] in
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                guard let finalURL = FileTransferStore.shared.finalizeIncoming(
                    key:      key,
                    inboxDir: ConfigStore.shared.inboxDirectory
                ) else { return }
                self.incomingTicker.removeValue(forKey: key)
                self.onComplete?(ip, "Receiving \(filename)", nil)
                self.onIncomingFile?(ip, sender, finalURL)
            }
        }
    }

    // MARK: - Send

    func enqueue(filePath: String, toPeerIP ip: String, peerPublicKeyB64: String) {
        let url = URL(fileURLWithPath: filePath)
        FileTransferStore.shared.enqueue(path: filePath, filename: url.lastPathComponent, forPeerIP: ip)
        startNextIfIdle(peerIP: ip, peerPublicKeyB64: peerPublicKeyB64)
    }

    // Re-trigger the queue for a peer that has just come back online — covers
    // the case where a previous attempt failed and the file is still queued.
    func retryQueue(toPeerIP ip: String, peerPublicKeyB64: String) {
        startNextIfIdle(peerIP: ip, peerPublicKeyB64: peerPublicKeyB64)
    }

    private func startNextIfIdle(peerIP: String, peerPublicKeyB64: String) {
        guard !FileTransferStore.shared.activeOutgoing.contains(peerIP),
              let item = FileTransferStore.shared.outgoingQueues[peerIP]?.first else { return }

        FileTransferStore.shared.markTransferStarted(peerIP: peerIP)
        let path     = item.path
        let filename = item.filename

        // Dispatch blocking I/O to sendQueue so Swift's cooperative thread pool stays
        // free for other async work.  Progress and completion callbacks are delivered
        // back to main via DispatchQueue.main.async (fire-and-forget — never suspends
        // the send loop).
        sendQueue.async { [weak self] in
            let success = Self.sendFileBlocking(
                path:             path,
                peerIP:           peerIP,
                peerPublicKeyB64: peerPublicKeyB64,
                filename:         filename,
                chunkSize:        65536,
                tcpPort:          54232,
                onProgress: { bytes, total in
                    DispatchQueue.main.async { [weak self] in
                        self?.onProgress?(peerIP, "Sending \(filename)", bytes, total)
                    }
                },
                onComplete: { url in
                    DispatchQueue.main.async { [weak self] in
                        self?.onComplete?(peerIP, "Sending \(filename)", url)
                    }
                }
            )

            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                FileTransferStore.shared.markTransferFinished(peerIP: peerIP, success: success)
                if success {
                    // Advance to the next queued file for this peer.
                    self.startNextIfIdle(peerIP: peerIP, peerPublicKeyB64: peerPublicKeyB64)
                } else {
                    // Do NOT retry immediately — the failed item stays in the queue
                    // and will be retried when retryQueue() is called (peer reconnect).
                    self.onError?(peerIP, "Failed to send \(filename) — will retry when peer reconnects")
                }
            }
        }
    }

    // Blocking send — must only be called from sendQueue, never from the main actor.
    // Returns true on success; false on any I/O, connection, or crypto error.
    // `nonisolated` removes the implicit @MainActor inheritance so the method can be
    // called safely from the background sendQueue without a concurrency warning.
    nonisolated private static func sendFileBlocking(
        path:             String,
        peerIP:           String,
        peerPublicKeyB64: String,
        filename:         String,
        chunkSize:        Int,
        tcpPort:          Int,
        onProgress:       @escaping (Int64, Int64) -> Void,
        onComplete:       @escaping (URL) -> Void
    ) -> Bool {
        let url = URL(fileURLWithPath: path)
        guard let attrs     = try? FileManager.default.attributesOfItem(atPath: path),
              let totalSize = attrs[.size] as? Int64,
              totalSize     >= 0,
              let handle    = try? FileHandle(forReadingFrom: url) else { return false }
        defer { handle.closeFile() }

        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else { return false }
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
            guard errno == EINPROGRESS else { return false }
            var pfd = pollfd()
            pfd.fd     = fd
            pfd.events = Int16(POLLOUT)
            guard Darwin.poll(&pfd, 1, 10_000) > 0 else { return false }
            var sockErr: Int32 = 0
            var errLen = socklen_t(MemoryLayout<Int32>.size)
            getsockopt(fd, SOL_SOCKET, SO_ERROR, &sockErr, &errLen)
            guard sockErr == 0 else { return false }
        }
        _ = fcntl(fd, F_SETFL, sockFlags)  // restore blocking mode for send()

        let transferId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let myKey      = KeyManager.shared.publicKeyB64
        let myName     = ConfigStore.shared.config.username

        // ── file_start ────────────────────────────────────────────────────────────
        let startPkt: [String: Any] = [
            "type": "file_start", "transfer_id": transferId,
            "filename": filename, "size": totalSize,
            "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
        ]
        guard let startFrame = try? FrameCodec.encodeDict(startPkt),
              rawSend(fd: fd, data: startFrame) else { return false }

        onProgress(0, totalSize)

        // ── file_chunks ───────────────────────────────────────────────────────────
        // Throttle progress updates to ~12 Hz. The previous logic OR'd a
        // byte threshold (== chunkSize) with the time threshold, so every
        // chunk passed it — for a 100 MB file that's ~1600 main-thread
        // hops that lock up the UI mid-transfer.
        var sent: Int64           = 0
        var lastReportAt          = Date.distantPast
        let minInterval: TimeInterval = 0.08

        while true {
            let chunk = handle.readData(ofLength: chunkSize)
            if chunk.isEmpty { break }

            let aad = Data(transferId.utf8)
            guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
                myPrivate:        KeyManager.shared.privateKey,
                peerPublicKeyB64: peerPublicKeyB64,
                plaintext:        chunk,
                aad:              aad
            ) else { return false }

            let chunkPkt: [String: Any] = [
                "type": "file_chunk", "transfer_id": transferId,
                "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
                "nonce": nonceB64, "ciphertext": ctB64,
            ]
            guard let chunkFrame = try? FrameCodec.encodeDict(chunkPkt),
                  rawSend(fd: fd, data: chunkFrame) else { return false }

            sent += Int64(chunk.count)
            let now = Date()
            if now.timeIntervalSince(lastReportAt) >= minInterval {
                lastReportAt = now
                onProgress(sent, totalSize)
            }
        }

        // ── file_end ──────────────────────────────────────────────────────────────
        let endPkt: [String: Any] = [
            "type": "file_end", "transfer_id": transferId,
            "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
        ]
        guard let endFrame = try? FrameCodec.encodeDict(endPkt),
              rawSend(fd: fd, data: endFrame) else { return false }

        onProgress(totalSize, totalSize)
        onComplete(url)
        return true
    }

    // Blocking byte-exact send loop.  Returns false if the socket errors or times out.
    nonisolated private static func rawSend(fd: Int32, data: Data) -> Bool {
        var offset = 0
        while offset < data.count {
            let n = data.withUnsafeBytes { ptr in
                Darwin.send(fd, ptr.baseAddress!.advanced(by: offset), data.count - offset, 0)
            }
            if n <= 0 { return false }
            offset += n
        }
        return true
    }
}
