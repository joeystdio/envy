using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Envy.Core;

namespace Envy.Cli;

/// <summary>
/// The envy command. Everything that reads or changes settings goes to the running app, which it starts when needed;
/// help, reload and exec run here. Output never contains secret values.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Agents read the output through a pipe, and expect UTF-8 there. A console keeps its own code page.
        if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

        var launcher = ReadLauncherInfo();
        var paths = new AppPaths(launcher?.Home);
        switch (args.FirstOrDefault())
        {
            case "exec":
                return Exec(args.Skip(1).ToList(), paths);
            case "reload":
                Console.WriteLine("A program can't change the environment of the shell that started it. In a terminal connected to Envy, envy reload is built in; anywhere else, run commands with: envy exec -- <command>");
                return 0;
            case "help" or "--help" or "-h":
                Console.WriteLine(EnvyCommand.HelpText);
                return 0;
            case "--version" or "version":
                var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
                Console.WriteLine($"envy {version.Split('+')[0]}");
                return 0;
            default:
                return Send(args, paths, AppPath(launcher));
        }
    }

    // Talking to the app

    static int Send(string[] arguments, AppPaths paths, string? appPath)
    {
        var request = new CommandRequest
        {
            Arguments = [.. arguments],
            Context = new RequestContext
            {
                Directories = WorkingDirectories(),
                Loaded = (Environment.GetEnvironmentVariable("__ENVY_VARS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            },
        };
        var name = CommandPipe.Name(paths);

        CommandResponse response;
        try
        {
            response = CommandPipe.Send(name, request, TimeSpan.FromMilliseconds(300), connected: AllowForeground);
        }
        catch (CommandPipeException e) when (e.Failure == PipeFailure.NotRunning)
        {
            if (!LaunchApp(appPath, paths))
                return Offline(request, paths, "Envy isn't running and couldn't be started. Open it once from the Start menu.");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                try
                {
                    response = CommandPipe.Send(name, request, TimeSpan.FromSeconds(1), connected: AllowForeground);
                    break;
                }
                catch (CommandPipeException again) when (again.Failure == PipeFailure.NotRunning && DateTime.UtcNow < deadline)
                {
                }
                catch (CommandPipeException)
                {
                    return Fail("Envy started but didn't answer. Open Envy, check that it shows no problem, and try again.");
                }
            }
        }
        catch (CommandPipeException e) when (e.Failure == PipeFailure.Blocked)
        {
            return Offline(request, paths,
                "This process isn't allowed to reach Envy, probably because a coding agent's sandbox blocks it. " +
                "Run the same command again with permission to run outside the sandbox, or ask the user to run it.");
        }
        catch (CommandPipeException e)
        {
            return Fail($"Couldn't reach Envy ({e.Message}).");
        }

        if (response.Activate) AllowForeground(response.ProcessID);
        if (response.Output.Length > 0) Console.WriteLine(response.Output);
        if (response.Error is { } error) Console.Error.WriteLine($"envy: {error}");
        return response.ExitCode;
    }

    /// <summary>
    /// When the app can't be reached, reads still work from the settings file, which holds no secret values; changes
    /// can't, and get <paramref name="reason"/> instead.
    /// </summary>
    static int Offline(CommandRequest request, AppPaths paths, string reason)
    {
        EnvyCommand command;
        try
        {
            command = EnvyCommand.Parse(request.Arguments, request.Context.Directories.FirstOrDefault() ?? paths.Home, paths.Home);
        }
        catch (EnvyUsageException e)
        {
            Console.Error.WriteLine($"envy: {e.Message}");
            return 2;
        }
        if (!command.IsReadOnly) return Fail(reason);
        EnvConfig config;
        try { config = ConfigFile.Read(paths.ConfigFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return Fail(reason); }
        // The file leaves secret values out; a placeholder lists them as secrets rather than as empty.
        foreach (var v in config.Scopes.SelectMany(p => p.Variables).Where(v => v.IsSecret)) v.Value = "•";
        try
        {
            Console.WriteLine(command.Run(config, request.CommandContext, paths.Home).Output);
        }
        catch (EnvyUsageException e)
        {
            return Fail(e.Message);
        }
        Console.Error.WriteLine("envy: read from Envy's settings file, because Envy itself can't be reached from here.");
        return 0;
    }

    /// <summary>
    /// Starts the app in the notification area, without a window. Through the shell, so it doesn't inherit this
    /// process's output (a caller reading it would wait for the app to quit) or keep the caller's folder in use.
    /// </summary>
    static bool LaunchApp(string? appPath, AppPaths paths)
    {
        if (appPath is null || !File.Exists(appPath)) return false;
        try
        {
            if (paths.IsSandbox) Environment.SetEnvironmentVariable("ENVY_HOME", paths.Home);
            using var process = Process.Start(new ProcessStartInfo(appPath, "--background")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(appPath)!,
            });
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The working folder: the Git Bash form ($PWD, which keeps the name of a linked folder) when that's really where we
    /// are, then the process's own.
    /// </summary>
    static List<string> WorkingDirectories()
    {
        var physical = Environment.CurrentDirectory;
        var output = new List<string>();
        if (Environment.GetEnvironmentVariable("PWD") is { Length: > 0 } pwd && (pwd.StartsWith('/') || PathStyle.IsWindowsPath(pwd)))
        {
            var windows = PathStyle.FromPosix(pwd).Replace('/', '\\');
            if (FolderMatch.Same(windows, physical)) output.Add(PathStyle.TrimTrailing(windows));
        }
        if (!output.Contains(physical, StringComparer.OrdinalIgnoreCase)) output.Add(physical);
        return output;
    }

    // exec

    /// <summary>
    /// Runs a command with what Envy loads in this folder right now: the caller's environment with Envy's earlier changes
    /// put back, then the current set applied, as a shell hook would. Values never pass through this process's output.
    /// </summary>
    static int Exec(List<string> command, AppPaths paths)
    {
        if (command.FirstOrDefault() == "--") command.RemoveAt(0);
        if (command.Count == 0) return Fail("Usage: envy exec -- COMMAND [ARGS...]");
        var output = new ShellOutput(paths.ShellDirectory);
        ExecData? data;
        try { data = JsonSerializer.Deserialize(File.ReadAllText(output.ExecVars), EnvyJson.Default.ExecData); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { data = null; }
        if (data is null) return Fail("Envy hasn't written its shell files yet. Open Envy once, then try again.");

        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string key && e.Value is string value) current[key] = value;
        var environment = ExecEnvironment.Compute(data, WorkingDirectories(), current, paths.Home);

        ProcessStartInfo start;
        try { start = ExecProcess.StartInfo(command, environment, Environment.CurrentDirectory); }
        catch (EnvyUsageException e) { return Fail(e.Message); }

        // Ctrl+C reaches the command too, since it shares the console; this process waits for it to finish.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        try
        {
            using var process = Process.Start(start);
            if (process is null) return Fail($"Couldn't run {command[0]}.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return Fail($"Couldn't run {command[0]} ({e.Message}).");
        }
    }

    // Helpers

    /// <summary>envy.json next to this file, written by the app: where it is, and its ENVY_HOME sandbox.</summary>
    static LauncherInfo? ReadLauncherInfo()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "envy.json");
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), EnvyJson.Default.LauncherInfo) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The app named in envy.json (only that one, so a sandbox never starts another install), or without envy.json, the
    /// Envy.exe this file came with (cli\ next to it) or the one in the usual install folder.
    /// </summary>
    static string? AppPath(LauncherInfo? launcher)
    {
        var candidates = launcher is not null
            ? new[] { launcher.App }
            : new string?[]
            {
                Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) ?? "", "Envy.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Envy", "Envy.exe"),
            };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    /// <summary>Lets the app bring its window to the front, which Windows allows the process you're typing in to grant.</summary>
    static void AllowForeground(int processId) => AllowSetForegroundWindow(processId);

    static int Fail(string message)
    {
        Console.Error.WriteLine($"envy: {message}");
        return 1;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern bool AllowSetForegroundWindow(int processId);
}
