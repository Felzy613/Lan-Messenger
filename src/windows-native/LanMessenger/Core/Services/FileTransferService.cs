using LanMessenger.Core.Crypto;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using Microsoft.UI.Dispatching;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace LanMessenger.Core.Services;

// Handles outgoing file transfers (queued per peer) and incoming file reassembly.
// Outgoing: fresh TCP connection per file; file_start / file_chunks / file_end.
// Incoming: receives chunks via the shared TCP listener, writes to temp, finalizes on file_end.
//
// Threading model
// ───────────────
//  • HandleFileStart / HandleFileChunk / HandleFileEnd are called on the UI thread
//    (via DispatcherQueue) — all dictionary bookkeeping happens there.
//  • Each incoming transfer owns an unbounded Channel<Func<Task>>.  Chunk work
//    (decrypt + WriteAsync) is written to the channel from the UI thread and consumed
//    by a single background Task, keeping the UI free while preserving TCP ordering.
//  • The finalization work (FinalizeIncoming) is also sent through the same channel
//    so it always runs after the last chunk write completes.
//  • Outgoing I/O runs entirely on a background Task (Task.Run) and never touches
//    the UI thread except through Dispatch().
public sealed class FileTransferService
{
    public static FileTransferService Shared { get; } = new();

    public Action<string, string, long, long>?  OnProgress     { get; set; }  // peerIP, label, bytes, total
    public Action<string, string, string?>?     OnComplete     { get; set; }  // peerIP, label, localPath (sender only)
    public Action<string, string>?              OnError        { get; set; }  // peerIP, message
    public Action<string, string, string>?      OnIncomingFile { get; set; }  // peerIP, sender, finalPath

    private DispatcherQueue? _dq;
    private const int ChunkSize = 64 * 1024; // 64 KiB
    private const int TcpPort   = 54232;

    // Per-transfer channel for ordered background chunk processing.
    // Accessed only from the UI thread (HandleFileStart/Chunk/End).
    private readonly Dictionary<TransferKey, Channel<Func<Task>>> _transferChannels = [];

    // Throttle for incoming progress UI events — 12 Hz. Without this, a 100 MB
    // file (~1600 chunks) would post 1600 work items to the UI thread and
    // freeze the chat window mid-transfer.
    private readonly Dictionary<TransferKey, DateTime> _lastIncomingReportAt = [];
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(80);

    // Wall-clock start time per active incoming transfer.  Used to compute
    // duration_ms and bytes_per_sec for the "complete" structured log event.
    private readonly Dictionary<TransferKey, DateTime> _incomingStartTimes = [];

    private FileTransferService() { }

    public void SetDispatcherQueue(DispatcherQueue dq) => _dq = dq;

    // MARK: - Receive (called from NetworkCoordinator on UI thread)

    public void HandlePacket(ValidatedPacket packet)
    {
        switch (packet)
        {
            case ValidatedFileStart fs: HandleFileStart(fs.Packet, fs.SenderIP); break;
            case ValidatedFileChunk fc: HandleFileChunk(fc.Packet, fc.SenderIP); break;
            case ValidatedFileEnd   fe: HandleFileEnd(fe.Packet,   fe.SenderIP); break;
        }
    }

    private void HandleFileStart(FileStartPacket pkt, string ip)
    {
        var key  = new TransferKey(ip, pkt.TransferId);
        var safe = PacketValidator.SanitizeFilename(pkt.Filename);

        LanLogger.FileTransfer(
            "start", transferId: pkt.TransferId, peer: ip,
            direction: "incoming", filename: safe, size: pkt.Size,
            mime: MimeFromFilename(safe));

        var transfer = FileTransferStore.Shared.BeginIncoming(
            pkt.TransferId, pkt.Filename, pkt.Size, ip, pkt.SenderPublicKeyB64,
            ConfigStore.Shared.InboxDirectory);

        if (transfer is null)
        {
            LanLogger.FileTransfer(
                "failed", transferId: pkt.TransferId, peer: ip,
                direction: "incoming", filename: safe, size: pkt.Size,
                reason: "cannot create temp file — disk full or permission denied");
            Dispatch(() => OnError?.Invoke(ip, "Cannot save incoming file — check disk space and inbox permissions"));
            return;
        }

        _incomingStartTimes[key] = DateTime.UtcNow;

        // Create an unbounded channel (single reader) for ordered chunk processing.
        var ch = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
        _transferChannels[key] = ch;

        // Spin up one background Task that drains this channel in order.
        _ = Task.Run(() => DrainChannelAsync(ch.Reader));

        Dispatch(() => OnProgress?.Invoke(ip, $"Receiving {safe}", 0, pkt.Size));
    }

