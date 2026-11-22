using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FxSsh;
using FxSsh.Algorithms;
using FxSsh.Messages.UserAuth;
using FxSsh.Services;
using SimpleSftp.Core;
using SimpleSftp.Desktop;

internal static class FeatureChecks
{
    internal static async Task Run(string root, Action<bool, string> check)
    {
        StartupChecks(root, check);
        var history = new TransferTracker();
        var active = history.Open(1, "tester", "active", false, 100);
        for (var i = 0; i < 250; i++) history.Open(1, "tester", i.ToString(), true, null).Finish("Finished");
        check(history.Snapshot().Length == 201 && history.Snapshot().Any(t => t.Id == active.Id && t.Status == "Active"), "Transfer history is bounded while preserving active transfers");
        await KeyAndMonitorChecks(root, check);
        await BackupChecks(root, check);
    }

    private static void StartupChecks(string root, Action<bool, string> check)
    {
        var command = WindowsStartup.BuildCommand(Path.Combine(root, "Program Files", "SimpleSftp.Desktop.exe"), Path.Combine(root, "Private data"));
        check(command.StartsWith('"') && command.Contains("--startup --data-dir ") && command.Contains("Private data\""), "Windows startup command quotes executable and data paths with spaces");
        var options = LaunchOptions.Parse(["--startup", "--data-dir", Path.Combine(root, "Private data")], root);
        check(options.WindowsStartup && options.DataDirectory == Path.GetFullPath(Path.Combine(root, "Private data")), "Startup launch arguments preserve the configured private directory");
        var defaults = new ServerSettings();
        check(!defaults.StartWithWindows && !defaults.AutoStartServer && defaults.StartMinimized, "Windows startup and automatic sharing remain opt-in");
        var invalid = false;
        try { LaunchOptions.Parse(["--data-dir"], root); } catch (ArgumentException) { invalid = true; }
        check(invalid, "Incomplete startup arguments are rejected");
    }

    private static async Task KeyAndMonitorChecks(string root, Action<bool, string> check)
    {
        var work = Path.Combine(root, "key-features"); Directory.CreateDirectory(work);
        var home = Path.Combine(work, "home"); Directory.CreateDirectory(home);
        var data = Path.Combine(work, "config");
        var ecKey = Path.Combine(work, "ecdsa"); var rsaKey = Path.Combine(work, "rsa"); var wrongKey = Path.Combine(work, "wrong");
        await GenerateKey(ecKey, "ecdsa", "256"); await GenerateKey(rsaKey, "rsa", "2048"); await GenerateKey(wrongKey, "ecdsa", "256");
        var ecPublic = File.ReadAllText(ecKey + ".pub").Trim(); var rsaPublic = File.ReadAllText(rsaKey + ".pub").Trim();
        check(PublicKeys.Parse(ecPublic).Fingerprint.StartsWith("SHA256:") && PublicKeys.Parse(rsaPublic).Algorithm == "ssh-rsa", "Independent OpenSSH ECDSA and RSA public keys import successfully");
        var invalid = false; try { PublicKeys.Parse(File.ReadAllText(ecKey)); } catch (ArgumentException) { invalid = true; }
        check(invalid, "Private keys cannot be added as authorized public keys");
        invalid = false; try { PublicKeys.Parse("ssh-rsa malformed"); } catch (ArgumentException) { invalid = true; }
        check(invalid, "Malformed authorized public keys are rejected");
        invalid = false; try { new SftpUser { Username = "no-method", PasswordAuthenticationEnabled = false, HomeDirectory = home }.Validate(); } catch (ArgumentException) { invalid = true; }
        check(invalid, "An account must retain at least one usable authentication method");
        var user = new SftpUser { Username = "key-user", PasswordHash = Passwords.Hash("test-feature-password-942!"), PasswordAuthenticationEnabled = false, AuthorizedKeys = [ecPublic, rsaPublic], HomeDirectory = home };
        user.Validate();
        var settings = new ServerSettings { Port = FreePort(), Users = [user] };
        PrivateStorage.Prepare(data); new SettingsStore(data).Save(settings);
        await using var host = new SftpHost(data);
        await host.StartAsync(settings);
        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        File.WriteAllBytes(Path.Combine(work, "input.bin"), payload);
        File.WriteAllText(Path.Combine(home, "existing.txt"), "key authentication");
        var ec = await Sftp(work, settings.Port, ecKey, "get existing.txt ec.txt\nbye\n");
        check(ec.Code == 0 && File.Exists(Path.Combine(work, "ec.txt")), "ECDSA public-key authentication downloads through Windows OpenSSH");
        var rsa = await Sftp(work, settings.Port, rsaKey, "get existing.txt rsa.txt\nbye\n");
        check(rsa.Code == 0 && File.Exists(Path.Combine(work, "rsa.txt")), "RSA SHA-2 public-key authentication downloads through Windows OpenSSH");
        var rejected = await Sftp(work, settings.Port, wrongKey, "bye\n");
        check(rejected.Code != 0, "An unauthorized public key cannot authenticate");
        var password = await Sftp(work, settings.Port, null, "bye\n", password: "test-feature-password-942!");
        check(password.Code != 0, "A key-only account rejects its otherwise correct password");

        await host.StopAsync(); user.ReadOnly = true; await host.StartAsync(settings);
        var readOnly = await Sftp(work, settings.Port, ecKey, "put input.bin forbidden.bin\nbye\n");
        check(readOnly.Code == 0 && readOnly.Output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(home, "forbidden.bin")), "Read-only permissions are enforced for public-key users");
        await host.StopAsync(); user.ReadOnly = false; await host.StartAsync(settings);

