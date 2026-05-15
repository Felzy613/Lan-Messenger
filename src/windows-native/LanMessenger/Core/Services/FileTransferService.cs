using LanMessenger.Core.Crypto;
using LanMessenger.Core.Persistence;
using LanMessenger.Core.Protocol;
using Microsoft.UI.Dispatching;
using System.Net.Sockets;
using System.Text;

namespace LanMessenger.Core.Services;

// Handles outgoing file transfers (queued per peer) and incoming file reassembly.
// Outgoing: fresh TCP connection per file; file_start / file_chunks / file_end.
// Incoming: receives chunks via the shared TCP listener, writes to temp, finalizes on file_end.
public sealed class FileTransferService
{
    public static FileTransferService Shared { get; } = new();

    public Action<string, string, long, long>?   OnProgress     { get; set; }  // peerIP, label, bytes, total
    public Action<string, string, string?>?      OnComplete     { get; set; }  // peerIP, label, localPath (sender side only)
    public Action<string, string, string>?       OnIncomingFile { get; set; }  // peerIP, sender, finalPath

    private DispatcherQueue? _dq;
    private const int ChunkSize = 64 * 1024; // 64 KiB
    private const int TcpPort   = 54232;

    private FileTransferService() { }

    public void SetDispatcherQueue(DispatcherQueue dq) => _dq = dq;

    // MARK: - Receive (called from NetworkCoordinator dispatch)

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
        var safe = PacketValidator.SanitizeFilename(pkt.Filename);
        FileTransferStore.Shared.BeginIncoming(
            pkt.TransferId, pkt.Filename, pkt.Size, ip, pkt.SenderPublicKeyB64,
            ConfigStore.Shared.InboxDirectory);
        Dispatch(() => OnProgress?.Invoke(ip, $"Receiving {safe}", 0, pkt.Size));
    }

    private void HandleFileChunk(FileChunkPacket pkt, string ip)
    {
        var key = new TransferKey(ip, pkt.TransferId);
        if (!FileTransferStore.Shared.Incoming.TryGetValue(key, out var transfer)) return;

        var aad = Encoding.UTF8.GetBytes(pkt.TransferId);
        byte[] plaintext;
        try { plaintext = SessionCrypto.DecryptFromPeer(KeyManager.Shared.PrivateKey, transfer.SenderPublicKeyB64, pkt.Nonce, pkt.Ciphertext, aad); }
        catch { return; }

        FileTransferStore.Shared.AppendChunk(plaintext, key);
        var received = FileTransferStore.Shared.Incoming.TryGetValue(key, out var t2) ? t2.BytesReceived : 0;
        Dispatch(() => OnProgress?.Invoke(ip, $"Receiving {transfer.Filename}", received, transfer.TotalSize));
    }

    private void HandleFileEnd(FileEndPacket pkt, string ip)
    {
        var key = new TransferKey(ip, pkt.TransferId);
        if (!FileTransferStore.Shared.Incoming.TryGetValue(key, out var transfer)) return;
        var filename = transfer.Filename;
        var sender   = pkt.Sender;

        var finalPath = FileTransferStore.Shared.FinalizeIncoming(key, ConfigStore.Shared.InboxDirectory);
        if (finalPath is null) return;

        Dispatch(() =>
        {
            OnComplete?.Invoke(ip, $"Receiving {filename}", null);  // receiver — no outgoing bubble
            OnIncomingFile?.Invoke(ip, sender, finalPath);
        });
    }

    // MARK: - Send

    public void Enqueue(string filePath, string peerIP, string peerPublicKeyB64)
    {
        FileTransferStore.Shared.Enqueue(filePath, Path.GetFileName(filePath), peerIP);
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
        Task.Run(async () =>
        {
            var success = await SendFileAsync(item.Path, peerIP, peerPublicKeyB64, item.Filename);
            Dispatch(() =>
            {
                FileTransferStore.Shared.MarkTransferFinished(peerIP, success);
                StartNextIfIdle(peerIP, peerPublicKeyB64);
            });
        });
    }

    private async Task<bool> SendFileAsync(string path, string peerIP, string peerPublicKeyB64, string filename)
    {
        if (!File.Exists(path)) return false;
        var totalSize = new FileInfo(path).Length;

        try
        {
            using var tcp    = new TcpClient();
            await tcp.ConnectAsync(peerIP, TcpPort).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var transferId = Guid.NewGuid().ToString("N").ToLowerInvariant();
            var myKey      = KeyManager.Shared.PublicKeyB64;
            var myName     = ConfigStore.Shared.Config.Username;

            // file_start
            var startPacket = new Dictionary<string, object?>
            {
                ["type"] = "file_start", ["transfer_id"] = transferId,
                ["filename"] = filename, ["size"] = totalSize,
                ["sender"] = myName, ["sender_public_key_b64"] = myKey, ["port"] = TcpPort,
            };
            await stream.WriteAsync(FrameCodec.EncodeDict(startPacket)).ConfigureAwait(false);
            Dispatch(() => OnProgress?.Invoke(peerIP, $"Sending {filename}", 0, totalSize));

            // chunks — throttle progress callbacks so the UI doesn't thrash on big files.
            using var handle = File.OpenRead(path);
            var buf = new byte[ChunkSize];
            long sent = 0;
            long lastReported = 0;
            var  lastReportAt = DateTime.UtcNow;
            var  minInterval  = TimeSpan.FromMilliseconds(100);
            var  minBytes     = Math.Max(totalSize / 50, (long)ChunkSize * 4);
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
                if (sent - lastReported >= minBytes || (now - lastReportAt) >= minInterval)
                {
                    var bytesSoFar = sent;
                    lastReported = sent;
                    lastReportAt = now;
                    Dispatch(() => OnProgress?.Invoke(peerIP, $"Sending {filename}", bytesSoFar, totalSize));
                }
            }

            // file_end
            var endPacket = new Dictionary<string, object?>
            {
                ["type"] = "file_end", ["transfer_id"] = transferId,
                ["sender"] = myName, ["sender_public_key_b64"] = myKey, ["port"] = TcpPort,
            };
            await stream.WriteAsync(FrameCodec.EncodeDict(endPacket)).ConfigureAwait(false);
            Dispatch(() => OnComplete?.Invoke(peerIP, $"Sending {filename}", path));
            return true;
        }
        catch { return false; }
    }

    private void Dispatch(Action action) => _dq?.TryEnqueue(() => action());
}
