using FxSsh.Services.Sftp;

namespace SimpleSftp.Core;

public sealed class RestrictedFileSystem : ISftpFileSystem
{
    private readonly LocalFileSystem inner;
    private readonly FolderGuard guard;
    private readonly Action<string> log;
    private TransferTracker? transfers;
    private long sessionId;
    private string username = "";
    private Func<bool> interrupted = () => false;
    internal RestrictedFileSystem(string root, bool readOnly, Action<string> log, TransferTracker transfers, long sessionId, string username, Func<bool> interrupted)
        : this(root, readOnly, log)
    { this.transfers = transfers; this.sessionId = sessionId; this.username = username; this.interrupted = interrupted; }
    public RestrictedFileSystem(string root, bool readOnly, Action<string> log)
    {
        guard = new(root);
        inner = new(root, readOnly);
        this.log = log;
    }
    private void Check(string path) => guard.Check(path);
    public ISftpFileHandle OpenFile(string path, SftpOpenFlags flags, SftpFileAttributes attributes)
    {
        Check(path);
        var handle = inner.OpenFile(path, flags, attributes);
        log($"Opened {path} ({flags})");
        TransferState? transfer = null;
        if (transfers != null)
        {
            var upload = (flags & (SftpOpenFlags.Write | SftpOpenFlags.Append)) != 0;
            long? total = null;
            var size = upload ? attributes?.Size : handle.GetAttributes()?.Size;
            if (size <= long.MaxValue) total = (long)size.Value;
            transfer = transfers.Open(sessionId, username, path, upload, total);
        }
        return new LoggedHandle(handle, () => Check(path), () => log($"Closed {path}"), transfer, interrupted);
    }
    public ISftpDirectoryHandle OpenDirectory(string path)
    {
        Check(path);
        return new CheckedDirectory(inner.OpenDirectory(path), () => Check(path));
    }
    public SftpFileAttributes GetAttributes(string path, bool followLinks) { Check(path); return inner.GetAttributes(path, followLinks); }
    public void SetAttributes(string path, SftpFileAttributes attributes) { Check(path); inner.SetAttributes(path, attributes); log($"Updated attributes: {path}"); }
    public void RemoveFile(string path) { Check(path); inner.RemoveFile(path); log($"Deleted file: {path}"); }
    public void Rename(string oldPath, string newPath) { Check(oldPath); Check(newPath); inner.Rename(oldPath, newPath); log($"Renamed {oldPath} to {newPath}"); }
    public void MakeDirectory(string path, SftpFileAttributes attributes) { Check(path); inner.MakeDirectory(path, attributes); log($"Created folder: {path}"); }
    public void RemoveDirectory(string path) { Check(path); inner.RemoveDirectory(path); log($"Deleted folder: {path}"); }
    public string RealPath(string path) { Check(path); return inner.RealPath(path); }

    private sealed class CheckedDirectory(ISftpDirectoryHandle inner, Action check) : ISftpDirectoryHandle
    {
        public SftpFileEntry[] ReadEntries(int maxCount) { check(); return inner.ReadEntries(maxCount); }
        public void Dispose() => inner.Dispose();
    }
    private sealed class LoggedHandle(ISftpFileHandle inner, Action check, Action closed, TransferState? transfer, Func<bool> interrupted) : ISftpFileHandle
    {
        private int disposed;
        public int Read(long offset, Span<byte> buffer)
        {
            try { check(); var count = inner.Read(offset, buffer); transfer?.Advance(count); return count; }
            catch { transfer?.Finish("Failed"); throw; }
        }
        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            try { check(); inner.Write(offset, data); transfer?.Advance(data.Length); }
            catch { transfer?.Finish("Failed"); throw; }
        }
        public SftpFileAttributes GetAttributes() { check(); return inner.GetAttributes(); }
        public void SetAttributes(SftpFileAttributes attributes)
        {
            try { check(); inner.SetAttributes(attributes); transfer?.SetSize(attributes?.Size); }
            catch { transfer?.Finish("Failed"); throw; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { inner.Dispose(); transfer?.Finish(interrupted() ? "Interrupted" : "Finished"); }
            catch { transfer?.Finish("Failed"); throw; }
            finally { closed(); }
        }
    }
}
