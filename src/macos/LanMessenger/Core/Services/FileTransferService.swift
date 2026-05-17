import Foundation

// Handles outgoing file transfers (one at a time per peer) and incoming file reassembly.
// Outgoing: open a fresh TCP connection per file, send file_start / file_chunks / file_end.
// Incoming: receive chunks via the shared TCP listener, write to temp, finalize on file_end.

@MainActor
final class FileTransferService {

    static let shared = FileTransferService()

    var onProgress: ((String, String, Int64, Int64) -> Void)?   // peerIP, label, bytes, total
    var onComplete: ((String, String, URL?) -> Void)?            // peerIP, label, localURL (non-nil on sender side)
    var onIncomingFile: ((String, String, URL) -> Void)?        // peerIP, sender, finalURL

    private let chunkSize = 64 * 1024   // 64 KiB
    private let tcpPort = 54232
    // Serial queue: guarantees chunks are written in receive order even though
    // crypto runs off the main actor. handleFileEnd is also routed through this
    // queue so finalization always happens after the last chunk write.
    private let chunkQueue = DispatchQueue(label: "com.dave.lanmessenger.file-chunks", qos: .utility)

    private init() {}

    // MARK: - Receive (called from MessagingService / NetworkCoordinator)

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
        _ = FileTransferStore.shared.beginIncoming(
            transferId: pkt.transferId,
            filename: pkt.filename,
            size: pkt.size,
            senderIP: ip,
            senderPublicKeyB64: pkt.senderPublicKeyB64,
            inboxDir: inboxDir
        )
        onProgress?(ip, "Receiving \(PacketValidator.sanitizeFilename(pkt.filename))", 0, pkt.size)
    }

    private func handleFileChunk(_ pkt: FileChunkPacket, fromIP ip: String) {
        let key = FileTransferStore.TransferKey(ip: ip, transferId: pkt.transferId)
        guard let transfer = FileTransferStore.shared.incoming[key],
              let fileHandle = transfer.fileHandle else { return }

        // Capture everything needed before leaving the main actor.
        let nonce      = pkt.nonce
        let ciphertext = pkt.ciphertext
        let transferId = pkt.transferId
        let senderKey  = transfer.senderPublicKeyB64
        let filename   = transfer.filename
        let totalSize  = transfer.totalSize

        // Decrypt and write on the serial background queue so the main thread
        // stays free. The serial queue preserves TCP chunk ordering.
        chunkQueue.async {
            let aad = Data(transferId.utf8)
            guard let plaintext = try? SessionCrypto.decryptFromPeer(
                myPrivate: KeyManager.shared.privateKey,
                peerPublicKeyB64: senderKey,
                nonceB64: nonce,
                ciphertextB64: ciphertext,
                aad: aad
            ) else { return }

            fileHandle.write(plaintext)
            let count = Int64(plaintext.count)

            DispatchQueue.main.async { [weak self] in
                FileTransferStore.shared.addBytesReceived(count, forKey: key)
                let received = FileTransferStore.shared.incoming[key]?.bytesReceived ?? 0
                self?.onProgress?(ip, "Receiving \(filename)", received, totalSize)
            }
        }
    }

    private func handleFileEnd(_ pkt: FileEndPacket, fromIP ip: String) {
        let key = FileTransferStore.TransferKey(ip: ip, transferId: pkt.transferId)
        guard let transfer = FileTransferStore.shared.incoming[key] else { return }
        let filename = transfer.filename
        let sender   = pkt.sender

        // Route through chunkQueue so finalization runs only after the last
        // chunk write has completed (serial queue drains in order).
        chunkQueue.async { [weak self] in
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                guard let finalURL = FileTransferStore.shared.finalizeIncoming(
                    key: key,
                    inboxDir: ConfigStore.shared.inboxDirectory
                ) else { return }

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
        let path = item.path
        let filename = item.filename

        Task.detached(priority: .utility) { [weak self] in
            let success = await self?.sendFile(path: path, peerIP: peerIP, peerPublicKeyB64: peerPublicKeyB64, filename: filename) ?? false
            await MainActor.run {
                FileTransferStore.shared.markTransferFinished(peerIP: peerIP, success: success)
                self?.startNextIfIdle(peerIP: peerIP, peerPublicKeyB64: peerPublicKeyB64)
            }
        }
    }

    // Returns true on success. Runs off the main actor.
    nonisolated private func sendFile(path: String, peerIP: String, peerPublicKeyB64: String, filename: String) async -> Bool {
        let url = URL(fileURLWithPath: path)
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: path),
              let totalSize = attrs[.size] as? Int64,
              let handle = try? FileHandle(forReadingFrom: url) else { return false }
        defer { handle.closeFile() }

        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else { return false }
        defer { Darwin.close(fd) }

        var tv = timeval(tv_sec: 5, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        var addr = sockaddr_in()
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = UInt16(tcpPort).bigEndian
        addr.sin_addr.s_addr = inet_addr(peerIP)

        // Non-blocking connect with a 5-second timeout so a stale or unreachable
        // peer IP doesn't hang the task for the OS default (~75 s).
        let sockFlags = fcntl(fd, F_GETFL, 0)
        _ = fcntl(fd, F_SETFL, sockFlags | O_NONBLOCK)
        let connectResult = withUnsafePointer(to: &addr) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sp in
                Darwin.connect(fd, sp, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        if connectResult != 0 {
            guard errno == EINPROGRESS else { return false }
            var pfd = pollfd()
            pfd.fd = fd
            pfd.events = Int16(POLLOUT)
            guard Darwin.poll(&pfd, 1, 5000) > 0 else { return false }
            var sockErr: Int32 = 0
            var errLen = socklen_t(MemoryLayout<Int32>.size)
            getsockopt(fd, SOL_SOCKET, SO_ERROR, &sockErr, &errLen)
            guard sockErr == 0 else { return false }
        }
        _ = fcntl(fd, F_SETFL, sockFlags)  // restore blocking mode for send()

        let transferId = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let myKey = KeyManager.shared.publicKeyB64
        let myName = ConfigStore.shared.config.username

        // file_start
        let startPacket: [String: Any] = [
            "type": "file_start", "transfer_id": transferId,
            "filename": filename, "size": totalSize,
            "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
        ]
        guard let startFrame = try? FrameCodec.encodeDict(startPacket),
              rawSend(fd: fd, data: startFrame) else { return false }

        await MainActor.run { self.onProgress?(peerIP, "Sending \(filename)", 0, totalSize) }

        // Throttle progress callbacks so the UI doesn't thrash on large files —
        // updating ~10× per second is plenty for a smooth progress bar.
        var sent: Int64 = 0
        var lastReported: Int64 = 0
        var lastReportAt = Date()
        let minInterval: TimeInterval = 0.1
        let minBytes: Int64 = max(totalSize / 50, Int64(chunkSize) * 4)

        while true {
            let chunk = handle.readData(ofLength: chunkSize)
            if chunk.isEmpty { break }
            let aad = Data(transferId.utf8)
            guard let (nonceB64, ctB64) = try? SessionCrypto.encryptForPeer(
                myPrivate: KeyManager.shared.privateKey,
                peerPublicKeyB64: peerPublicKeyB64,
                plaintext: chunk,
                aad: aad
            ) else { return false }

            let chunkPacket: [String: Any] = [
                "type": "file_chunk", "transfer_id": transferId,
                "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
                "nonce": nonceB64, "ciphertext": ctB64,
            ]
            guard let chunkFrame = try? FrameCodec.encodeDict(chunkPacket),
                  rawSend(fd: fd, data: chunkFrame) else { return false }

            sent += Int64(chunk.count)
            let now = Date()
            if sent - lastReported >= minBytes || now.timeIntervalSince(lastReportAt) >= minInterval {
                let bytesSoFar = sent
                lastReported = sent
                lastReportAt = now
                await MainActor.run { self.onProgress?(peerIP, "Sending \(filename)", bytesSoFar, totalSize) }
            }
        }

        // file_end
        let endPacket: [String: Any] = [
            "type": "file_end", "transfer_id": transferId,
            "sender": myName, "sender_public_key_b64": myKey, "port": tcpPort,
        ]
        guard let endFrame = try? FrameCodec.encodeDict(endPacket),
              rawSend(fd: fd, data: endFrame) else { return false }

        await MainActor.run { self.onComplete?(peerIP, "Sending \(filename)", url) }
        return true
    }

    nonisolated private func rawSend(fd: Int32, data: Data) -> Bool {
        var sent = 0
        while sent < data.count {
            let n = data.withUnsafeBytes { ptr in Darwin.send(fd, ptr.baseAddress!.advanced(by: sent), data.count - sent, 0) }
            if n <= 0 { return false }
            sent += n
        }
        return true
    }
}
