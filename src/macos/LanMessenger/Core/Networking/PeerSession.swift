import Foundation

// One persistent TCP connection to a single peer.
// Reconnects automatically with exponential back-off (500 ms → 2 s → 5 s).
// All received validated packets are delivered via the onPacket callback on the main queue.
// Outgoing packets are queued and sent serially.

final class PeerSession {

    let peerIP: String
    let peerPort: Int
    var onPacket: ((ValidatedPacket) -> Void)?
    var onDisconnect: ((PeerSession) -> Void)?

    var ownPublicKeyB64: String = ""

    private var socket: CFSocket?
    private var inputStream: InputStream?
    private var outputStream: OutputStream?
    private var sendQueue: [Data] = []
    private var connected = false
    private var stopped = false
    private let sessionQueue = DispatchQueue(label: "com.dave.lanmessenger.peer.\(UUID().uuidString)", qos: .utility)
    private let backoffDelays: [TimeInterval] = [0.5, 2.0, 5.0]
    private var backoffIndex = 0

    init(ip: String, port: Int) {
        self.peerIP = ip
        self.peerPort = port
    }

    func start() {
        sessionQueue.async { [weak self] in self?.connect() }
    }

    func stop() {
        stopped = true
        sessionQueue.async { [weak self] in self?.teardown() }
    }

    // Enqueue a packet frame for sending. Thread-safe.
    func send(_ data: Data) {
        sessionQueue.async { [weak self] in
            self?.sendQueue.append(data)
            self?.drainSendQueue()
        }
    }

    // MARK: - Connection lifecycle

    private func connect() {
        guard !stopped else { return }
        teardown()
        let attemptStartedAt = Date()
        NetLogger.peer(event: "connect", peer: peerIP)

        var readStream: Unmanaged<CFReadStream>?
        var writeStream: Unmanaged<CFWriteStream>?
        CFStreamCreatePairWithSocketToHost(
            nil,
            peerIP as CFString,
            UInt32(peerPort),
            &readStream,
            &writeStream
        )

        guard let rs = readStream?.takeRetainedValue(),
              let ws = writeStream?.takeRetainedValue() else {
            scheduleReconnect()
            return
        }

        let input = rs as InputStream
        let output = ws as OutputStream
        input.open()
        output.open()

        // Give streams up to 5 s to connect
        let deadline = Date().addingTimeInterval(5)
        while input.streamStatus == .opening || output.streamStatus == .opening {
            if Date() > deadline { teardown(); scheduleReconnect(); return }
            Thread.sleep(forTimeInterval: 0.05)
        }
        guard input.streamStatus == .open, output.streamStatus == .open else {
            NetLogger.peer(
                event: "connect_fail", peer: peerIP,
                durationMs: Int(Date().timeIntervalSince(attemptStartedAt) * 1000),
                reason: "stream did not reach open state"
            )
            teardown(); scheduleReconnect(); return
        }

        self.inputStream = input
        self.outputStream = output
        connected = true
        backoffIndex = 0
        NetLogger.peer(
            event: "connected", peer: peerIP,
            durationMs: Int(Date().timeIntervalSince(attemptStartedAt) * 1000)
        )

        drainSendQueue()
        receiveLoop(input: input)
    }

    private func receiveLoop(input: InputStream) {
        while !stopped, input.streamStatus == .open {
            do {
                guard let frameData = try readFrame(from: input) else { break }
                guard let json = try? JSONSerialization.jsonObject(with: frameData) as? [String: Any] else { continue }
                let result = PacketValidator.validate(json: json, senderIP: peerIP, ownPublicKeyB64: ownPublicKeyB64)
                switch result {
                case .success(let pkt):
                    DispatchQueue.main.async { [weak self] in self?.onPacket?(pkt) }
                case .failure:
                    break   // silently drop
                }
            } catch {
                break
            }
        }
        connected = false
        NetLogger.peer(event: "disconnect", peer: peerIP, reason: stopped ? "stopped" : "stream closed")
        DispatchQueue.main.async { [weak self] in guard let self else { return }; self.onDisconnect?(self) }
        teardown()
        if !stopped { scheduleReconnect() }
    }

    private func drainSendQueue() {
        guard connected, let output = outputStream else { return }
        while !sendQueue.isEmpty {
            let data = sendQueue[0]
            var written = 0
            while written < data.count {
                let n = data.withUnsafeBytes { ptr in
                    output.write(ptr.baseAddress!.advanced(by: written).assumingMemoryBound(to: UInt8.self),
                                 maxLength: data.count - written)
                }
                if n <= 0 { return }
                written += n
            }
            sendQueue.removeFirst()
        }
    }

    private func scheduleReconnect() {
        guard !stopped else { return }
        let delay = backoffDelays[min(backoffIndex, backoffDelays.count - 1)]
        backoffIndex += 1
        sessionQueue.asyncAfter(deadline: .now() + delay) { [weak self] in self?.connect() }
    }

    private func teardown() {
        inputStream?.close(); inputStream = nil
        outputStream?.close(); outputStream = nil
        connected = false
    }

    // MARK: - Frame reader

    private func readFrame(from stream: InputStream) throws -> Data? {
        var header = [UInt8](repeating: 0, count: 4)
        guard tryReadExact(stream: stream, buffer: &header, count: 4) else { return nil }
        let length = Int(UInt32(bigEndian: header.withUnsafeBytes { $0.load(as: UInt32.self) }))
        guard length > 0, length <= FrameCodec.maxFrameSize else {
            throw FrameCodecError.frameTooLarge(length)
        }
        var body = [UInt8](repeating: 0, count: length)
        guard tryReadExact(stream: stream, buffer: &body, count: length) else { return nil }
        return Data(body)
    }

    private func tryReadExact(stream: InputStream, buffer: inout [UInt8], count: Int) -> Bool {
        var total = 0
        while total < count {
            let n = buffer.withUnsafeMutableBytes { ptr in
                stream.read(ptr.baseAddress!.advanced(by: total).assumingMemoryBound(to: UInt8.self),
                            maxLength: count - total)
            }
            if n <= 0 { return false }
            total += n
        }
        return true
    }
}