    private void HandleFileChunk(FileChunkPacket pkt, string ip)
    {
        var key = new TransferKey(ip, pkt.TransferId);
        if (!FileTransferStore.Shared.Incoming.TryGetValue(key, out var transfer)) return;
        if (!_transferChannels.TryGetValue(key, out var ch)) return;

        // Snapshot all packet data before yielding to the background channel —
        // the packet object may be reused after this call returns.
        var nonce      = pkt.Nonce;
        var ciphertext = pkt.Ciphertext;
        var transferId = pkt.TransferId;
        var senderKey  = transfer.SenderPublicKeyB64;
        var filename   = transfer.Filename;
        var totalSize  = transfer.TotalSize;

        // Enqueue work onto the channel.  The background consumer decrypts and writes;
        // the UI thread returns immediately and stays responsive.
        ch.Writer.TryWrite(async () =>
        {
            var aad = Encoding.UTF8.GetBytes(transferId);
            byte[] plaintext;
            try
            {
                plaintext = SessionCrypto.DecryptFromPeer(
                    KeyManager.Shared.PrivateKey, senderKey, nonce, ciphertext, aad);
            }
            catch (Exception ex)
            {
                LanLogger.FileTransfer(
                    "failed", transferId: transferId, peer: ip,
                    direction: "incoming", filename: filename,
                    reason: $"chunk decrypt failed: {ex.GetType().Name}");
                return;
            }

            FileTransferStore.Shared.AppendChunk(plaintext, key);
            var received = FileTransferStore.Shared.Incoming.TryGetValue(key, out var t2)
                           ? t2.BytesReceived : 0;

            // Coalesce progress updates to ~12 Hz on the UI thread. The
            // completion event in HandleFileEnd fires unconditionally so the
            // bar always reaches 100%.
            Dispatch(() =>
            {
                var now = DateTime.UtcNow;
                var due = !_lastIncomingReportAt.TryGetValue(key, out var last) ||
                          (now - last) >= ProgressInterval ||
                          (totalSize > 0 && received >= totalSize);
                if (!due) return;
                _lastIncomingReportAt[key] = now;
                OnProgress?.Invoke(ip, $"Receiving {filename}", received, totalSize);
            });
        });
    }

    private void HandleFileEnd(FileEndPacket pkt, string ip)
    {
        var key = new TransferKey(ip, pkt.TransferId);
        if (!FileTransferStore.Shared.Incoming.TryGetValue(key, out var transfer)) return;
        if (!_transferChannels.TryGetValue(key, out var ch)) return;

        // Remove the channel entry now (on UI thread) before handing off finalization.
        _transferChannels.Remove(key);

        var filename = transfer.Filename;
        var sender   = pkt.Sender;

        // Enqueue the finalization work — the channel consumer guarantees it only
        // runs after every preceding chunk write has completed.
        ch.Writer.TryWrite(async () =>
        {
            var size = transfer.TotalSize;
            var finalPath = FileTransferStore.Shared.FinalizeIncoming(
                key, ConfigStore.Shared.InboxDirectory);
            if (finalPath is null)
            {
                LanLogger.FileTransfer(
                    "failed", transferId: pkt.TransferId, peer: ip,
                    direction: "incoming", filename: filename, size: size,
                    reason: "finalize failed (missing transfer record)");
                return;
            }

            Dispatch(() =>
            {
                _lastIncomingReportAt.Remove(key);
                int? durationMs = null;
                double? bps = null;
                if (_incomingStartTimes.TryGetValue(key, out var startedAt))
                {
                    _incomingStartTimes.Remove(key);
                    durationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                    if (durationMs > 0 && size > 0)
                        bps = (double)size * 1000.0 / durationMs.Value;
                }
                LanLogger.FileTransfer(
                    "complete", transferId: pkt.TransferId, peer: ip,
                    direction: "incoming", filename: filename, size: size,
                    mime: MimeFromFilename(filename),
                    durationMs: durationMs, bytesPerSec: bps);
                OnComplete?.Invoke(ip, $"Receiving {filename}", null);
                OnIncomingFile?.Invoke(ip, sender, finalPath);
            });
            await Task.CompletedTask; // satisfies Func<Task> signature
        });

        // Signal the consumer that no more items will arrive for this transfer.
        ch.Writer.Complete();
    }

