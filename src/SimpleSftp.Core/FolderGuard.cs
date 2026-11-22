namespace SimpleSftp.Core;

/// <summary>Rejects traversal, device paths, alternate streams, and reparse points.</summary>
public sealed class FolderGuard
{
    private readonly string root;
    public FolderGuard(string rootPath)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        CheckRoot(root);
    }

    public static void CheckRoot(string rootPath)
    {
        for (var part = new DirectoryInfo(rootPath); part != null; part = part.Parent)
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Home folders and their parents cannot be symbolic links or junctions.");
    }

    public string Check(string path)
    {
        if (path.Length > 4096 || path.Contains('\0') || path.Contains('\\') || path.Contains(':')) Deny();
        var parts = path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == "." || part == "..") continue;
            if (part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) Deny();
            var name = part.Split('.')[0].TrimEnd(' ');
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9\u00b9\u00b2\u00b3]|LPT[0-9\u00b9\u00b2\u00b3])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) Deny();
        }
        var relative = path.TrimStart('/');
        if (relative is "~" or "~/") relative = "";
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.Equals(root, comparison) && !full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison)) Deny();
        CheckRoot(root);
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) Deny();
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static void Deny() => throw new UnauthorizedAccessException("Path is outside the allowed folder or uses an unsupported name or link.");
}


