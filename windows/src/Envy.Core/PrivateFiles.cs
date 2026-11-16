using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Envy.Core;

/// <summary>Writes files only the current user can read: an owner-only ACL on Windows, mode 600/700 elsewhere.</summary>
public static class PrivateFiles
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    public static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Creates <paramref name="directory"/> if needed and makes it (and what's created inside) private.</summary>
    public static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) RestrictDirectory(directory);
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Replaces <paramref name="path"/> in one step: written next to it, then moved over it. A shell reading the file
    /// at that moment can briefly keep it from being replaced on Windows, so that is retried.
    /// </summary>
    public static void AtomicWrite(string path, string text, Encoding? encoding = null)
    {
        var bytes = (encoding ?? Utf8).GetPreamble().Concat((encoding ?? Utf8).GetBytes(text)).ToArray();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(25);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { }
                throw;
            }
        }
    }

    /// <summary>Whether only the current user (and nobody else) has access to <paramref name="path"/>.</summary>
    public static bool IsPrivate(string path)
    {
        if (OperatingSystem.IsWindows()) return IsPrivateWindows(path);
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0;
    }

    [SupportedOSPlatform("windows")]
    static void RestrictDirectory(string directory)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        // Files that were there before inherit the new rules once their own explicit ones are dropped.
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new FileInfo(file).SetAccessControl(fileSecurity);
        }
    }

    [SupportedOSPlatform("windows")]
    static bool IsPrivateWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        AuthorizationRuleCollection rules = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            : new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        return rules.Cast<FileSystemAccessRule>().All(r => r.AccessControlType == AccessControlType.Deny || r.IdentityReference.Equals(user));
    }
}
