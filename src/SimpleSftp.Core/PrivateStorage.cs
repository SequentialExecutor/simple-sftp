using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace SimpleSftp.Core;

public static class PrivateStorage
{
    public static void Prepare(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) RestrictWindows(directory);
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string directory)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot determine the current Windows account.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
    }
}

