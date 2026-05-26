import Foundation

// Tracks in-progress incoming file transfers.
// Each transfer is identified by (senderIP, transferId).

struct IncomingTransfer {
    let transferId: String
    let filename: String
    let totalSize: Int64
    let senderPublicKeyB64: String
    let tempURL: URL
    var fileHandle: FileHandle?
    var bytesReceived: Int64 = 0
}

final class FileTransferStore {

    static let shared = FileTransferStore()

    // Outgoing file queue per conversation (keyed by peer IP).
    private(set) var outgoingQueues: [String: [OutgoingFileItem]] = [:]
    private(set) var activeOutgoing: Set<String> = []   // peer IPs with an active transfer

    // Incoming transfers in progress: (senderIP, transferId) → state
    private(set) var incoming: [TransferKey: IncomingTransfer] = [:]

    private init() {}

    // MARK: - Incoming

    struct TransferKey: Hashable {
        let ip: String
        let transferId: String
    }

    func beginIncoming(
        transferId: String,
        filename: String,
        size: Int64,
        senderIP: String,
        senderPublicKeyB64: String,
        inboxDir: URL
    ) -> IncomingTransfer? {
        let safe = PacketValidator.sanitizeFilename(filename)
        let tempName = "\(transferId)_\(safe).part"
        let tempURL = inboxDir.appendingPathComponent(tempName)

        try? FileManager.default.createDirectory(at: inboxDir, withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: tempURL.path, contents: nil)
        guard let handle = try? FileHandle(forWritingTo: tempURL) else { return nil }

        let transfer = IncomingTransfer(
            transferId: transferId,
            filename: safe,
            totalSize: size,
            senderPublicKeyB64: senderPublicKeyB64,
            tempURL: tempURL,
            fileHandle: handle
        )
        incoming[TransferKey(ip: senderIP, transferId: transferId)] = transfer
        return transfer
    }

    func appendChunk(_ data: Data, forKey key: TransferKey) {
        guard var transfer = incoming[key] else { return }
        transfer.fileHandle?.write(data)
        transfer.bytesReceived += Int64(data.count)
        incoming[key] = transfer
    }

    // Called from the main actor with the coalesced byte count from ChunkQueueState.
    // Sets an absolute value (not an increment) so that throttled updates don't drift.
    func setBytesReceived(_ bytes: Int64, forKey key: TransferKey) {
        guard var transfer = incoming[key] else { return }
        transfer.bytesReceived = bytes
        incoming[key] = transfer
    }

    // Finalizes the transfer: closes the handle, moves temp to final name (deduped).
    // Returns the URL where the file now lives (final path on success, temp path
    // as a fallback when the move fails so the caller always gets a real file).
    func finalizeIncoming(key: TransferKey, inboxDir: URL) -> URL? {
        guard let transfer = incoming.removeValue(forKey: key) else { return nil }
        transfer.fileHandle?.closeFile()

        let finalURL = uniqueURL(inboxDir: inboxDir, filename: transfer.filename)
        do {
            try FileManager.default.moveItem(at: transfer.tempURL, to: finalURL)
            return finalURL
        } catch {
            // The received data is intact at the temp path. Return it so the
            // message bubble at least points to a real file instead of a
            // missing one. Log the failure so it can be diagnosed.
            NetLogger.error("FileTransfer",
                "finalizeIncoming: move '\(transfer.tempURL.lastPathComponent)'" +
                " → '\(finalURL.lastPathComponent)' failed: \(error.localizedDescription)")
            return transfer.tempURL
        }
    }

    func cancelIncoming(key: TransferKey) {
        guard var transfer = incoming.removeValue(forKey: key) else { return }
        transfer.fileHandle?.closeFile()
        try? FileManager.default.removeItem(at: transfer.tempURL)
    }

    // MARK: - Outgoing

    struct OutgoingFileItem {
        let path: String
        let filename: String
        let queuedAt: Date
    }

    func enqueue(path: String, filename: String, forPeerIP ip: String) {
        let item = OutgoingFileItem(path: path, filename: filename, queuedAt: Date())
        var queue = outgoingQueues[ip] ?? []
        queue.append(item)
        outgoingQueues[ip] = queue
    }

    func markTransferStarted(peerIP: String) {
        activeOutgoing.insert(peerIP)
    }

    func markTransferFinished(peerIP: String, success: Bool) {
        activeOutgoing.remove(peerIP)
        if success, var queue = outgoingQueues[peerIP], !queue.isEmpty {
            queue.removeFirst()
            outgoingQueues[peerIP] = queue.isEmpty ? nil : queue
        }
    }

    // MARK: - Helpers

    private func uniqueURL(inboxDir: URL, filename: String) -> URL {
        let base = inboxDir.appendingPathComponent(filename)
        if !FileManager.default.fileExists(atPath: base.path) { return base }
        let stem = base.deletingPathExtension().lastPathComponent
        let ext = base.pathExtension
        for i in 1...999 {
            let candidate = inboxDir.appendingPathComponent(ext.isEmpty ? "\(stem)_\(i)" : "\(stem)_\(i).\(ext)")
            if !FileManager.default.fileExists(atPath: candidate.path) { return candidate }
        }
        let fallback = stem + "_" + UUID().uuidString.prefix(8)
        return inboxDir.appendingPathComponent(ext.isEmpty ? fallback : "\(fallback).\(ext)")
    }
}
