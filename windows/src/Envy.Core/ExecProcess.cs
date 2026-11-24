using System.Diagnostics;
using System.Text;

namespace Envy.Core;

/// <summary>
/// How envy exec starts a command on Windows, which has no exec: the program is looked up on the new PATH (with PATHEXT,
/// as cmd does), batch files run through cmd.exe with arguments quoted for it, and PowerShell scripts through PowerShell.
/// </summary>
public static class ExecProcess
{
    /// <summary>The start info for <paramref name="command"/> with <paramref name="environment"/> as its whole environment.</summary>
    public static ProcessStartInfo StartInfo(IReadOnlyList<string> command, IReadOnlyDictionary<string, string> environment, string workingDirectory)
    {
        var program = Resolve(command[0], environment, workingDirectory)
            ?? throw new EnvyUsageException($"{command[0]} isn't a program on PATH. To run a shell command, use envy exec -- pwsh -c \"…\" or envy exec -- cmd /c \"…\".");
        var start = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = workingDirectory };
        start.Environment.Clear();
        foreach (var (key, value) in environment) start.Environment[key] = value;

        var extension = Path.GetExtension(program).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
        {
            start.FileName = environment.GetValueOrDefault("ComSpec") is { Length: > 0 } comSpec && File.Exists(comSpec)
                ? comSpec
                : Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = "/d /s /c \"" + BatchCommandLine(program, command.Skip(1)) + "\"";
        }
        else if (extension == ".ps1")
        {
            start.FileName = Resolve("pwsh", environment, workingDirectory) ?? Resolve("powershell", environment, workingDirectory) ?? "powershell.exe";
            foreach (var a in new[] { "-NoLogo", "-NoProfile", "-File", program }.Concat(command.Skip(1))) start.ArgumentList.Add(a);
        }
        else
        {
            start.FileName = program;
            foreach (var a in command.Skip(1)) start.ArgumentList.Add(a);
        }
        return start;
    }

    /// <summary>
    /// The full path of <paramref name="name"/>: as given when it names a folder, otherwise the first match on PATH, trying
    /// the extensions in PATHEXT (and .ps1 last). Null when there's none.
    /// </summary>
    public static string? Resolve(string name, IReadOnlyDictionary<string, string> environment, string workingDirectory)
    {
        var extensions = (environment.GetValueOrDefault("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(".PS1")
            .ToList();
        IEnumerable<string> folders = name.Contains('\\') || name.Contains('/')
            ? [""]
            : (environment.GetValueOrDefault("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var folder in folders)
        {
            string basePath;
            try { basePath = Path.GetFullPath(Path.Combine(folder.Trim('"'), name), workingDirectory); }
            catch (ArgumentException) { continue; }
            var hasExtension = extensions.Any(e => basePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (hasExtension && File.Exists(basePath)) return basePath;
            foreach (var extension in extensions.Select(e => e.ToLowerInvariant()))
                if (File.Exists(basePath + extension)) return basePath + extension;
        }
        return null;
    }

    /// <summary>
    /// The command line for cmd.exe /s /c "…": every argument quoted when it holds anything cmd treats specially, quotes
    /// inside doubled, and % written so cmd can't expand it. Line breaks can't be passed to a batch file at all.
    /// </summary>
    public static string BatchCommandLine(string program, IEnumerable<string> arguments)
    {
        var b = new StringBuilder();
        b.Append('"').Append(program).Append('"');
        foreach (var argument in arguments)
        {
            if (argument.Contains('\n') || argument.Contains('\r'))
                throw new EnvyUsageException("A batch file can't take an argument with a line break.");
            b.Append(' ');
            var quote = argument.Length == 0 || argument.Any(c => c is ' ' or '\t' or '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')' or '%' or '!' or ',' or ';' or '=');
            if (quote) b.Append('"');
            foreach (var c in argument)
            {
                if (c == '"') b.Append("\"\"");
                else if (c == '%') b.Append("%%cd:~,%");
                else b.Append(c);
            }
            if (quote) b.Append('"');
        }
        return b.ToString();
    }
}
