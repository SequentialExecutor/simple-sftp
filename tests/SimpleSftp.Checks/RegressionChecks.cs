using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FxSsh;
using FxSsh.Messages;
using FxSsh.Messages.Connection;
using FxSsh.Services;
using FxSsh.Services.Sftp;
using SimpleSftp.Core;

internal static class RegressionChecks
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        await SenderChecks(check);
        HandleAndErrorChecks(check);
        ThrottleChecks(check);
        await ChannelChecks(check);
        await LifecycleChecks(root, check);
    }

    private static async Task SenderChecks(Action<bool, string> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var packets = new List<int>();
        var active = 0;
        var peak = 0;
        var finished = false;
        using var sender = new PacketSender(async (packet, token) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref active));
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            // Deliberately fragment each response across asynchronous yields.
            for (var i = 0; i < 4; i++) await Task.Yield();
            packets.Add(packet[0]);
            Interlocked.Decrement(ref active);
        }, ex => throw new Exception("Unexpected response failure", ex), () => finished = true);
        sender.Send([0]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 1; i <= PacketSender.Capacity; i++) sender.Send([(byte)i]);
        var blocked = Task.Run(() => sender.Send([(byte)(PacketSender.Capacity + 1)]));
        await Task.Delay(100);
        check(!blocked.IsCompleted, "Slow response transport applies bounded backpressure");
        release.SetResult();
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        sender.Complete();
        await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        check(peak == 1 && packets.SequenceEqual(Enumerable.Range(0, PacketSender.Capacity + 2)), "Pipelined responses remain ordered with one active sender");
        check(finished, "EOF waits for every queued response to drain");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new PacketSender(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }, _ => { }, () => { });
        cancelled.Send([0]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < PacketSender.Capacity; i++) cancelled.Send([0]);
        var producer = Task.Run(() => { try { cancelled.Send([0]); return false; } catch (OperationCanceledException) { return true; } });
        await Task.Delay(50);
        cancelled.Dispose();
        check(await producer.WaitAsync(TimeSpan.FromSeconds(5)), "Disconnect releases a producer blocked on a full response queue");
        await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        check(cancelled.Completion.IsCompletedSuccessfully, "Disconnect cancels a stalled response transport cleanly");

        Exception? failure = null;
        using var failing = new PacketSender((_, _) => Task.FromException(new IOException("simulated send failure")), ex => failure = ex, () => { });
        failing.Send([0]);
        await failing.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        check(failure is IOException, "Response transport failures are observed");
    }

    private static void HandleAndErrorChecks(Action<bool, string> check)
    {
        var fs = new TrackingFileSystem();
        using var service = new SftpService(fs, false) { MaxOpenHandles = 2 };
        byte[] response = [];
        service.DataReceived += (_, packet) => response = packet;
        void Request(byte type, uint id, Action<SshDataWriter> fields) => service.OnData(Frame(type, id, fields));
        Request(3, 1, w => { w.Write("first", Encoding.UTF8); w.Write(1u); w.Write(0u); });
        var first = new SshDataReader(response.AsMemory(9)).ReadString(Encoding.ASCII);
        Request(11, 2, w => w.Write("/", Encoding.UTF8));
        Request(3, 3, w => { w.Write("must-not-create", Encoding.UTF8); w.Write(26u); w.Write(0u); });
        check(response[4] == 101 && ReadCode(response) == 4 && fs.Opened == 2, "Shared file/directory handle limit rejects requests before opening or creating files");
        Request(4, 4, w => w.Write(first, Encoding.ASCII));
        Request(3, 5, w => { w.Write("replacement", Encoding.UTF8); w.Write(1u); w.Write(0u); });
        check(response[4] == 102 && fs.Opened == 3 && fs.Disposed == 1, "Closing a handle restores capacity");
        service.Dispose();
        service.Dispose();
        check(fs.Disposed == 3, "Disconnect disposes all handles exactly once");
        Request(11, 6, w => w.Write("/", Encoding.UTF8));
        check(fs.Opened == 3, "Disposed SFTP engines reject subsequent work");

        using var errors = new SftpService(fs, false);
        errors.DataReceived += (_, packet) => response = packet;
        Exception? logged = null;
        errors.RequestFailed += (_, ex) => logged = ex;
        foreach (var (exception, code) in new (Exception, uint)[]
        {
            (new FileNotFoundException(@"private C:\Secret\share missing"), 2),
            (new UnauthorizedAccessException(@"private C:\Secret\share denied"), 3),
            (new ArgumentException(@"private C:\Secret\share invalid"), 5),
            (new IOException(@"private C:\Secret\share failure"), 4),
            (new InvalidOperationException(@"private C:\Secret\share unexpected"), 4)
        })
        {
            fs.Failure = exception;
            errors.OnData(Frame(17, 7, w => w.Write("missing", Encoding.UTF8)));
            check(ReadCode(response) == code && !Encoding.UTF8.GetString(response).Contains("Secret") && ReferenceEquals(logged, exception), $"{exception.GetType().Name} returns a safe status and preserves local diagnostics");
        }
        var frame = Frame(17, 8, w => w.Write("missing", Encoding.UTF8));
        errors.OnData(frame.AsMemory(0, 3));
        errors.OnData(frame.AsMemory(3));
        check(BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(5, 4)) == 8, "Fragmented SFTP requests preserve their request identifiers");
    }

    private static void ThrottleChecks(Action<bool, string> check)
    {
        var clock = new TestClock();
        var throttle = new AuthenticationThrottle(clock, addressLimit: 3, userLimit: 2);
        check(throttle.TryAcquire("a", "one", out _) && throttle.TryAcquire("b", "one", out _) && !throttle.TryAcquire("c", "one", out _), "Username throttling spans source addresses and reconnects");
        check(throttle.TryAcquire("a", "two", out _) && throttle.TryAcquire("a", "three", out _) && !throttle.TryAcquire("a", "four", out _), "Address throttling spans usernames and reconnects");
        clock.Now = clock.Now.AddMinutes(1);
        check(throttle.TryAcquire("a", "one", out var refund), "Authentication budgets recover after their time window");
        refund(); refund();
        check(throttle.TryAcquire("a", "one", out _) && throttle.TryAcquire("a", "one", out _), "Successful authentication refunds its reservation only once");
        var concurrent = new AuthenticationThrottle(addressLimit: 5, userLimit: 5);
        var accepted = 0;
        Parallel.For(0, 100, _ => { if (concurrent.TryAcquire("shared", "tester", out var ignored)) Interlocked.Increment(ref accepted); });
        check(accepted == 5, "Concurrent authentication cannot exceed its reserved budget");
        var small = new AuthenticationThrottle(clock, capacity: 4);
        check(small.TryAcquire("a", "a", out _) && small.TryAcquire("b", "b", out _) && !small.TryAcquire("c", "c", out _), "Throttle memory is bounded without evicting active restrictions");
        clock.Now = clock.Now.AddMinutes(1);
        check(small.TryAcquire("c", "c", out _), "Expired throttle entries are reclaimed");
        var hashes = new List<string>();
        var authenticator = new PasswordAuthenticator(verify: (_, hash) => { hashes.Add(hash); return true; });
        var user = new SftpUser { PasswordHash = "known-hash", Enabled = false };
        check(!authenticator.Authenticate("ip", "missing", "password", null, out _) && !authenticator.Authenticate("ip", "disabled", "password", user, out _) && hashes.Count == 2 && hashes.All(h => h != "known-hash" && h.Split(':')[0] == "600000"), "Unknown and disabled users perform equivalent password verification and cannot authenticate");
        user.Enabled = true;
        check(authenticator.Authenticate("ip", "enabled", "password", user, out _) && hashes.Last() == "known-hash", "Enabled accounts verify their own stored password hash");
    }

    private static async Task ChannelChecks(Action<bool, string> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var socket = await listener.AcceptSocketAsync();
        await connect;
        var session = new Session(socket, [], "SSH-2.0-Test");
        var connection = (ConnectionService)session.RegisterService("ssh-connection", new UserAuthArgs(session, "tester", "test-password"));
        try
        {
            for (uint i = 0; i < 9; i++) connection.HandleMessageCore(OpenChannel(i));
            await Until(() => connection.OpenChannelCount == 8);
            // A marker request runs after all channel opens in the same queue.
            var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SessionChannel? first = null;
            var subsystemCount = 0;
            connection.SubsystemRequested += (_, args) => { subsystemCount++; first = args.Channel; args.Agreed = true; marker.TrySetResult(); };
            connection.HandleMessageCore(Subsystem(0));
            await marker.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(connection.OpenChannelCount == 8, "SSH channel opens are capped even without starting a subsystem");
            // Exercise the actual channel window implementation, not only a
            // simulated slow transport. Credit arrives in two installments.
            var windowed = first!.SendDataAsync(new byte[2048]);
            check(!windowed.IsCompleted, "An exhausted SSH receive window suspends the response sender");
            first.ClientAdjustWindow(1024);
            await Task.Delay(50);
            check(!windowed.IsCompleted, "Partial window credit sends only the permitted bytes");
            first.ClientAdjustWindow(1024);
            await windowed.WaitAsync(TimeSpan.FromSeconds(5));
            check(windowed.IsCompletedSuccessfully, "Window adjustments resume and complete the response");
            var stalled = first.SendDataAsync(new byte[8]);
            var fs = new TrackingFileSystem();
            using var engine = new SftpService(fs, false);
            engine.Attach(first);
            first.OnData(Frame(3, 1, w => { w.Write("open", Encoding.UTF8); w.Write(1u); w.Write(0u); }));
            connection.HandleMessageCore(Subsystem(0));
            var close = new ChannelCloseMessage();
            close.Load(Payload(97, w => w.Write(0u)));
            connection.HandleMessageCore(close);
            await Until(() => first!.IsForceClosed);
            check(subsystemCount == 1, "Repeated subsystem requests cannot attach multiple SFTP engines");
            connection.HandleMessageCore(OpenChannel(10));
            await Until(() => connection.OpenChannelCount == 8);
            check(first.IsForceClosed && fs.Disposed == 1, "Closing a stalled SFTP channel disposes handles and releases channel capacity");
            try { await stalled.WaitAsync(TimeSpan.FromSeconds(5)); check(false, "Channel closure cancels a response waiting for window credit"); }
            catch (ObjectDisposedException) { check(true, "Channel closure cancels a response waiting for window credit"); }
            var blocked = first.SendDataAsync(new byte[8]);
            try { await blocked; check(false, "Closed channel rejects sends"); }
            catch (ObjectDisposedException) { check(true, "Closed channel rejects sends"); }

            using var releaseRequests = new ManualResetEventSlim();
            var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.SubsystemRequested += (_, args) =>
            {
                if (args.Channel.ServerChannelId == 1)
                {
                    processing.TrySetResult();
                    if (!releaseRequests.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Request flood test timed out");
                }
            };
            connection.HandleMessageCore(Subsystem(1));
            await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rejected = false;
            try
            {
                for (var i = 0; i < 257; i++)
                {
                    var eof = new ChannelEofMessage(); eof.Load(Payload(96, w => w.Write(2u)));
                    connection.HandleMessageCore(eof);
                }
            }
            catch (SshConnectionException) { rejected = true; }
            finally { releaseRequests.Set(); }
            check(rejected, "Inbound request floods are rejected at the bounded queue limit");
        }
        finally { await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        check(connection.Completion.IsCompleted, "Session shutdown waits for channel processing to finish");

        // A stopped socket pump cannot grow an unbounded wire-buffer queue.
        using var secondClient = new TcpClient();
        var secondConnect = secondClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var secondSocket = await listener.AcceptSocketAsync(); await secondConnect;
        var stalledSession = new Session(secondSocket, [], "SSH-2.0-Test");
        var rejectedOutput = false;
        try
        {
            for (var i = 0; i < 257; i++) stalledSession.SendMessage(new ChannelDataMessage { RecipientChannel = 0, Data = new byte[32768] });
        }
        catch (SshConnectionException) { rejectedOutput = true; }
        finally { await stalledSession.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        check(rejectedOutput, "A stalled socket cannot grow the outbound SSH queue indefinitely");
    }

    private static async Task LifecycleChecks(string root, Action<bool, string> check)
    {
        var home = Path.Combine(root, "regression-home"); Directory.CreateDirectory(home);
        var user = new SftpUser { Username = "active", PasswordHash = Passwords.Hash("regression-password-942!"), HomeDirectory = home };
        var disabled = new SftpUser { Username = "offline", PasswordHash = user.PasswordHash, HomeDirectory = Path.Combine(root, "unavailable"), Enabled = false };
        var settings = new ServerSettings { Users = [user, disabled] };
        var store = new SettingsStore(Path.Combine(root, "regression-config"));
        PrivateStorage.Prepare(store.DataDirectory);
        store.Save(settings);
        check(store.Load().Users.Count == 2, "Disabled accounts with missing folders do not block settings saves");
        disabled.Enabled = true;
        try { settings.Validate(); check(false, "Re-enabling an unavailable home is rejected"); }
        catch (ArgumentException) { check(true, "Re-enabling an unavailable home is rejected"); }
        disabled.Enabled = false;
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); settings.Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        await using var host = new SftpHost(store.DataDirectory) { AuthenticationTimeout = TimeSpan.FromMilliseconds(250) };
        await host.StartAsync(settings);
        check(host.IsRunning, "Disabled accounts with missing folders do not block other accounts starting");
        using var idle = new TcpClient(); await idle.ConnectAsync(IPAddress.Loopback, settings.Port);
        await Until(() => host.ConnectionCount == 1);
        await Until(() => host.ConnectionCount == 0);
        check(host.IsRunning, "Unauthenticated connections expire without stopping the listener");
        settings.MaxConnections = 1;
        await host.StopAsync(); await host.StartAsync(settings);
        using var one = new TcpClient(); using var two = new TcpClient();
        await one.ConnectAsync(IPAddress.Loopback, settings.Port);
        await Until(() => host.ConnectionCount == 1);
        await two.ConnectAsync(IPAddress.Loopback, settings.Port);
        await Task.Delay(75);
        check(host.ConnectionCount <= 1, "Connection admission respects the configured limit");
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(!host.IsRunning && host.ConnectionCount == 0, "Stopping with connected clients completes and releases connections");
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static uint ReadCode(byte[] response) => BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(9, 4));
    private static byte[] Payload(byte type, Action<SshDataWriter> fields)
    {
        using var writer = new SshDataWriter(); writer.Write(type); fields(writer); return writer.ToByteArray();
    }
    private static byte[] Frame(byte type, uint id, Action<SshDataWriter> fields)
    {
        var payload = Payload(type, w => { w.Write(id); fields(w); });
        var packet = new byte[payload.Length + 4]; BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)payload.Length); payload.CopyTo(packet, 4); return packet;
    }
    private static SessionOpenMessage OpenChannel(uint id)
    {
        var message = new SessionOpenMessage(); message.Load(Payload(90, w => { w.Write("session", Encoding.ASCII); w.Write(id); w.Write(0u); w.Write(1024u); })); return message;
    }
    private static SubsystemRequestMessage Subsystem(uint id)
    {
        var message = new SubsystemRequestMessage(); message.Load(Payload(98, w => { w.Write(id); w.Write("subsystem", Encoding.ASCII); w.Write(true); w.Write("sftp", Encoding.ASCII); })); return message;
    }
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TrackingFileSystem : ISftpFileSystem
    {
        internal int Opened; internal int Disposed; internal Exception? Failure;
        public ISftpFileHandle OpenFile(string path, SftpOpenFlags flags, SftpFileAttributes attributes) { Opened++; return new FileHandle(this); }
        public ISftpDirectoryHandle OpenDirectory(string path) { Opened++; return new DirectoryHandle(this); }
        public SftpFileAttributes GetAttributes(string path, bool followLinks) => throw Failure ?? new IOException("simulated error");
        public void SetAttributes(string path, SftpFileAttributes attributes) => throw new NotSupportedException();
        public void RemoveFile(string path) => throw new NotSupportedException();
        public void Rename(string oldPath, string newPath) => throw new NotSupportedException();
        public void MakeDirectory(string path, SftpFileAttributes attributes) => throw new NotSupportedException();
        public void RemoveDirectory(string path) => throw new NotSupportedException();
        public string RealPath(string path) => path;
        private sealed class FileHandle(TrackingFileSystem owner) : ISftpFileHandle
        {
            public int Read(long offset, Span<byte> buffer) => 0;
            public void Write(long offset, ReadOnlySpan<byte> data) { }
            public SftpFileAttributes GetAttributes() => new();
            public void SetAttributes(SftpFileAttributes attributes) { }
            public void Dispose() => owner.Disposed++;
        }
        private sealed class DirectoryHandle(TrackingFileSystem owner) : ISftpDirectoryHandle
        {
            public SftpFileEntry[] ReadEntries(int maxCount) => [];
            public void Dispose() => owner.Disposed++;
        }
    }
}