        using (var process = StartSftp(work, settings.Port, ecKey, limit: "8192"))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteAsync("put input.bin /monitored.bin\nget /monitored.bin monitored-download.bin\n"); await process.StandardInput.FlushAsync();
                await Until(() => host.Sessions.Any(s => s.Username == "key-user" && s.Authentication == "Public key"));
                check(host.Sessions.Any(s => s.RemoteAddress.StartsWith("127.0.0.1:")), "Live sessions show the verified username, method, address, and connection time");
                await Until(() => host.Transfers.Any(t => t.Direction == "Upload" && t.BytesTransferred > 0));
                check(host.Transfers.Any(t => t.Direction == "Upload" && t.BytesTransferred > 0), "Live upload counters advance during a transfer");
                await Until(() => host.Transfers.Any(t => t.Direction == "Download" && t.Status == "Active"));
                check(host.Transfers.Any(t => t.Direction == "Download" && t.TotalBytes == payload.Length && t.Percent is >= 0 and <= 100), "Live downloads expose bounded percentage progress and total size");
                await Until(() => host.Transfers.Any(t => t.Path == "/monitored.bin" && t.Direction == "Download" && t.Status == "Finished"));
                check(host.Transfers.Any(t => t.Path == "/monitored.bin" && t.Direction == "Upload" && t.BytesTransferred == payload.Length && t.Status == "Finished"), "Finished transfer history retains the correct byte count");
                await process.StandardInput.WriteAsync("bye\n"); process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                _ = await stdout; _ = await stderr;
                check(File.ReadAllBytes(Path.Combine(work, "monitored-download.bin")).SequenceEqual(payload), "Monitored upload/download remains byte exact");
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        await Until(() => host.Sessions.Length == 0);
        check(host.Sessions.Length == 0, "Closing an authenticated client removes its live session");

