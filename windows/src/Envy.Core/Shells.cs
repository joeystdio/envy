using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Envy.Core;

public enum ShellKind { PowerShell7, WindowsPowerShell, GitBash, Cmd }

/// <summary>A shell found on this PC.</summary>
public sealed record InstalledShell(ShellKind Kind, string Executable)
{
    public string Name => Kind switch
    {
        ShellKind.PowerShell7 => "PowerShell 7",
        ShellKind.WindowsPowerShell => "Windows PowerShell 5.1",
        ShellKind.GitBash => "Git Bash",
        _ => "Command Prompt",
    };
}

/// <summary>Finds the shells Envy supports. Looks in the usual install folders and on PATH, without starting anything.</summary>
public static class ShellLocator
{
    public static List<InstalledShell> Find(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        var found = new List<InstalledShell>();
        if (FindPowerShell7(path) is { } pwsh) found.Add(new(ShellKind.PowerShell7, pwsh));
        if (OperatingSystem.IsWindows())
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var windowsPowerShell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(windowsPowerShell)) found.Add(new(ShellKind.WindowsPowerShell, windowsPowerShell));
        }
        if (FindGitBash(path) is { } bash) found.Add(new(ShellKind.GitBash, bash));
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (File.Exists(cmd)) found.Add(new(ShellKind.Cmd, cmd));
        }
        return found;
    }

    public static string? FindPowerShell7(string path)
    {
        if (!OperatingSystem.IsWindows()) return OnPath("pwsh", path);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
            Path.Combine(programFiles, "PowerShell", "7-preview", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerShell", "pwsh.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? OnPath("pwsh.exe", path);
    }

    /// <summary>Git for Windows' bash.exe (not WSL's bash in System32, which runs Linux).</summary>
    public static string? FindGitBash(string path)
    {
        if (!OperatingSystem.IsWindows()) return File.Exists("/bin/bash") ? "/bin/bash" : OnPath("bash", path);
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git"),
        };
        // git.exe on PATH lives in <Git>\cmd or <Git>\bin.
        if (OnPath("git.exe", path) is { } git) roots.Insert(0, Path.GetDirectoryName(Path.GetDirectoryName(git)!)!);
        return roots.Select(r => Path.Combine(r, "bin", "bash.exe")).FirstOrDefault(File.Exists);
    }

    public static string? OnPath(string name, string path)
    {
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}

/// <summary>What a freshly opened shell looks like.</summary>
public sealed record ShellInspection(bool HookLoaded, List<string> Agents, string? ExecutionPolicy = null)
{
    /// <summary>Windows PowerShell doesn't run profile scripts at all under these policies.</summary>
    public bool ScriptsBlocked => ExecutionPolicy is "Restricted" or "AllSigned";
}

/// <summary>Runs short scripts in the user's own shells, the way a new terminal tab starts them.</summary>
public static class ShellProbe
{
    public static readonly (string Command, string Name)[] KnownAgents =
    [
        ("claude", "Claude Code"), ("codex", "Codex"), ("cursor-agent", "Cursor CLI"), ("gemini", "Gemini CLI"),
        ("aider", "Aider"), ("opencode", "opencode"), ("amp", "Amp"), ("goose", "Goose"), ("copilot", "Copilot CLI"),
    ];

