using System.Text.RegularExpressions;

namespace Envy.Core;

/// <summary>Converts between Windows paths (C:\Users\me) and the POSIX form Git Bash uses (/c/Users/me).</summary>
public static partial class PathStyle
{
    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex DriveRegex();

    [GeneratedRegex(@"^/([A-Za-z])(?:/|$)")]
    private static partial Regex PosixDriveRegex();

    public static bool IsWindowsPath(string path) => DriveRegex().IsMatch(path) || path.StartsWith(@"\\");

    /// <summary>C:\Users\me becomes /c/Users/me and \\server\share becomes //server/share; POSIX paths stay as they are.</summary>
    public static string ToPosix(string path)
    {
        if (DriveRegex().IsMatch(path))
        {
            var rest = path[2..].Replace('\\', '/');
            if (!rest.StartsWith('/')) rest = "/" + rest;
            return "/" + char.ToLowerInvariant(path[0]) + rest;
        }
        if (path.StartsWith(@"\\")) return path.Replace('\\', '/');
        return path;
    }

    /// <summary>/c/Users/me becomes C:\Users\me and //server/share becomes \\server\share; anything else stays as it is.</summary>
    public static string FromPosix(string path)
    {
        var m = PosixDriveRegex().Match(path);
        if (m.Success)
        {
            var rest = path[2..].Replace('/', '\\');
            return char.ToUpperInvariant(m.Groups[1].Value[0]) + ":" + (rest.Length == 0 ? "\\" : rest);
        }
        if (path.StartsWith("//")) return path.Replace('/', '\\');
        return path;
    }

    /// <summary>The separator the path itself uses: backslash for Windows paths, slash otherwise.</summary>
    public static char SeparatorOf(string path) => IsWindowsPath(path) || (path.Contains('\\') && !path.Contains('/')) ? '\\' : '/';

    /// <summary>The path without trailing separators, keeping roots such as C:\ and / intact.</summary>
    public static string TrimTrailing(string path)
    {
        var p = path;
        while (p.Length > 1 && (p.EndsWith('\\') || p.EndsWith('/')))
        {
            if (p.Length == 3 && p[1] == ':') break; // C:\
            p = p[..^1];
        }
        return p;
    }

    /// <summary>The folder as a prefix that matches itself and everything below it.</summary>
    public static string AsPrefix(string folder)
    {
        var p = TrimTrailing(folder);
        var sep = SeparatorOf(p);
        return p.EndsWith(sep) ? p : p + sep;
    }

    /// <summary>
    /// The folder as given and with links (symlinks, junctions) resolved, since a shell's current folder can be either.
    /// </summary>
    public static List<string> Variants(string folder, string? home = null)
    {
        var expanded = ExpandHome(folder, home);
        var output = new List<string> { TrimTrailing(expanded) };
        foreach (var other in new[] { ResolveLinks(expanded), LongPath.Of(expanded) })
        {
            var trimmed = TrimTrailing(other);
            if (!output.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) output.Add(trimmed);
        }
        return output;
    }

    public static string ExpandHome(string path, string? home)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") return home;
        if (path.StartsWith("~/") || path.StartsWith("~\\")) return Path.Combine(home, path[2..]);
        return path;
    }

    /// <summary>Resolves every link along the path, so /var/x becomes /private/var/x and a junction its target.</summary>
    public static string ResolveLinks(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = ResolveLinks(target.FullName);
            }
            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>Length used to order folder projects: the deepest folder loads last.</summary>
    public static int Depth(Project p, string? home = null) =>
        p.Folders.Count == 0 ? 0 : p.Folders.Max(f => ExpandHome(f, home).Length);
}
