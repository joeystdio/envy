namespace Envy.Core;

/// <summary>
/// Writes the shell files into the shell directory (normally %LOCALAPPDATA%\Envy\shell, which only you can read):
/// a fixed hook and generated variables for each shell, the envy command for cmd, what envy exec loads, the state stamp
/// that makes open shells reload, and with <paramref name="commandSource"/>, the envy command in bin\.
/// </summary>
/// <param name="commandSource">The folder holding the app's own envy.exe, copied to bin\ when it changed.</param>
/// <param name="envyHome">The ENVY_HOME sandbox the envy command should use, written to bin\envy.json.</param>
public sealed class ShellOutput(string directory, string? appPath = null, string? home = null, string? commandSource = null, string? envyHome = null)
{
    public string Directory { get; } = directory;

    public string BashHook => Path.Combine(Directory, "init.sh");
    public string BashVars => Path.Combine(Directory, "vars.sh");
    public string PowerShellHook => Path.Combine(Directory, "init.ps1");
    public string PowerShellVars => Path.Combine(Directory, "vars.ps1");
    public string CmdInit => Path.Combine(Directory, "init.cmd");
    public string CmdEnvy => Path.Combine(Directory, "envy.cmd");
    public string CmdVars => Path.Combine(Directory, "vars.cmd");
    public string State => Path.Combine(Directory, "state");
    /// <summary>What envy exec loads, values included.</summary>
    public string ExecVars => Path.Combine(Directory, "vars.json");
    public string CommandDirectory => Path.Combine(Directory, "bin");
    public string Command => Path.Combine(CommandDirectory, "envy.exe");
    public string LauncherFile => Path.Combine(CommandDirectory, "envy.json");

    /// <summary>
    /// Brings the files up to date. The state stamp only changes when the variables did, so open shells reload only
    /// then. Returns whether they changed.
    /// </summary>
    public bool Write(EnvConfig config)
    {
        PrivateFiles.EnsurePrivateDirectory(Directory);
        RemoveLeftovers();

        // Windows PowerShell reads a .ps1 without a byte order mark in the ANSI code page, so they get one.
        WriteIfChanged(BashHook, BashScript.Hook(PathStyle.ToPosix(Directory), appPath is null ? null : PathStyle.ToPosix(appPath)), PrivateFiles.Utf8);
        WriteIfChanged(PowerShellHook, PowerShellScript.Hook(Directory, appPath), PrivateFiles.Utf8Bom);
        WriteIfChanged(CmdInit, CmdScript.Init(Directory), PrivateFiles.Utf8);
        WriteIfChanged(CmdEnvy, CmdScript.EnvyCommand(appPath), PrivateFiles.Utf8);
        if (commandSource is not null) InstallCommand(commandSource);

        var bash = BashScript.Vars(config, home: home);
        var powerShell = PowerShellScript.Vars(config, home: home);
        var cmd = CmdScript.Vars(config, home);
        var exec = System.Text.Json.JsonSerializer.Serialize(ExecEnvironment.Data(config, home), EnvyJson.Default.ExecData);
        var unchanged = Read(BashVars) == bash && Read(PowerShellVars, PrivateFiles.Utf8Bom) == powerShell && Read(CmdVars) == cmd && Read(ExecVars) == exec;
        if (unchanged && File.Exists(State)) return false;

        PrivateFiles.AtomicWrite(BashVars, bash);
        PrivateFiles.AtomicWrite(PowerShellVars, powerShell, PrivateFiles.Utf8Bom);
        PrivateFiles.AtomicWrite(CmdVars, cmd);
        PrivateFiles.AtomicWrite(ExecVars, exec);
        PrivateFiles.AtomicWrite(State, Guid.NewGuid() + "\n");
        return true;
    }

    /// <summary>
    /// Copies the app's envy command to bin\ when it changed, so it follows the app when it's updated or moved, and
    /// writes envy.json, which tells it where the app is. A running envy.exe can't be replaced but can be renamed, so
    /// the old one is moved aside first and deleted later.
    /// </summary>
    void InstallCommand(string source)
    {
        try
        {
            System.IO.Directory.CreateDirectory(CommandDirectory);
            foreach (var file in System.IO.Directory.EnumerateFiles(source))
            {
                var target = Path.Combine(CommandDirectory, Path.GetFileName(file));
                FileInfo from = new(file), to = new(target);
                if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc == from.LastWriteTimeUtc) continue;
                var temp = Path.Combine(CommandDirectory, $".{to.Name}.{Guid.NewGuid():N}");
                File.Copy(file, temp, overwrite: true);
                File.SetLastWriteTimeUtc(temp, from.LastWriteTimeUtc);
                if (to.Exists) File.Move(target, Path.Combine(CommandDirectory, $".{to.Name}.{Guid.NewGuid():N}.old"));
                File.Move(temp, target);
            }
            var info = System.Text.Json.JsonSerializer.Serialize(new LauncherInfo { App = appPath, Home = envyHome }, EnvyJson.Default.LauncherInfo);
            WriteIfChanged(LauncherFile, info, PrivateFiles.Utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void WriteIfChanged(string path, string text, System.Text.Encoding encoding)
    {
        if (Read(path, encoding) != text) PrivateFiles.AtomicWrite(path, text, encoding);
    }

    static string? Read(string path, System.Text.Encoding? encoding = null)
    {
        try
        {
            var text = File.ReadAllText(path, encoding ?? PrivateFiles.Utf8);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Copies of vars.cmd that a Command Prompt couldn't delete, for example because it was closed mid-way.</summary>
    void RemoveLeftovers()
    {
        try
        {
            foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory, "run-*"))
                if (System.IO.Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddMinutes(-5)) System.IO.Directory.Delete(folder, recursive: true);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "run-*.cmd"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-5)) File.Delete(file);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, ".*.*"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-5)) File.Delete(file);
            if (System.IO.Directory.Exists(CommandDirectory))
                foreach (var file in System.IO.Directory.EnumerateFiles(CommandDirectory, ".*.*"))
                    try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
