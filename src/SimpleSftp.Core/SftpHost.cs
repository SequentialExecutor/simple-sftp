using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using FxSsh;
using FxSsh.Algorithms;
using FxSsh.Services;
using FxSsh.Services.Sftp;

namespace SimpleSftp.Core;

public sealed record ActivityEntry(DateTimeOffset Time, string Level, string Message)
{
    public string DisplayTime => Time.ToLocalTime().ToString("HH:mm:ss");
}

public sealed class SftpHost(string dataDirectory) : IAsyncDisposable
{
    private SshServer? server;
    private readonly ConcurrentDictionary<Session, ClientSession> sessions = new();
    private readonly object logLock = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly object admissionLock = new();
    private readonly PasswordAuthenticator authenticator = new();
    private readonly AuthenticationThrottle keyThrottle = new();
    private readonly TransferTracker transfers = new();
    public ClientSession[] Sessions => sessions.Values.OrderBy(s => s.ConnectedAt).ToArray();
    public FileTransfer[] Transfers => transfers.Snapshot();
    public Task DisconnectSessionAsync(long id)
    {
        var session = sessions.Keys.FirstOrDefault(s => s.Id == id);
        return session?.DisconnectAsync(description: "Disconnected by the server operator.") ?? Task.CompletedTask;
    }
    internal TimeSpan AuthenticationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool IsRunning => server != null;
    public int ConnectionCount => sessions.Count;
    public string Fingerprint { get; private set; } = "Generated when the server first starts";
    public event Action<ActivityEntry>? Activity;
    public event Action? ConnectionsChanged;
    public string LogPath => Path.Combine(dataDirectory, "activity.jsonl");

    public void ReloadIdentity()
    {
        if (IsRunning) throw new InvalidOperationException("Stop the server before changing its identity.");
        var path = Path.Combine(dataDirectory, "host-key.pem");
        if (!File.Exists(path)) { Fingerprint = "Generated when the server first starts"; return; }
        var key = new EcdsaKey("nistp256", ""); key.ImportKey(File.ReadAllText(path));
        Fingerprint = "SHA256:" + key.GetFingerprint().TrimEnd('=');
    }

