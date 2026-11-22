using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimpleSftp.Core;

public static class BackupService
{
    private static readonly byte[] Magic = "SFTPBAK1"u8.ToArray();
    private const int MaxLength = 8 * 1024 * 1024;
    private sealed record BackupData(int Version, ServerSettings Settings, string? HostKey);

    public static void Export(SettingsStore store, string destination, string password)
    {
        if (password.Length is < 12 or > 1024) throw new ArgumentException("Use a backup password between 12 and 1024 characters.");
        var settings = store.Load(); settings.Validate(false);
        var keyPath = Path.Combine(store.DataDirectory, "host-key.pem");
        var hostKey = File.Exists(keyPath) ? File.ReadAllText(keyPath) : null;
        ValidateHostKey(hostKey);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new BackupData(1, settings, hostKey));
        if (plain.Length > MaxLength) throw new InvalidDataException("The configuration is too large to back up.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16]; var cipher = new byte[plain.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, cipher, tag, Magic);
            var output = new byte[52 + cipher.Length];
            Magic.CopyTo(output, 0); salt.CopyTo(output, 8); nonce.CopyTo(output, 24); tag.CopyTo(output, 36); cipher.CopyTo(output, 52);
            var target = Path.GetFullPath(destination);
            if (target.StartsWith(Path.TrimEndingDirectorySeparator(store.DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a backup location outside the app's private data folder.");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, output); File.Move(temporary, target, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    public static ServerSettings Restore(SettingsStore store, string source, string password)
    {
        var info = new FileInfo(source);
        if (info.Length < 52 || info.Length > MaxLength + 52) throw new InvalidDataException("This is not a valid Simple SFTP backup.");
        if (password.Length is < 12 or > 1024) throw new ArgumentException("Enter the backup password (12 to 1024 characters).");
        var input = File.ReadAllBytes(source);
        if (!input.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException("This is not a supported Simple SFTP backup.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, input.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[input.Length - 52];
        try
        {
            try { using var aes = new AesGcm(key, 16); aes.Decrypt(input.AsSpan(24, 12), input.AsSpan(52), input.AsSpan(36, 16), plain, Magic); }
            catch (CryptographicException ex) { throw new InvalidDataException("The backup password is incorrect or the backup was modified.", ex); }
            var backup = JsonSerializer.Deserialize<BackupData>(plain) ?? throw new InvalidDataException("The backup is empty.");
            if (backup.Version != 1 || backup.Settings == null) throw new InvalidDataException("Unsupported backup version.");
            backup.Settings.Validate(false); ValidateHostKey(backup.HostKey);
            // Imported accounts may refer to another computer's folders. Never
            // start sharing or register Windows startup as a side effect of restore.
            backup.Settings.StartWithWindows = false;
            backup.Settings.AutoStartServer = false;
            ReplaceFiles(store, backup.Settings, backup.HostKey);
            return backup.Settings;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    private static void ValidateHostKey(string? pem)
    {
        if (pem == null) return;
        using var key = ECDsa.Create(); key.ImportFromPem(pem);
        var parameters = key.ExportParameters(true);
        if (key.KeySize != 256 || parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidDataException("Unsupported host identity in backup.");
    }

    private static void ReplaceFiles(SettingsStore store, ServerSettings settings, string? hostKey)
    {
        PrivateStorage.Prepare(store.DataDirectory);
        var stage = Path.Combine(store.DataDirectory, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var settingsPath = store.SettingsPath;
        var keyPath = Path.Combine(store.DataDirectory, "host-key.pem");
        var hadSettings = File.Exists(settingsPath); var hadKey = File.Exists(keyPath);
        var preserveRecovery = false;
        try
        {
            if (hadSettings) File.Copy(settingsPath, Path.Combine(stage, "old-settings"));
            if (hadKey) File.Copy(keyPath, Path.Combine(stage, "old-key"));
            File.WriteAllText(Path.Combine(stage, "settings"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            if (hostKey != null) File.WriteAllText(Path.Combine(stage, "key"), hostKey);
            var keyChanged = false;
            try
            {
                if (hostKey != null) { File.Move(Path.Combine(stage, "key"), keyPath, true); keyChanged = true; }
                else if (File.Exists(keyPath)) { File.Delete(keyPath); keyChanged = true; }
                // Settings are the final commit. Until this atomic replacement
                // succeeds, only the host key can have changed.
                File.Move(Path.Combine(stage, "settings"), settingsPath, true);
            }
            catch (Exception failure)
            {
                try
                {
                    if (keyChanged)
                    {
                        if (hadKey) File.Copy(Path.Combine(stage, "old-key"), keyPath, true);
                        else if (File.Exists(keyPath)) File.Delete(keyPath);
                    }
                }
                catch (Exception rollback)
                {
                    preserveRecovery = true;
                    throw new IOException("Restore failed. Protected recovery files were preserved in " + stage, new AggregateException(failure, rollback));
                }
                throw;
            }
        }
        finally
        {
            // Never delete recovery material if rollback itself failed.
            if (!preserveRecovery && Path.GetFullPath(stage).StartsWith(Path.TrimEndingDirectorySeparator(store.DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(stage, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
