using System.Diagnostics;
using System.Text;
using Envy.Core;

namespace Envy.Core.Tests;

/// <summary>A temporary folder per test, removed afterwards.</summary>
public abstract class TempDirectoryTest : IDisposable
{
    protected readonly string Root;

    protected TempDirectoryTest()
    {
        Root = LongPath.Of(Path.Combine(Path.GetTempPath(), "envy-tests-" + Guid.NewGuid().ToString("N")[..8]));
        Directory.CreateDirectory(Root);
        Root = LongPath.Of(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    protected string Write(string text, string relativePath)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    protected string Folder(string name)
    {
        var path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        return path;
    }

    protected static EnvConfig GlobalOnly(params EnvVar[] vars) => new(new Project("Global", variables: vars, id: Project.GlobalId));
}

/// <summary>Runs real shells: bash (Git Bash on Windows), PowerShell 7, Windows PowerShell and cmd.</summary>
public static class Shells
{
    public static string? Bash => ShellLocator.FindGitBash(Environment.GetEnvironmentVariable("PATH") ?? "");
    public static string? Pwsh => ShellLocator.FindPowerShell7(Environment.GetEnvironmentVariable("PATH") ?? "");
    public static string? WindowsPowerShell => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
        : null;
    public static string? Cmd => OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : null;

    /// <summary>The PowerShells available here: pwsh, plus powershell.exe on Windows.</summary>
    public static IEnumerable<string> PowerShells => new[] { Pwsh, WindowsPowerShell }.Where(p => p is not null && File.Exists(p))!;

    public static string Require(string? shell, string name)
    {
        if (shell is null || !File.Exists(shell)) Assert.Skip($"{name} isn't installed here.");
        return shell!;
    }

    /// <summary>A path as the given shell spells it.</summary>
    public static string ForBash(string path) => PathStyle.ToPosix(path);

    public static string RunBash(string script, string shellDir, string cwd, Dictionary<string, string>? env = null, string? bash = null)
    {
        bash ??= Require(Bash, "bash");
        var full = $". \"$ENVY_DIR/init.sh\"\n{script}";
        return Run(bash, ["--norc", "--noprofile", "-c", full], cwd, BaseEnvironment(env, ForBash(shellDir)));
    }

    public static string RunPowerShell(string exe, string script, string shellDir, string cwd, Dictionary<string, string>? env = null)
    {
        var full = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n"
            + $". (Join-Path $env:ENVY_DIR 'init.ps1')\n{script}";
        return Run(exe, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(full))],
            cwd, BaseEnvironment(env, shellDir));
    }

    /// <summary>Runs a batch file that first calls init.cmd, as the AutoRun value would. Output is read as UTF-8.</summary>
    public static string RunCmd(string script, string shellDir, string cwd, Dictionary<string, string>? env = null)
    {
        var cmd = Require(Cmd, "cmd");
        var file = Path.Combine(Path.GetTempPath(), $"envy-test-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(file, ("@chcp 65001 >nul\r\n@call \"%ENVY_DIR%\\init.cmd\"\r\n" + script).ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
        try
        {
            return Run(cmd, ["/d", "/c", file], cwd, BaseEnvironment(env, shellDir), cmdLine: true);
        }
        finally
        {
            File.Delete(file);
        }
    }

    static Dictionary<string, string> BaseEnvironment(Dictionary<string, string>? extra, string shellDir)
    {
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                var key = (string)e.Key;
                if (key.StartsWith("__ENVY", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ENVY_", StringComparison.OrdinalIgnoreCase)) continue;
                env[key] = (string)e.Value!;
            }
        }
        else
        {
            env["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            env["PATH"] = "/usr/bin:/bin:/usr/local/bin:/opt/homebrew/bin";
        }
        env["ENVY_DIR"] = shellDir;
        if (extra is not null) foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    public static string Run(string exe, IEnumerable<string> args, string cwd, Dictionary<string, string> env, bool cmdLine = false)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (cmdLine) start.Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        else foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment.Clear();
        foreach (var (k, v) in env) start.Environment[k] = v;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60_000)) { process.Kill(true); throw new TimeoutException($"{exe} took too long"); }
        return (stdout + stderr.Result).Replace("\r\n", "\n");
    }
}
