using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FxSsh.Services.Sftp;
using SimpleSftp.Core;

if (Environment.GetEnvironmentVariable("SIMPLE_SFTP_ASKPASS") == "1" && args.Length > 0)
{
    Console.WriteLine(Environment.GetEnvironmentVariable("SIMPLE_SFTP_PASSWORD"));
    return;
}

if (args.Contains("--ui-smoke")) { UiSmoke.Run(); return; }
var root = Path.GetFullPath(Path.Combine("artifacts", "checks", Guid.NewGuid().ToString("N")));
var data = Path.Combine(root, "config");
var home = Path.Combine(root, "home");
var clientDir = Path.Combine(root, "client");
Directory.CreateDirectory(home);
Directory.CreateDirectory(clientDir);
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label); passed++;
}
void Denied(Action action, string label)
{
    try { action(); } catch (UnauthorizedAccessException) { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
HomeFolderChecks.Run(root, Check);
NetworkAddressChecks.Run(Check);
if (args.Contains("--features"))
{
    await FeatureChecks.Run(root, Check);
    Console.WriteLine($"\n{passed} feature checks passed. Test artifacts: {root}");
    return;
}
const string password = "SimpleSftp-test-only-942!";
var hash = Passwords.Hash(password);
Check(Passwords.Verify(password, hash), "Correct password accepted");
Check(!Passwords.Verify("wrong", hash), "Incorrect password rejected");
Check(hash != Passwords.Hash(password), "Passwords have independent random salts");
Check(!Passwords.Verify(password, "corrupt"), "Malformed password hashes rejected");
var user = new SftpUser { Username = "tester", PasswordHash = hash, HomeDirectory = home };
var settings = new ServerSettings { Users = [user] };
var store = new SettingsStore(data);
PrivateStorage.Prepare(data);
store.Save(settings);
Check(store.Load().Users.Single().Username == "tester", "Settings round trip");
Check(!File.ReadAllText(store.SettingsPath).Contains(password), "Settings never contain plaintext password");
var fs = new RestrictedFileSystem(home, false, _ => { });
foreach (var path in new[] { "../outside.txt", "/../../outside", "C:/Windows/win.ini", "file:stream", @"..\outside", "NUL", "COM1.txt", "file. ", "/../home-sibling/file" })
    Denied(() => fs.GetAttributes(path, true), "Blocked unsafe path: " + path);
var ro = new RestrictedFileSystem(home, true, _ => { });
Denied(() => ro.OpenFile("blocked.txt", SftpOpenFlags.Write | SftpOpenFlags.Create, null!), "Read-only blocks create");
File.WriteAllText(Path.Combine(home, "existing.txt"), "existing");
Denied(() => ro.RemoveFile("existing.txt"), "Read-only blocks delete");
Denied(() => ro.Rename("existing.txt", "moved.txt"), "Read-only blocks rename");
Denied(() => ro.MakeDirectory("blocked", null!), "Read-only blocks mkdir");
try
{
    Directory.CreateSymbolicLink(Path.Combine(home, "escape"), clientDir);
    Denied(() => fs.GetAttributes("escape", true), "Symbolic link escape blocked");
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Console.WriteLine("SKIP: symlink creation unavailable on this machine"); }

// OpenSSH is an independent protocol implementation; no test NuGet packages are required.
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start(); settings.Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
await using var host = new SftpHost(data);
var events = new List<ActivityEntry>();
host.Activity += entry => { lock(events) events.Add(entry); };
await host.StartAsync(settings);
Check(host.IsRunning, "Listener starts");
var fingerprint = host.Fingerprint;
Check(fingerprint.StartsWith("SHA256:"), "Host fingerprint available");
var payload = RandomNumberGenerator.GetBytes(16 * 1024 * 1024);
File.WriteAllBytes(Path.Combine(clientDir, "input.bin"), payload);
File.WriteAllBytes(Path.Combine(clientDir, "empty.bin"), []);

async Task<(int Code, string Output)> Client(string username, string commands, string secret = password, bool ssh = false)
{
    var start = new ProcessStartInfo(ssh ? "ssh.exe" : "sftp.exe")
    {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = clientDir,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
    };
    foreach (var option in new[] { "BatchMode=no", "StrictHostKeyChecking=accept-new", "UserKnownHostsFile=known_hosts", "NumberOfPasswordPrompts=1", "PreferredAuthentications=password", "ConnectTimeout=5" })
    { start.ArgumentList.Add("-o"); start.ArgumentList.Add(option); }
    start.ArgumentList.Add(ssh ? "-p" : "-P"); start.ArgumentList.Add(settings.Port.ToString());
    start.ArgumentList.Add(username + "@127.0.0.1");
    if (ssh) start.ArgumentList.Add("echo should-not-execute");
    start.Environment["SSH_ASKPASS"] = Environment.ProcessPath!;
    start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
    start.Environment["DISPLAY"] = "1";
    start.Environment["SIMPLE_SFTP_ASKPASS"] = "1";
    start.Environment["SIMPLE_SFTP_PASSWORD"] = secret;
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
    await process.StandardInput.WriteAsync(commands); process.StandardInput.Close();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
    catch { process.Kill(true); throw; }
    return (process.ExitCode, await stdout + await stderr);
}

var transfer = await Client("tester", "put input.bin /upload.bin\nget /upload.bin output.bin\nput empty.bin /empty.bin\nmkdir /sub\nrename /upload.bin /sub/renamed.bin\nls /sub\nbye\n");
Console.WriteLine(transfer.Output);
Check(transfer.Code == 0 && File.Exists(Path.Combine(clientDir, "output.bin")), "OpenSSH client connects and downloads");
Check(File.ReadAllBytes(Path.Combine(clientDir, "output.bin")).SequenceEqual(payload), "16 MB pipelined binary upload/download is byte exact");
Check(File.Exists(Path.Combine(home, "sub", "renamed.bin")), "Remote mkdir and rename work");
Check(new FileInfo(Path.Combine(home, "empty.bin")).Length == 0, "Empty file upload works");
var traversal = await Client("tester", "get /../config/host-key.pem stolen.pem\nbye\n");
Check(!File.Exists(Path.Combine(clientDir, "stolen.pem")), "Network traversal cannot read private host key");
var deleted = await Client("tester", "rm /sub/renamed.bin\nrmdir /sub\nbye\n");
Check(!Directory.Exists(Path.Combine(home, "sub")), "Remote delete and rmdir work");
var rejected = await Client("tester", "bye\n", "incorrect-password");
Check(rejected.Code != 0 && rejected.Output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase), "OpenSSH bad password rejected");
var unknown = await Client("unknown", "bye\n");
Check(unknown.Code != 0, "Unknown user rejected");
var command = await Client("tester", "", ssh: true);
Check(command.Code != 0 && !command.Output.Contains("\nshould-not-execute"), "SSH command execution rejected");
await host.StopAsync();
Check(!host.IsRunning && host.ConnectionCount == 0, "Stop clears connections");
user.ReadOnly = true;
settings.Users.Add(new SftpUser { Username = "disabled", PasswordHash = hash, HomeDirectory = home, Enabled = false });
await host.StartAsync(settings);
Check(host.Fingerprint == fingerprint, "Host identity persists across restart");
var readOnly = await Client("tester", "get existing.txt read-only-download.txt\nput input.bin /forbidden.bin\nrm existing.txt\nrename existing.txt changed.txt\nmkdir /forbidden\nbye\n");
Check(File.Exists(Path.Combine(clientDir, "read-only-download.txt")), "Read-only user can download");
Check(!File.Exists(Path.Combine(home, "forbidden.bin")) && File.Exists(Path.Combine(home, "existing.txt")) && !Directory.Exists(Path.Combine(home, "forbidden")), "Network read-only mutations are blocked");
var disabled = await Client("disabled", "bye\n");
Check(disabled.Code != 0, "Disabled user cannot sign in");
await host.StopAsync();
Check(File.Exists(host.LogPath) && File.ReadAllText(host.LogPath).Contains("Login rejected"), "Activity log records rejected sign-ins");
Check(!File.ReadAllText(host.LogPath).Contains(password), "Activity log does not expose passwords");
// Stop while an independent client has an open upload handle.
user.ReadOnly = false;
await host.StartAsync(settings);
var uploadOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
void WatchUpload(ActivityEntry entry) { if (entry.Message.Contains("Opened /interrupted.bin")) uploadOpened.TrySetResult(); }
host.Activity += WatchUpload;
var interrupted = Client("tester", "put input.bin /interrupted.bin\nbye\n");
await uploadOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));
await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
await interrupted.WaitAsync(TimeSpan.FromSeconds(10));
host.Activity -= WatchUpload;
using (File.Open(Path.Combine(home, "interrupted.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(!host.IsRunning && host.ConnectionCount == 0, "Stopping during a network upload releases the client's file handle");
await host.StartAsync(settings);
var recovered = await Client("tester", "get existing.txt recovered.txt\nbye\n");
Check(recovered.Code == 0 && File.Exists(Path.Combine(clientDir, "recovered.txt")), "A fresh network transfer works after interrupted-upload shutdown");
await host.StopAsync();

// Failed-login budgets survive reconnects and server stop/start within the app.
await host.StartAsync(settings);
for (var attempt = 0; attempt < 11; attempt++) await Client("tester", "bye\n", "wrong-password");
Check(File.ReadAllText(host.LogPath).Contains("Login throttled"), "Network password attempts are throttled across reconnects");
await host.StopAsync();
await host.StartAsync(settings);
var stillThrottled = await Client("tester", "bye\n");
Check(stillThrottled.Code != 0, "Stopping and starting the listener does not reset authentication throttling");
await host.StopAsync();
await RegressionChecks.Run(root, Check);
await FeatureChecks.Run(root, Check);
Console.WriteLine($"\n{passed} checks passed. Test artifacts: {root}");



