using System.IO;
using Microsoft.Win32;

namespace SimpleSftp.Desktop;

internal interface IStartupRegistration
{
    string? Read();
    void Write(string? command);
}

internal sealed class WindowsStartup : IStartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SimpleSftp";
    public string? Read() { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(ValueName) as string; }
    public void Write(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (command == null) key.DeleteValue(ValueName, false); else key.SetValue(ValueName, command, RegistryValueKind.String);
    }
    internal static string BuildCommand(string executable, string dataDirectory)
    {
        var exe = Path.GetFullPath(executable); var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (exe.Contains('"') || data.Contains('"') || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid startup executable or data folder.");
        // Avoid the Windows quoted-argument trailing-backslash escape for a drive root.
        if (data.EndsWith('\\')) data += ".";
        var command = $"\"{exe}\" --startup --data-dir \"{data}\"";
        if (command.Length > 260) throw new ArgumentException("The startup command is too long. Move the app or data folder to a shorter path.");
        return command;
    }
}

internal sealed record LaunchOptions(string DataDirectory, bool WindowsStartup)
{
    internal static LaunchOptions Parse(string[] args, string defaultData)
    {
        var data = defaultData; var startup = false; var hasData = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--startup" && !startup) startup = true;
            else if (args[i] == "--data-dir" && !hasData && i + 1 < args.Length) { data = args[++i]; hasData = true; }
            else throw new ArgumentException("Usage: SimpleSftp.Desktop.exe [--startup] [--data-dir <folder>]");
        }
        return new(Path.GetFullPath(data), startup);
    }
}
