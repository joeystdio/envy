namespace Envy.Core;

/// <summary>
/// Writes the shell files into the shell directory (normally %LOCALAPPDATA%\Envy\shell, which only you can read):
/// a fixed hook and generated variables for each shell, the envy command for cmd, and the state stamp that makes open
/// shells reload.
/// </summary>
public sealed class ShellOutput(string directory, string? appPath = null, string? home = null)
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

        var bash = BashScript.Vars(config, home: home);
        var powerShell = PowerShellScript.Vars(config, home: home);
        var cmd = CmdScript.Vars(config, home);
        var unchanged = Read(BashVars) == bash && Read(PowerShellVars, PrivateFiles.Utf8Bom) == powerShell && Read(CmdVars) == cmd;
        if (unchanged && File.Exists(State)) return false;

        PrivateFiles.AtomicWrite(BashVars, bash);
        PrivateFiles.AtomicWrite(PowerShellVars, powerShell, PrivateFiles.Utf8Bom);
        PrivateFiles.AtomicWrite(CmdVars, cmd);
        PrivateFiles.AtomicWrite(State, Guid.NewGuid() + "\n");
        return true;
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
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
