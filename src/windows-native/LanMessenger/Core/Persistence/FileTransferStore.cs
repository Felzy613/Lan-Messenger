using LanMessenger.Core.Protocol;

namespace LanMessenger.Core.Persistence;

public sealed record TransferKey(string IP, string TransferId);

public sealed class IncomingTransfer
{
    public string TransferId        { get; init; } = "";
    public string Filename          { get; init; } = "";
    public long   TotalSize         { get; init; }
    public string SenderIP          { get; init; } = "";
    public string SenderPublicKeyB64 { get; init; } = "";
    public string TempFilePath      { get; init; } = "";
    public long   BytesReceived     { get; set; }
    public FileStream? TempStream   { get; set; }
}

public sealed class OutgoingQueueItem
{
    public string Path     { get; init; } = "";
    public string Filename { get; init; } = "";
}

// Tracks in-flight incoming and outgoing file transfers (same model as Swift).
public sealed class FileTransferStore
{
    public static FileTransferStore Shared { get; } = new();

    public Dictionary<TransferKey, IncomingTransfer> Incoming       { get; } = [];
    public Dictionary<string, Queue<OutgoingQueueItem>> OutgoingQueues { get; } = []; // keyed by peerIP
    public HashSet<string> ActiveOutgoing { get; } = [];

    private FileTransferStore() { }

    public IncomingTransfer? BeginIncoming(
        string transferId, string filename, long size,
        string senderIP, string senderPublicKeyB64, string inboxDir)
    {
        var safe = PacketValidator.SanitizeFilename(filename);
        Directory.CreateDirectory(inboxDir);
        var tempPath = Path.Combine(inboxDir, $"{transferId}_{safe}.part");
        try
        {
            var stream = File.Create(tempPath);
            var transfer = new IncomingTransfer
            {
                TransferId         = transferId,
                Filename           = safe,
                TotalSize          = size,
                SenderIP           = senderIP,
                SenderPublicKeyB64 = senderPublicKeyB64,
                TempFilePath       = tempPath,
                TempStream         = stream,
            };
            var key = new TransferKey(senderIP, transferId);
            Incoming[key] = transfer;
            return transfer;
        }
        catch { return null; }
    }

    public void AppendChunk(byte[] data, TransferKey key)
    {
        if (!Incoming.TryGetValue(key, out var t) || t.TempStream is null) return;
        t.TempStream.Write(data);
        t.BytesReceived += data.Length;
    }

    // Rename temp file to final name; returns final path or null on failure.
    public string? FinalizeIncoming(TransferKey key, string inboxDir)
    {
        if (!Incoming.TryGetValue(key, out var t)) return null;
        Incoming.Remove(key);
        t.TempStream?.Dispose();
        t.TempStream = null;

        var finalBase = Path.Combine(inboxDir, t.Filename);
        var finalPath = FindAvailablePath(finalBase);
        try
        {
            File.Move(t.TempFilePath, finalPath, overwrite: false);
            return finalPath;
        }
        catch { return null; }
    }

    private static string FindAvailablePath(string basePath)
    {
        if (!File.Exists(basePath)) return basePath;
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var ext  = Path.GetExtension(basePath);
        var dir  = Path.GetDirectoryName(basePath)!;
        for (int i = 1; i <= 999; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}_{Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8]}{ext}");
    }

    public void Enqueue(string path, string filename, string peerIP)
    {
        if (!OutgoingQueues.TryGetValue(peerIP, out var q))
            q = OutgoingQueues[peerIP] = new Queue<OutgoingQueueItem>();
        q.Enqueue(new OutgoingQueueItem { Path = path, Filename = filename });
    }

    public void MarkTransferStarted(string peerIP) => ActiveOutgoing.Add(peerIP);

    public void MarkTransferFinished(string peerIP, bool success)
    {
        ActiveOutgoing.Remove(peerIP);
        if (OutgoingQueues.TryGetValue(peerIP, out var q) && q.Count > 0)
            q.Dequeue(); // remove the just-finished item
    }
}