    public async Task StartAsync(ServerSettings settings)
    {
        await lifecycle.WaitAsync();
        try
        {
            if (server != null) return;
            settings.Validate();
            if (!settings.Users.Any(u => u.Enabled)) throw new InvalidOperationException("Add at least one enabled user before starting the server.");
            // Freeze a configuration snapshot for the entire run.
            var snapshot = JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(settings))!;
            var privatePath = Path.GetFullPath(dataDirectory);
            foreach (var user in snapshot.Users.Where(u => u.Enabled))
            {
                var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(user.HomeDirectory));
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (privatePath.Equals(home, comparison) || privatePath.StartsWith(Path.EndsInDirectorySeparator(home) ? home : home + Path.DirectorySeparatorChar, comparison))
                    throw new ArgumentException("A user's home folder cannot contain the application's private settings folder.");
            }
            PrivateStorage.Prepare(dataDirectory);
            var keyPath = Path.Combine(dataDirectory, "host-key.pem");
            var hostKey = new EcdsaKey("nistp256", "");
            if (File.Exists(keyPath)) hostKey.ImportKey(File.ReadAllText(keyPath));
            else
            {
                using var output = new StreamWriter(new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None));
                output.Write(hostKey.ExportKey());
            }
            Fingerprint = "SHA256:" + hostKey.GetFingerprint().TrimEnd('=');
            var candidate = new SshServer(new StartingInfo(IPAddress.Parse(snapshot.ListenAddress), snapshot.Port, "SSH-2.0-SimpleSftp"))
            {
                InactivityTimeout = TimeSpan.FromMinutes(5)
            };
            candidate.AddHostKey("ecdsa-sha2-nistp256", hostKey.ExportKey());
            candidate.ConnectionAccepted += (_, session) => Accept(session, snapshot);
            candidate.ExceptionRaised += (_, ex) => Log("Info", "Connection ended: " + ex.Message);
            try { await candidate.StartAsync(); }
            catch { await candidate.DisposeAsync(); throw; }
            server = candidate;
            Log("Info", $"Server started on {snapshot.ListenAddress}:{snapshot.Port}.");
        }
        finally { lifecycle.Release(); }
    }

    private void Accept(Session session, ServerSettings settings)
    {
        lock (admissionLock)
        {
            if (sessions.Count >= settings.MaxConnections)
            {
                _ = session.DisconnectAsync(description: "Server connection limit reached.");
                return;
            }
            sessions.TryAdd(session, new(session.Id, "Authenticating...", session.RemoteEndPoint?.ToString() ?? "client", "-", DateTimeOffset.Now));
        }
        ConnectionsChanged?.Invoke();
        var remote = session.RemoteEndPoint?.ToString() ?? "client";
        Log("Info", $"Connected: {remote}");
        session.Disconnected += (_, _) =>
        {
            sessions.TryRemove(session, out _);
            ConnectionsChanged?.Invoke();
            Log("Info", $"Disconnected: {remote}");
        };
        var address = (session.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6().ToString() ?? "unknown";
        var authenticated = 0;
        var authTimeout = new Timer(state =>
        {
            if (Volatile.Read(ref authenticated) == 0)
                _ = session.DisconnectAsync(description: "Authentication timed out.");
        }, null, AuthenticationTimeout, Timeout.InfiniteTimeSpan);
        session.Disconnected += (_, _) => authTimeout.Dispose();
        var attempts = 0;
        var keyAttempts = 0;
        session.ServiceRegistered += (_, service) =>
        {
            if (service is UserAuthService auth)
            {
                Action? keySuccess = null;
                auth.AuthenticationCompleted += (_, args) =>
                {
                    Interlocked.Exchange(ref authenticated, 1);
                    authTimeout.Dispose();
                    keySuccess?.Invoke(); keySuccess = null;
                    if (sessions.TryGetValue(session, out var info))
                        sessions.TryUpdate(session, info with { Username = args.Username, Authentication = args.AuthMethod == "publickey" ? "Public key" : "Password" }, info);
                    ConnectionsChanged?.Invoke();
                    Log("Info", $"Signed in: {args.Username} from {remote} ({args.AuthMethod})");
                };
                auth.UserAuth += (_, args) =>
                {
                    args.Result = false;
                    keySuccess = null;
                    if (args.AuthMethod == "publickey")
                    {
                        if (++keyAttempts > 20) { _ = session.DisconnectAsync(description: "Too many authentication attempts."); return; }
                        if (!keyThrottle.TryAcquire(address, args.Username, out var refund))
                        { _ = session.DisconnectAsync(description: "Too many authentication attempts. Try again later."); return; }
                        var keyUser = settings.Users.FirstOrDefault(u => u.Username == args.Username);
                        args.Result = PublicKeys.IsAuthorized(keyUser, args.Key);
                        if (args.Result && !args.HasSignature) refund();
                        if (args.Result && args.HasSignature) keySuccess = refund;
                        if (!args.Result) Log("Warning", $"Public key rejected: {args.Username} from {remote}");
                        return;
                    }
                    if (args.AuthMethod != "password") return;
                    if (++attempts > 5) { _ = session.DisconnectAsync(description: "Too many authentication attempts."); return; }
                    var user = settings.Users.FirstOrDefault(u => u.Username == args.Username);
                    args.Result = authenticator.Authenticate(address, args.Username, args.Password, user is { PasswordAuthenticationEnabled: true } ? user : null, out var throttled);
                    if (throttled)
                    {
                        Log("Warning", $"Login throttled from {remote}");
                        _ = session.DisconnectAsync(description: "Too many authentication attempts. Try again later.");
                        return;
                    }
                    if (!args.Result) Log("Warning", $"Login rejected: {args.Username} from {remote}");
                };
            }
            else if (service is ConnectionService connection)
            {
                // No shell, command execution, or forwarding handlers are registered.
                connection.SubsystemRequested += (_, args) =>
                {
                    if (args.Name != "sftp") return;
                    var user = settings.Users.FirstOrDefault(u => u.Enabled && u.Username == args.AttachedUserAuthArgs.Username);
                    if (user == null) return;
                    var fs = new RestrictedFileSystem(user.HomeDirectory, user.ReadOnly, message => Log("Info", $"{user.Username}: {message}"), transfers, session.Id, user.Username, () => session.IsDisconnected || args.Channel.IsForceClosed);
                    var sftp = new SftpService(fs, user.ReadOnly);
                    sftp.RequestFailed += (_, ex) => Log("Warning", $"SFTP request failed for {user.Username}: {ex}");
                    sftp.Attach(args.Channel);
                    args.Agreed = true;
                };
            }
        };
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            if (server == null) return;
            var current = server;
            server = null;
            await current.DisposeAsync();
            sessions.Clear();
            ConnectionsChanged?.Invoke();
            Log("Info", "Server stopped. All clients disconnected.");
        }
        finally { lifecycle.Release(); }
    }

    private void Log(string level, string message)
    {
        message = new string(message.Select(c => char.IsControl(c) ? ' ' : c).Take(1200).ToArray());
        var entry = new ActivityEntry(DateTimeOffset.Now, level, message);
        try
        {
            lock (logLock)
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 5 * 1024 * 1024)
                    File.Move(LogPath, LogPath + ".1", true);
                File.AppendAllText(LogPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch (IOException) { entry = entry with { Level = "Warning", Message = message + " (Activity could not be written to disk.)" }; }
        catch (UnauthorizedAccessException) { entry = entry with { Level = "Warning", Message = message + " (Activity log access denied.)" }; }
        Activity?.Invoke(entry);
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}




