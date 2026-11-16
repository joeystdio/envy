using System.Text;

namespace Envy.Core;

/// <summary>
/// Adds or removes the marked block that loads Envy's hook from a startup file (~/.bashrc or a PowerShell profile).
/// Keeps the file's encoding, byte order mark and line endings, writes through symlinks, and leaves a copy of the
/// previous file at &lt;name&gt;.envy-backup.
/// </summary>
public static class StartupFiles
{
    public const string BeginMarker = "# >>> Envy >>>";
    public const string EndMarker = "# <<< Envy <<<";
    const string Comment = "# Loads the environment variables managed by Envy. Remove it from Envy > Shell Integration.";

    public static string BashLine(string initShPosix) =>
        $"[ -r {BashScript.SingleQuoted(initShPosix)} ] && . {BashScript.SingleQuoted(initShPosix)}";

    /// <summary>The line for PowerShell; <paramref name="initPs1"/> is a PowerShell expression such as a quoted path.</summary>
    public static string PowerShellLine(string initPs1) =>
        $"if (Test-Path -LiteralPath {initPs1}) {{ . {initPs1} }}";

    public static string Snippet(string line, string newline = "\n") =>
        string.Join(newline, BeginMarker, Comment, line, EndMarker);

    public static bool IsInstalled(string path) =>
        ReadText(Resolve(path)) is { } file && Lines(file.Text).Any(l => l.Trim() == BeginMarker);

    /// <summary>Appends the block, creating the file (and its folder) if needed.</summary>
    public static void Install(string path, string line, bool preferCrLf = false)
    {
        if (IsInstalled(path)) return;
        var target = Resolve(path);
        var existing = ReadText(target);
        var newline = existing is { } e && e.Text.Contains("\r\n") || existing is null && preferCrLf ? "\r\n" : "\n";
        var text = existing?.Text ?? "";
        var addition = new StringBuilder();
        if (text.Length > 0)
        {
            Backup(target);
            addition.Append(text.EndsWith('\n') ? newline : newline + newline);
        }
        addition.Append(Snippet(line, newline)).Append(newline);
        // New PowerShell profiles get a byte order mark so Windows PowerShell reads them as UTF-8.
        var encoding = existing?.Encoding ?? (preferCrLf ? PrivateFiles.Utf8Bom : PrivateFiles.Utf8);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        WriteInPlace(target, text + addition, encoding);
    }

    public static void Uninstall(string path)
    {
        var target = Resolve(path);
        if (ReadText(target) is not { } file) return;
        var newline = file.Text.Contains("\r\n") ? "\r\n" : "\n";
        var all = Lines(file.Text);
        var start = all.FindIndex(l => l.Trim() == BeginMarker);
        if (start < 0) return;
        var end = all.FindIndex(start, l => l.Trim() == EndMarker);
        if (end < 0) end = start;
        Backup(target);
        // Also drop the blank line install put in front of the block.
        var from = start > 0 && all[start - 1].Length == 0 ? start - 1 : start;
        all.RemoveRange(from, end - from + 1);
        WriteInPlace(target, string.Join(newline, all), file.Encoding);
    }

    /// <summary>Removes whole lines (1-based) from a file, keeping a backup first. Used to take secrets out of startup files.</summary>
    public static void RemoveLines(string path, IEnumerable<int> lineNumbers)
    {
        var target = Resolve(path);
        if (ReadText(target) is not { } file) return;
        var newline = file.Text.Contains("\r\n") ? "\r\n" : "\n";
        var drop = lineNumbers.ToHashSet();
        var all = Lines(file.Text);
        Backup(target);
        var kept = all.Where((_, i) => !drop.Contains(i + 1));
        WriteInPlace(target, string.Join(newline, kept), file.Encoding);
    }

    public static string BackupPath(string path) => Resolve(path) + ".envy-backup";

    /// <summary>The real file behind a symlink, so dotfile managers keep working.</summary>
    public static string Resolve(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) return target.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return path;
    }

    public sealed record FileText(string Text, Encoding Encoding);

    /// <summary>The text and the encoding it was stored in (UTF-8 or UTF-16 with a byte order mark, or plain UTF-8).</summary>
    public static FileText? ReadText(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return new(PrivateFiles.Utf8.GetString(bytes, 3, bytes.Length - 3), PrivateFiles.Utf8Bom);
        if (bytes is [0xFF, 0xFE, ..]) return new(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes is [0xFE, 0xFF, ..]) return new(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);
        return new(PrivateFiles.Utf8.GetString(bytes), PrivateFiles.Utf8);
    }

    /// <summary>Writing in place keeps the file's permissions.</summary>
    static void WriteInPlace(string path, string text, Encoding encoding)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var preamble = encoding.GetPreamble();
        stream.Write(preamble);
        stream.Write(encoding.GetBytes(text));
    }

    static void Backup(string path)
    {
        var copy = path + ".envy-backup";
        File.Copy(path, copy, overwrite: true);
    }

    static List<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').ToList();
}