        using (var process = StartSftp(work, settings.Port, ecKey, limit: "64"))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteAsync("get /monitored.bin interrupted-download.bin\nbye\n"); process.StandardInput.Close();
                await Until(() => host.Transfers.Any(t => t.Status == "Active" && t.Direction == "Download"));
                var session = host.Sessions.Single(s => s.Username == "key-user");
                await host.DisconnectSessionAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                check(host.Sessions.Length == 0 && host.Transfers.Any(t => t.SessionId == session.Id && t.Status == "Interrupted"), "Operator disconnect removes the selected session and marks its active transfer interrupted");
                _ = await stdout; _ = await stderr;
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        await host.StopAsync();
        user.Enabled = false;
        settings.Users.Add(new SftpUser { Username = "enabled", PasswordHash = user.PasswordHash, HomeDirectory = home });
        await host.StartAsync(settings);
        var disabled = await Sftp(work, settings.Port, ecKey, "bye\n");
        check(disabled.Code != 0, "Disabled accounts reject previously authorized public keys");
        await host.StopAsync();
        user.Enabled = true; user.AuthorizedKeys.Clear(); user.PasswordAuthenticationEnabled = true;
        await host.StartAsync(settings);
        var revoked = await Sftp(work, settings.Port, ecKey, "bye\n");
        check(revoked.Code != 0, "Removing an authorized key revokes public-key access");
        await host.StopAsync();
        await UnverifiedProbeCheck(ecPublic, check);
    }

    private static async Task UnverifiedProbeCheck(string publicKey, Action<bool, string> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var socket = await listener.AcceptSocketAsync(); await connect;
        var session = new Session(socket, [], "SSH-2.0-Test");
        var auth = (UserAuthService)session.RegisterService("ssh-userauth");
        var completed = false; auth.UserAuth += (_, args) => args.Result = true; auth.AuthenticationCompleted += (_, _) => completed = true;
        var key = PublicKeys.Parse(publicKey);
        using var writer = new SshDataWriter(); writer.Write((byte)50); writer.Write("key-user", Encoding.UTF8); writer.Write("ssh-connection", Encoding.ASCII); writer.Write("publickey", Encoding.ASCII); writer.Write(false); writer.Write(key.Algorithm, Encoding.ASCII); writer.WriteBinary(key.Blob);
        var probe = new PublicKeyRequestMessage(); probe.Load(writer.ToByteArray()); auth.HandleMessageCore(probe);
        check(!completed && session.GetService<ConnectionService>() == null, "Accepting an unsigned public-key probe never signs in or opens the connection service");
        using var passwordWriter = new SshDataWriter(); passwordWriter.Write((byte)50); passwordWriter.Write("first-user", Encoding.UTF8); passwordWriter.Write("ssh-connection", Encoding.ASCII); passwordWriter.Write("password", Encoding.ASCII); passwordWriter.Write(false); passwordWriter.Write("test", Encoding.UTF8);
        var passwordRequest = new PasswordRequestMessage(); passwordRequest.Load(passwordWriter.ToByteArray());
        var completions = 0; auth.AuthenticationCompleted += (_, _) => completions++;
        auth.HandleMessageCore(passwordRequest); auth.HandleMessageCore(passwordRequest);
        check(completions == 1 && session.IsDisconnected, "An authenticated session cannot re-authenticate and change its live identity");
        await session.DisconnectAsync();
    }

    private static async Task BackupChecks(string root, Action<bool, string> check)
    {
        var work = Path.Combine(root, "backup-features"); Directory.CreateDirectory(work);
        var home = Path.Combine(work, "home"); Directory.CreateDirectory(home);
        var store = new SettingsStore(Path.Combine(work, "source")); PrivateStorage.Prepare(store.DataDirectory);
        var ec = new EcdsaKey("nistp256", ""); var pem = ec.ExportKey(); File.WriteAllText(Path.Combine(store.DataDirectory, "host-key.pem"), pem);
        var pub = "ecdsa-sha2-nistp256 " + Convert.ToBase64String(ec.CreateKeyAndCertificatesData());
        var settings = new ServerSettings { Port = FreePort(), StartWithWindows = true, AutoStartServer = true, MinimizeToTray = false, Users = [new SftpUser { Username = "restored", HomeDirectory = home, PasswordAuthenticationEnabled = false, AuthorizedKeys = [pub] }] };
        store.Save(settings);
        var backup = Path.Combine(work, "portable.sftpbak"); const string secret = "backup-feature-password-942!";
        BackupService.Export(store, backup, secret);
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(backup));
        check(!raw.Contains("PRIVATE KEY") && !raw.Contains("restored") && !raw.Contains(pub), "Portable backup encrypts account configuration and the private host identity");
        var target = new SettingsStore(Path.Combine(work, "target")); PrivateStorage.Prepare(target.DataDirectory);
        target.Save(new ServerSettings());
        var before = File.ReadAllText(target.SettingsPath);
        var rejected = false; try { BackupService.Restore(target, backup, "incorrect-password-942!"); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllText(target.SettingsPath) == before, "A wrong backup password cannot modify existing settings");
        var modified = File.ReadAllBytes(backup); modified[^1] ^= 1; var tampered = Path.Combine(work, "tampered.sftpbak"); File.WriteAllBytes(tampered, modified);
        rejected = false; try { BackupService.Restore(target, tampered, secret); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllText(target.SettingsPath) == before, "An altered backup fails authentication before any settings are replaced");
        Directory.Move(home, home + "-moved");
        var restored = BackupService.Restore(target, backup, secret);
        check(restored.Users.Single().AuthorizedKeys.Single() == pub && !restored.MinimizeToTray, "Backup restore preserves users, authorized keys, and application preferences");
        check(!restored.StartWithWindows && !restored.AutoStartServer, "Restoring a backup disables Windows startup and automatic server start");
        check(File.ReadAllText(Path.Combine(target.DataDirectory, "host-key.pem")) == pem, "Backup restore preserves the exact private host identity");
        check(!Directory.Exists(restored.Users[0].HomeDirectory), "Backups can restore accounts whose folders are unavailable on the destination computer");
        await using var host = new SftpHost(target.DataDirectory);
        var failed = false; try { await host.StartAsync(restored); } catch (ArgumentException) { failed = true; }
        check(failed && !host.IsRunning, "Restored accounts cannot start sharing until their folders are valid");
        restored.Users[0].HomeDirectory = home + "-moved"; target.Save(restored);
        await host.StartAsync(restored);
        check(host.Fingerprint == "SHA256:" + ec.GetFingerprint().TrimEnd('='), "Restored server presents the original host fingerprint after folder review");
        await host.StopAsync();
        check(!Directory.EnumerateDirectories(target.DataDirectory, "restore-*").Any(), "Restore removes its temporary plaintext staging files");
        var blocked = new SettingsStore(Path.Combine(work, "blocked-target")); PrivateStorage.Prepare(blocked.DataDirectory); blocked.Save(new ServerSettings());
        var originalSettings = File.ReadAllText(blocked.SettingsPath);
        var originalIdentity = new EcdsaKey("nistp256", "").ExportKey(); File.WriteAllText(Path.Combine(blocked.DataDirectory, "host-key.pem"), originalIdentity);
        var failedCommit = false;
        using (File.Open(blocked.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { BackupService.Restore(blocked, backup, secret); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failedCommit = true; }
        }
        check(failedCommit && File.ReadAllText(blocked.SettingsPath) == originalSettings && File.ReadAllText(Path.Combine(blocked.DataDirectory, "host-key.pem")) == originalIdentity, "A failed settings commit rolls back the host key and preserves the original configuration");
    }

    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static async Task GenerateKey(string file, string type, string bits)
    {
        var start = new ProcessStartInfo("ssh-keygen.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-q", "-t", type, "-b", bits, "-N", "", "-f", file }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if (process.ExitCode != 0) throw new Exception("ssh-keygen failed: " + await process.StandardError.ReadToEndAsync());
    }
    private static Process StartSftp(string work, int port, string? key, string? limit = null, string? password = null)
    {
        var start = new ProcessStartInfo("sftp.exe") { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var option in new[] { "BatchMode=" + (password == null ? "yes" : "no"), "StrictHostKeyChecking=accept-new", "UserKnownHostsFile=known_hosts", "IdentitiesOnly=yes", "IdentityAgent=none", "PreferredAuthentications=" + (password == null ? "publickey" : "password"), "NumberOfPasswordPrompts=1", "ConnectTimeout=5" })
        { start.ArgumentList.Add("-o"); start.ArgumentList.Add(option); }
        if (key != null) { start.ArgumentList.Add("-i"); start.ArgumentList.Add(key); }
        if (limit != null) { start.ArgumentList.Add("-l"); start.ArgumentList.Add(limit); }
        start.ArgumentList.Add("-P"); start.ArgumentList.Add(port.ToString()); start.ArgumentList.Add("key-user@127.0.0.1");
        if (password != null)
        {
            start.Environment["SSH_ASKPASS"] = Environment.ProcessPath!; start.Environment["SSH_ASKPASS_REQUIRE"] = "force"; start.Environment["DISPLAY"] = "1";
            start.Environment["SIMPLE_SFTP_ASKPASS"] = "1"; start.Environment["SIMPLE_SFTP_PASSWORD"] = password;
        }
        return Process.Start(start)!;
    }
    private static async Task<(int Code, string Output)> Sftp(string work, int port, string? key, string commands, string? password = null)
    {
        using var process = StartSftp(work, port, key, password: password);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(commands); process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var output = await stdout + await stderr;
            if (process.ExitCode != 0 && key != null && !key.EndsWith("wrong")) Console.WriteLine(output);
            return (process.ExitCode, output);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
