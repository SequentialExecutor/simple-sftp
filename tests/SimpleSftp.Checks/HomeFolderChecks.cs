using System.IO;
using System.Text.Json;
using SimpleSftp.Core;

internal static class HomeFolderChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "missing-home-check");
        var user = new SftpUser { Username = "folder-test", HomeDirectory = path, PasswordHash = "stored-hash" };
        var notifications = 0;
        user.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SftpUser.IsHomeDirectoryMissing)) notifications++; };
        user.RefreshHomeDirectoryStatus();
        check(user.IsHomeDirectoryMissing && user.HomeDirectoryHint.Contains(path), "Missing home folders expose an observable state and an actionable tooltip");
        var json = JsonSerializer.Serialize(user);
        check(!json.Contains("IsHomeDirectoryMissing") && !json.Contains("HomeDirectoryHint"), "Folder availability is rechecked locally and never persisted in backups or settings");
        Directory.CreateDirectory(path); user.RefreshHomeDirectoryStatus();
        check(!user.IsHomeDirectoryMissing && notifications == 2, "Creating a missing folder clears its warning and notifies the UI");
        Directory.Delete(path);
        var informative = false;
        try { user.Validate(); } catch (ArgumentException ex) { informative = ex.Message.Contains(user.Username) && ex.Message.Contains(path); }
        check(informative && user.IsHomeDirectoryMissing, "Pre-start validation reports the affected username and unavailable folder");
        user.Enabled = false; user.Validate();
        check(user.IsHomeDirectoryMissing, "Disabled accounts keep their unavailable-folder warning without blocking validation");
        user.Enabled = true;
        var other = new SftpUser { Username = "other", HomeDirectory = path + "-other", PasswordHash = "stored-hash" };
        var settings = new ServerSettings { Users = [user, other] };
        var store = new SettingsStore(Path.Combine(root, "folder-repair-config")); PrivateStorage.Prepare(store.DataDirectory);
        store.Save(settings, false);
        var repaired = Path.Combine(root, "repaired-home"); Directory.CreateDirectory(repaired); user.HomeDirectory = repaired;
        store.Save(settings, false);
        check(store.Load().Users[0].HomeDirectory == repaired && !Directory.Exists(store.Load().Users[1].HomeDirectory), "Accounts can be repaired and saved one at a time while other home folders remain unavailable");
    }
}
