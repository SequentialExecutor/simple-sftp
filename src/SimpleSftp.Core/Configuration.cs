using System.Net;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleSftp.Core;

public sealed class ServerSettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public bool? AutoDetectListenAddress { get; set; }
    public int Port { get; set; } = 2222;
    public int MaxConnections { get; set; } = 20;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool AutoStartServer { get; set; }
    public List<SftpUser> Users { get; set; } = [];

    public void Validate(bool requireAccessibleHomes = true)
    {
        if (!IPAddress.TryParse(ListenAddress, out _)) throw new ArgumentException("Enter a valid listen IP address, such as 127.0.0.1 or 0.0.0.0.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if (MaxConnections is < 1 or > 200) throw new ArgumentException("Connection limit must be between 1 and 200.");
        if (Users == null || Users.Any(u => u == null)) throw new ArgumentException("Users must be a list of valid accounts.");
        if (Users.Select(u => u.Username).Distinct(StringComparer.Ordinal).Count() != Users.Count)
            throw new ArgumentException("Each username must be unique.");
        if (Users.Count > 1000) throw new ArgumentException("At most 1000 accounts are supported.");
        foreach (var user in Users) user.Validate(requireAccessibleHomes);
    }
}

public sealed class SftpUser : INotifyPropertyChanged
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string HomeDirectory { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHomeDirectoryMissing { get; private set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string HomeDirectoryHint => IsHomeDirectoryMissing
        ? $"Missing or unavailable home folder: {HomeDirectory}. Edit this user to select an existing folder."
        : HomeDirectory;
    public void RefreshHomeDirectoryStatus() => SetHomeDirectoryAvailability(Directory.Exists(HomeDirectory));
    internal void SetHomeDirectoryAvailability(bool available)
    {
        if (IsHomeDirectoryMissing != !available)
        {
            IsHomeDirectoryMissing = !available;
            PropertyChanged?.Invoke(this, new(nameof(IsHomeDirectoryMissing)));
        }
        PropertyChanged?.Invoke(this, new(nameof(HomeDirectoryHint)));
    }
    public bool PasswordAuthenticationEnabled { get; set; } = true;
    public List<string> AuthorizedKeys { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public string AuthenticationLabel => PasswordAuthenticationEnabled ? (AuthorizedKeys.Count > 0 ? "Password + key" : "Password") : "Public key";
    public bool ReadOnly { get; set; }
    public bool Enabled { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore]
    public string AccessLabel => ReadOnly ? "Read only" : "Read & write";
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusLabel => Enabled ? "Enabled" : "Disabled";

    public void Validate(bool requireAccessibleHomes = true)
    {
        if (Username == null || !Regex.IsMatch(Username, "^[a-zA-Z0-9._-]{1,64}$"))
            throw new ArgumentException("Username must contain 1–64 letters, numbers, dots, underscores, or hyphens.");
        if (string.IsNullOrEmpty(HomeDirectory) || !Path.IsPathFullyQualified(HomeDirectory))
            throw new ArgumentException($"Choose an absolute home folder for {Username}.");
        if (requireAccessibleHomes) RefreshHomeDirectoryStatus();
        if (requireAccessibleHomes && Enabled && IsHomeDirectoryMissing)
            throw new ArgumentException($"The home folder for '{Username}' is missing or unavailable: {HomeDirectory}. Choose an existing folder or disable this account.");
        if (requireAccessibleHomes && Enabled) FolderGuard.CheckRoot(HomeDirectory);
        if (PasswordAuthenticationEnabled && string.IsNullOrEmpty(PasswordHash)) throw new ArgumentException($"Set a password for {Username} or disable password authentication.");
        if (AuthorizedKeys == null || AuthorizedKeys.Count > 16) throw new ArgumentException("Use at most 16 public keys per account.");
        foreach (var key in AuthorizedKeys) PublicKeys.Parse(key);
        if (!PasswordAuthenticationEnabled && AuthorizedKeys.Count == 0) throw new ArgumentException($"Add a public key or enable password authentication for {Username}.");
    }
}

public static class Passwords
{
    private const int Iterations = 600_000;
    public static string Hash(string password)
    {
        if (password.Length < 12 || password.Length > 1024)
            throw new ArgumentException("Use a password between 12 and 1024 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        if (password.Length > 1024) return false;
        try
        {
            var parts = stored.Split(':');
            if (parts.Length != 3 || !int.TryParse(parts[0], out var rounds) || rounds is < 100_000 or > 1_000_000) return false;
            var expected = Convert.FromBase64String(parts[2]);
            if (expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), rounds, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }
    }
}

public sealed class SettingsStore(string dataDirectory)
{
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ServerSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        return JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(SettingsPath), JsonOptions)
            ?? throw new InvalidDataException("The settings file is empty. Restore a valid settings file.");
    }

    public void Save(ServerSettings settings, bool requireAccessibleHomes = true)
    {
        settings.Validate(requireAccessibleHomes);
        Directory.CreateDirectory(DataDirectory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }
}


