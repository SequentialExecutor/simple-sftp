using System.Collections.Concurrent;
using System.Diagnostics;

namespace SimpleSftp.Core;

public sealed record ClientSession(long Id, string Username, string RemoteAddress, string Authentication, DateTimeOffset ConnectedAt)
{
    public string ConnectedTime => ConnectedAt.ToLocalTime().ToString("HH:mm:ss");
}

public sealed record FileTransfer(Guid Id, long SessionId, string Username, string Path, string Direction, long BytesTransferred, long? TotalBytes, string Status, DateTimeOffset StartedAt, double BytesPerSecond)
{
    public string Transferred => FormatBytes(BytesTransferred);
    public string Speed => Status == "Active" ? FormatBytes((long)BytesPerSecond) + "/s" : "-";
    public double Percent => TotalBytes is > 0 ? Math.Clamp(100.0 * BytesTransferred / TotalBytes.Value, 0, 100) : 0;
    public bool IsIndeterminate => TotalBytes == null && Status == "Active";
    public string ProgressLabel => TotalBytes is > 0 ? $"{Percent:0}% of {FormatBytes(TotalBytes.Value)}" : TotalBytes == 0 ? "Empty file" : "Size not provided";
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";
}

internal sealed class TransferTracker
{
    private readonly ConcurrentDictionary<Guid, TransferState> transfers = new();
    internal TransferState Open(long session, string username, string path, bool upload, long? total)
    {
        var state = new TransferState(session, username, path, upload, total);
        transfers[state.Id] = state;
        // Keep every active transfer and only the latest 200 finished handles.
        foreach (var old in transfers.Values.Where(t => t.Status != "Active").OrderByDescending(t => t.StartedAt).Skip(200)) transfers.TryRemove(old.Id, out _);
        return state;
    }
    internal FileTransfer[] Snapshot()
    {
        foreach (var old in transfers.Values.Where(t => t.Status != "Active").OrderByDescending(t => t.StartedAt).Skip(200)) transfers.TryRemove(old.Id, out _);
        return transfers.Values.Select(t => t.Snapshot()).OrderBy(t => t.Status != "Active").ThenByDescending(t => t.StartedAt).ToArray();
    }
}

internal sealed class TransferState(long session, string username, string path, bool upload, long? total)
{
    private long bytes;
    private long expected = total ?? -1;
    private string status = "Active";
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    internal Guid Id { get; } = Guid.NewGuid();
    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    internal string Status => Volatile.Read(ref status);
    internal void Advance(int count) => Interlocked.Add(ref bytes, count);
    internal void SetSize(ulong? size) { if (size <= long.MaxValue) Interlocked.Exchange(ref expected, (long)size.Value); }
    internal void Finish(string value) { if (Interlocked.CompareExchange(ref status, value, "Active") == "Active") elapsed.Stop(); }
    internal FileTransfer Snapshot()
    {
        var count = Interlocked.Read(ref bytes); var size = Interlocked.Read(ref expected);
        return new(Id, session, username, path, upload ? "Upload" : "Download", count, size >= 0 ? size : null, Status, StartedAt, count / Math.Max(elapsed.Elapsed.TotalSeconds, 0.001));
    }
}