    // Background channel consumer — single reader, processes items in FIFO order.
    private static async Task DrainChannelAsync(ChannelReader<Func<Task>> reader)
    {
        await foreach (var work in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try   { await work().ConfigureAwait(false); }
            catch { /* drop bad chunk, transfer continues */ }
        }
    }

    // MARK: - Send

    public void Enqueue(string filePath, string peerIP, string peerPublicKeyB64)
    {
        var name = Path.GetFileName(filePath);
        long? size = null;
        try { size = new FileInfo(filePath).Length; } catch { /* file may have been deleted */ }
        LanLogger.FileTransfer(
            "queued", peer: peerIP, direction: "outgoing",
            filename: name, size: size, mime: MimeFromFilename(name));
        FileTransferStore.Shared.Enqueue(filePath, name, peerIP);
        StartNextIfIdle(peerIP, peerPublicKeyB64);
    }

    // Re-trigger the queue for a peer that has just come back online — covers
    // the case where a previous attempt failed and the file is still queued.
    public void RetryQueue(string peerIP, string peerPublicKeyB64) =>
        StartNextIfIdle(peerIP, peerPublicKeyB64);

    private void StartNextIfIdle(string peerIP, string peerPublicKeyB64)
    {
        if (FileTransferStore.Shared.ActiveOutgoing.Contains(peerIP)) return;
        if (!FileTransferStore.Shared.OutgoingQueues.TryGetValue(peerIP, out var q) || q.Count == 0) return;
        var item = q.Peek();

        FileTransferStore.Shared.MarkTransferStarted(peerIP);
        long? outgoingSize = null;
        try { outgoingSize = new FileInfo(item.Path).Length; } catch { /* deleted between enqueue and send */ }
        LanLogger.FileTransfer(
            "start", peer: peerIP, direction: "outgoing",
            filename: item.Filename, size: outgoingSize,
            mime: MimeFromFilename(item.Filename));

        var startedAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            var success = await SendFileAsync(item.Path, peerIP, peerPublicKeyB64, item.Filename)
                               .ConfigureAwait(false);
            Dispatch(() =>
            {
                FileTransferStore.Shared.MarkTransferFinished(peerIP, success);
                var durationMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
                double? bps = (durationMs > 0 && outgoingSize > 0)
                    ? (double)outgoingSize.Value * 1000.0 / durationMs
                    : null;
                if (success)
                {
                    LanLogger.FileTransfer(
                        "complete", peer: peerIP, direction: "outgoing",
                        filename: item.Filename, size: outgoingSize,
                        mime: MimeFromFilename(item.Filename),
                        bytesSent: outgoingSize,
                        durationMs: durationMs, bytesPerSec: bps);
                    // Advance to the next queued file.
                    StartNextIfIdle(peerIP, peerPublicKeyB64);
                }
                else
                {
                    LanLogger.FileTransfer(
                        "failed", peer: peerIP, direction: "outgoing",
                        filename: item.Filename, size: outgoingSize,
                        durationMs: durationMs,
                        reason: "will retry on reconnect");
                    // Do NOT retry immediately — the item stays queued and will be
                    // retried when RetryQueue() is called (e.g., on peer reconnect).
                    OnError?.Invoke(peerIP, $"Failed to send {item.Filename} — will retry when peer reconnects");
                }
            });
        });
    }

    // Lightweight MIME inference for log enrichment.  Not exhaustive — only
    // returns the common categories the support workflow cares about so the
    // log line stays readable.
    internal static string? MimeFromFilename(string filename)
    {
        var dot = filename.LastIndexOf('.');
        if (dot < 0 || dot == filename.Length - 1) return null;
        return filename.Substring(dot + 1).ToLowerInvariant() switch
        {
            "png"  => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif"  => "image/gif",
            "heic" => "image/heic",
            "webp" => "image/webp",
            "mp4"  => "video/mp4",
            "mov"  => "video/quicktime",
            "mkv"  => "video/x-matroska",
            "webm" => "video/webm",
            "pdf"  => "application/pdf",
            "zip"  => "application/zip",
            "txt"  => "text/plain",
            "md"   => "text/markdown",
            "json" => "application/json",
            "csv"  => "text/csv",
            "doc" or "docx" => "application/msword",
            "xls" or "xlsx" => "application/vnd.ms-excel",
            "ppt" or "pptx" => "application/vnd.ms-powerpoint",
            "mp3"  => "audio/mpeg",
            "wav"  => "audio/wav",
            "m4a"  => "audio/mp4",
            _ => null,
        };
    }

    private async Task<bool> SendFileAsync(
        string path, string peerIP, string peerPublicKeyB64, string filename)
    {
        if (!File.Exists(path))
        {
            LanLogger.FileTransfer(
                "failed", peer: peerIP, direction: "outgoing",
                filename: filename, reason: "source file missing");
            return false;
        }
        var totalSize = new FileInfo(path).Length;

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(peerIP, TcpPort)
                     .WaitAsync(TimeSpan.FromSeconds(10))
                     .ConfigureAwait(false);

            // Disable Nagle's algorithm — reduces latency on the final small frame.
            tcp.NoDelay = true;

            var stream     = tcp.GetStream();
            var transferId = Guid.NewGuid().ToString("N").ToLowerInvariant();
            var myKey      = KeyManager.Shared.PublicKeyB64;
            var myName     = ConfigStore.Shared.Config.Username;

            // ── file_start ────────────────────────────────────────────────────────
            var startPacket = new Dictionary<string, object?>
            {
                ["type"] = "file_start", ["transfer_id"] = transferId,
                ["filename"] = filename, ["size"] = totalSize,
                ["sender"] = myName, ["sender_public_key_b64"] = myKey, ["port"] = TcpPort,
            };
            await stream.WriteAsync(FrameCodec.EncodeDict(startPacket)).ConfigureAwait(false);
            Dispatch(() => OnProgress?.Invoke(peerIP, $"Sending {filename}", 0, totalSize));

            // ── file_chunks ───────────────────────────────────────────────────────
            // Throttle progress updates to ~12 Hz. The earlier code OR'd a
            // byte-threshold check (== ChunkSize) with the time check, so the
            // throttle never engaged: every chunk hopped to the UI thread and
            // froze it on large files.
            using var handle     = File.OpenRead(path);
            var  buf             = new byte[ChunkSize];
            long sent            = 0;
            var  lastReportAt    = DateTime.MinValue;
            var  minInterval     = TimeSpan.FromMilliseconds(80);
            int  read;

            while ((read = await handle.ReadAsync(buf.AsMemory(0, ChunkSize)).ConfigureAwait(false)) > 0)
            {
                var chunk = buf[..read];
                var aad   = Encoding.UTF8.GetBytes(transferId);
                var (nonceB64, ctB64) = SessionCrypto.EncryptForPeer(
                    KeyManager.Shared.PrivateKey, peerPublicKeyB64, chunk, aad);

                var chunkPacket = new Dictionary<string, object?>
                {
                    ["type"] = "file_chunk", ["transfer_id"] = transferId,
                    ["sender"] = myName, ["sender_public_key_b64"] = myKey, ["port"] = TcpPort,
                    ["nonce"] = nonceB64, ["ciphertext"] = ctB64,
                };
                await stream.WriteAsync(FrameCodec.EncodeDict(chunkPacket)).ConfigureAwait(false);

                sent += read;
                var now = DateTime.UtcNow;
                if ((now - lastReportAt) >= minInterval)
                {
                    var snap = sent;
                    lastReportAt = now;
                    Dispatch(() => OnProgress?.Invoke(peerIP, $"Sending {filename}", snap, totalSize));
                }
            }

            // ── file_end ──────────────────────────────────────────────────────────
            var endPacket = new Dictionary<string, object?>
            {
                ["type"] = "file_end", ["transfer_id"] = transferId,
                ["sender"] = myName, ["sender_public_key_b64"] = myKey, ["port"] = TcpPort,
            };
            await stream.WriteAsync(FrameCodec.EncodeDict(endPacket)).ConfigureAwait(false);

            Dispatch(() =>
            {
                OnProgress?.Invoke(peerIP, $"Sending {filename}", totalSize, totalSize);
                OnComplete?.Invoke(peerIP, $"Sending {filename}", path);
            });
            return true;
        }
        catch (Exception ex)
        {
            LanLogger.FileTransfer(
                "failed", peer: peerIP, direction: "outgoing",
                filename: filename, size: totalSize,
                reason: $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void Dispatch(Action action) => _dq?.TryEnqueue(() => action());
}