    /// <summary>
    /// Starts <paramref name="shell"/> as a terminal would (profile and startup files included) and reports whether
    /// Envy's hook loaded and which coding agents are on its PATH. Null when it didn't get that far in time.
    /// <paramref name="profile"/> dot-sources that file instead of the real profile (sandboxes and tests);
    /// <paramref name="home"/> sets HOME for bash; <paramref name="autoRun"/> stands in for cmd's AutoRun value.
    /// </summary>
    public static ShellInspection? Inspect(InstalledShell shell, string? profile = null, string? home = null, string? autoRun = null, TimeSpan? timeout = null)
    {
        var commands = KnownAgents.Select(a => a.Command).ToList();
        switch (shell.Kind)
        {
            case ShellKind.PowerShell7 or ShellKind.WindowsPowerShell:
            {
                var load = profile is null ? "" : $"if (Test-Path -LiteralPath {PowerShellScript.Quoted(profile)}) {{ . {PowerShellScript.Quoted(profile)} }}\n";
                var script = load + $$"""
                    $o = $env:ENVY_PROBE_OUT
                    $lines = @(if (Get-Command __envy_hook -ErrorAction Ignore) { 'hook:yes' } else { 'hook:no' })
                    $lines += "policy:$(Get-ExecutionPolicy)"
                    foreach ($c in @({{string.Join(", ", commands.Select(PowerShellScript.Quoted))}})) {
                        if (Get-Command $c -CommandType Application, ExternalScript -ErrorAction Ignore) { $lines += "agent:$c" }
                    }
                    [IO.File]::WriteAllLines($o, [string[]]$lines)
                    """;
                var args = new List<string> { "-NoLogo", "-NonInteractive" };
                if (profile is not null) args.Add("-NoProfile");
                args.AddRange(["-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
                return Parse(Run(shell, args, null, timeout));
            }
            case ShellKind.GitBash:
            {
                var script = $"""
                    if type __envy_hook >/dev/null 2>&1; then echo hook:yes; else echo hook:no; fi > "$ENVY_PROBE_OUT"
                    for c in {string.Join(" ", commands)}; do command -v "$c" >/dev/null 2>&1 && echo "agent:$c" >> "$ENVY_PROBE_OUT"; done
                    :
                    """;
                var env = home is null ? null : new Dictionary<string, string> { ["HOME"] = home };
                return Parse(Run(shell, ["--login", "-i", "-c", script], env, timeout, posixOut: true));
            }
            default:
            {
                // AutoRun runs before the /c command, so its variables are there; without it Envy isn't.
                var check = "if defined __envy_dir (echo hook:yes> \"%ENVY_PROBE_OUT%\") else (echo hook:no> \"%ENVY_PROBE_OUT%\")";
                var args = autoRun is null ? new List<string> { "/c", check } : ["/d", "/c", autoRun + " & " + check];
                return Parse(Run(shell, args, null, timeout, cmdLine: true));
            }
        }
    }

    /// <summary>
    /// Leaves PSModulePath out for Windows PowerShell. Envy started from PowerShell 7 (by envy open, say) inherits its
    /// module path, with which Windows PowerShell can't load its own modules, so Get-ExecutionPolicy and
    /// Set-ExecutionPolicy fail. Without the variable it builds its default path, as a new window does.
    /// </summary>
    public static void UseDefaultModulePath(ProcessStartInfo start, ShellKind kind)
    {
        if (kind == ShellKind.WindowsPowerShell) start.Environment.Remove("PSModulePath");
    }

    static ShellInspection? Parse(string? output)
    {
        if (output is null) return null;
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (!lines.Contains("hook:yes") && !lines.Contains("hook:no")) return null;
        var policy = lines.FirstOrDefault(l => l.StartsWith("policy:"))?["policy:".Length..];
        return new ShellInspection(
            lines.Contains("hook:yes"),
            KnownAgents.Where(a => lines.Contains("agent:" + a.Command)).Select(a => a.Name).ToList(),
            policy);
    }

    /// <summary>
    /// Runs the shell with output going to a file rather than a pipe: startup files often print banners, and a
    /// background job they start could keep a pipe open long after the shell exits.
    /// </summary>
    static string? Run(InstalledShell shell, List<string> args, Dictionary<string, string>? env, TimeSpan? timeout, bool posixOut = false, bool cmdLine = false)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"envy-probe-{Guid.NewGuid():N}");
        File.WriteAllText(outFile, "");
        try
        {
            var start = new ProcessStartInfo(shell.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = env?.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (cmdLine && OperatingSystem.IsWindows()) start.Arguments = string.Join(" ", args); // cmd parses its own command line
            else foreach (var a in args) start.ArgumentList.Add(a);
            foreach (var key in start.Environment.Keys.ToList())
                if (key.StartsWith("__ENVY", StringComparison.OrdinalIgnoreCase) || key.Equals("ENVY_DIR", StringComparison.OrdinalIgnoreCase))
                    start.Environment.Remove(key);
            UseDefaultModulePath(start, shell.Kind);
            if (env is not null) foreach (var (k, v) in env) start.Environment[k] = v;
            start.Environment["ENVY_PROBE_OUT"] = posixOut ? PathStyle.ToPosix(outFile) : outFile;

            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(20)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return File.ReadAllText(outFile);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(outFile); } catch (IOException) { }
        }
    }
}

/// <summary>Long names for 8.3 short paths (C:\Users\RUNNER~1 → C:\Users\runneradmin), which shells may show either way.</summary>
public static partial class LongPath
{
    public static string Of(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~')) return path;
        return Windows(path);
    }

    [SupportedOSPlatform("windows")]
    static string Windows(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathName(path, buffer, buffer.Length);
        return length > 0 && length < buffer.Length ? new string(buffer, 0, (int)length) : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathName(string shortPath, [Out] char[] longPath, int bufferLength);
}
