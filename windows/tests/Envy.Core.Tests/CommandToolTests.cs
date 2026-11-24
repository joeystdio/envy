using System.Runtime.Versioning;

namespace Envy.Core.Tests;

/// <summary>What envy exec gives a command: the environment a shell hook would set up in that folder, right now.</summary>
public class ExecEnvironmentTests : TempDirectoryTest
{
    [Fact]
    public void PutsBackWhatEnvySetThenAppliesWhatLoadsHere()
    {
        var api = Folder("api");
        var config = new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("EDITOR", "code"), new EnvVar("OFF", "x", isEnabled: false)]),
            [new Project("api", folders: [api], variables: [new EnvVar("PORT", "8080"), new EnvVar("URL", "http://localhost:$PORT/%EDITOR%", expands: true)])]);
        var data = ExecEnvironment.Data(config);

        // This process started while an older set was loaded: STALE came from Envy, EDITOR overrode the user's own.
        var before = new Dictionary<string, string>
        {
            ["Path"] = @"C:\Windows",
            ["__ENVY_VARS"] = "STALE EDITOR ",
            ["STALE"] = "old",
            ["EDITOR"] = "vim",
            ["__ENVY_O_EDITOR"] = "notepad",
        };
        var inside = ExecEnvironment.Compute(data, [Path.Combine(api, "src")], before);
        Assert.False(inside.ContainsKey("STALE"));
        Assert.False(inside.ContainsKey("OFF"));
        Assert.Equal("code", inside["EDITOR"]);
        Assert.Equal("notepad", inside["__ENVY_O_EDITOR"]);
        Assert.Equal("http://localhost:8080/code", inside["URL"]);
        Assert.Equal("EDITOR PORT URL ", inside["__ENVY_VARS"]);
        Assert.Equal(@"C:\Windows", inside["PATH"]);

        var outside = ExecEnvironment.Compute(data, [Root], before);
        Assert.False(outside.ContainsKey("PORT"));
        Assert.Equal("EDITOR ", outside["__ENVY_VARS"]);
    }

    [Fact]
    public void FindsProgramsOnTheNewPathWithPathExt()
    {
        WindowsOnly.Check();
        var bin = Folder("bin");
        var tool = Write("@echo off\r\n", "bin/tool.cmd");
        var env = new Dictionary<string, string> { ["PATH"] = @"C:\nowhere;" + bin, ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD" };
        Assert.Equal(tool, ExecProcess.Resolve("tool", env, Root));
        Assert.Equal(tool, ExecProcess.Resolve("TOOL.cmd", env, Root), ignoreCase: true);
        Assert.Equal(tool, ExecProcess.Resolve(@"bin\tool", env, Root));
        Assert.Null(ExecProcess.Resolve("missing", env, Root));
    }

    [Fact]
    public void BatchFilesGetTheirArgumentsIntact()
    {
        var cmd = Shells.Require(Shells.Cmd, "cmd");
        // Echoes its command line as cmd parsed it; %~1 and friends would let & and | run as commands inside the batch.
        var tool = Write("@echo(%*\r\n", "bin/show args.cmd");
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);
        var start = ExecProcess.StartInfo([tool, "a b", "x&y|z", "100%PATH%", "^caret", "plain"], env, Root);
        Assert.Equal(cmd, start.FileName, ignoreCase: true);
        start.RedirectStandardOutput = true;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal("\"a b\" \"x&y|z\" \"100%PATH%\" \"^caret\" plain", output.Trim());
        Assert.Throws<EnvyUsageException>(() => ExecProcess.BatchCommandLine(tool, ["two\nlines"]));
    }
}

/// <summary>The named pipe between the envy command and the app.</summary>
[SupportedOSPlatform("windows")]
public class CommandPipeTests : TempDirectoryTest
{
    [Fact]
    public void RoundTripAndNotRunning()
    {
        WindowsOnly.Check();
        var name = CommandPipe.Name(new AppPaths(Root));
        Assert.NotEqual(name, CommandPipe.Name(new AppPaths(Path.Combine(Root, "other"))));
        var request = new CommandRequest { Arguments = ["list", "api"], Context = new RequestContext { Directories = [Root], Loaded = ["A"] } };

        var missing = Assert.Throws<CommandPipeException>(() => CommandPipe.Send(name, request, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(PipeFailure.NotRunning, missing.Failure);

        using var server = new CommandPipeServer(name, r => Task.FromResult(new CommandResponse
        {
            Output = string.Join(",", r.Arguments) + "|" + string.Join(",", r.Context.Loaded),
            ExitCode = 3,
        }));
        server.Start();
        int? serverProcess = null;
        for (var i = 0; i < 3; i++)
        {
            var response = CommandPipe.Send(name, request, TimeSpan.FromSeconds(5), connected: pid => serverProcess = pid);
            Assert.Equal("list,api|A", response.Output);
            Assert.Equal(3, response.ExitCode);
            Assert.Equal(Environment.ProcessId, response.ProcessID);
        }
        Assert.Equal(Environment.ProcessId, serverProcess);
    }
}

/// <summary>
/// The envy command as programs and shells find it: bin\ on PATH once, the shell function passing everything but status
/// and reload to it, and exec. Runs the envy.exe built next to the tests, with no app running, so reads come from the
/// settings file.
/// </summary>
public class EnvyCommandInShellsTests : TempDirectoryTest
{
    string ShellDir => Path.Combine(Root, "AppData", "Local", "Envy", "shell");
    static string CommandSource => Path.Combine(AppContext.BaseDirectory, "cli");

    AppPaths Setup(EnvConfig config)
    {
        if (!File.Exists(Path.Combine(CommandSource, "envy.exe"))) Assert.Skip("The envy command isn't built here.");
        var paths = new AppPaths(Root);
        ConfigFile.Write(paths.ConfigFile, config);
        new ShellOutput(ShellDir, home: Root, commandSource: CommandSource, envyHome: Root).Write(config);
        Assert.True(File.Exists(Path.Combine(ShellDir, "bin", "envy.exe")));
        Assert.Contains(Root.Replace("\\", "\\\\"), File.ReadAllText(Path.Combine(ShellDir, "bin", "envy.json")));
        return paths;
    }

    static EnvConfig Sample(string folder) => new(
        new Project("Global", id: Project.GlobalId, variables: [new EnvVar("ENVYT_G", "global"), new EnvVar("ENVYT_SECRET", "s3cret", isSecret: true)]),
        [new Project("api", folders: [folder], variables: [new EnvVar("ENVYT_PORT", "8080")])]);

    [Theory, MemberData(nameof(PowerShellHookTests.Editions), MemberType = typeof(PowerShellHookTests))]
    public void PowerShellPutsTheCommandOnPathAndPassesItOn(string edition)
    {
        var exe = edition == "pwsh" ? Shells.Require(Shells.Pwsh, "PowerShell 7") : Shells.Require(Shells.WindowsPowerShell, "Windows PowerShell");
        var api = Folder("api");
        Setup(Sample(api));
        // The envy command writes a note to stderr, which Windows PowerShell would turn into a stopping error.
        var script = """
            $ErrorActionPreference = 'Continue'
            . (Join-Path $env:ENVY_DIR 'init.ps1')
            $bin = Join-Path $env:ENVY_DIR 'bin'
            "on path: $(@($env:PATH.Split(';') | Where-Object { $_ -eq $bin }).Count)"
            "found: $((Get-Command envy -CommandType Application).Source -eq (Join-Path $bin 'envy.exe'))"
            envy status
            envy list Global
            Set-Location -LiteralPath $env:API; $null = prompt
            envy exec -- cmd /d /c "echo [%ENVYT_PORT%] [%ENVYT_SECRET%]"
            "exit: $LASTEXITCODE"
            """;
        var output = Shells.RunPowerShell(exe, script, ShellDir, Root, new() { ["API"] = api });
        Assert.Contains("on path: 1\n", output);
        Assert.Contains("found: True\n", output);
        Assert.Contains("Envy: Global\nVariables: ENVYT_G ENVYT_SECRET\n", output);
        Assert.Contains("Global (on, every terminal)\n  ENVYT_G=global\n  ENVYT_SECRET  secret\n", output);
        Assert.Contains("envy: read from Envy's settings file", output);
        Assert.Contains("[8080] [s3cret]\nexit: 0\n", output);
    }

    [Fact]
    public void GitBashPutsTheCommandOnPathAndLeavesArgumentsAlone()
    {
        var api = Folder("api");
        Setup(Sample(api));
        var script = """
            case ":$PATH:" in *":$ENVY_DIR/bin:"*) echo "on path" ;; esac
            . "$ENVY_DIR/init.sh"
            printf '%s\n' "$PATH" | tr ':' '\n' | grep -c "^$ENVY_DIR/bin\$"
            envy list /c/nope
            cd "$API"; __envy_hook
            envy exec -- cmd //d //c "echo [%ENVYT_PORT%]"
            unset -f __envy_hook; unset __envy_gen
            envy status
            """;
        var output = Shells.RunBash(script, ShellDir, Root, new() { ["API"] = Shells.ForBash(api) });
        Assert.StartsWith("on path\n1\n", output); // RunBash loads init.sh once already
        Assert.Contains("envy: No project called /c/nope.", output);
        Assert.Contains("[8080]", output);
        // Without the hook (a copied function in an agent's shell), status comes from the envy command.
        Assert.Contains("Envy loads here: Global, api (later ones win).", output);
    }

    [Fact]
    public void CommandPromptPutsTheCommandOnPathOnce()
    {
        Setup(Sample(Folder("api")));
        var script = """
            @call "%ENVY_DIR%\init.cmd"
            @set "n=0"
            @for %%p in ("%PATH:;=" "%") do @if /i "%%~p"=="%ENVY_DIR%\bin" set /a n+=1 >nul
            @echo on path: %n%
            @call "%ENVY_DIR%\envy.cmd" list Global
            @envy --version
            """;
        var output = Shells.RunCmd(script, ShellDir, Root);
        Assert.Contains("on path: 1\n", output);
        Assert.Contains("  ENVYT_SECRET  secret\n", output);
        Assert.Contains("envy ", output);
        Assert.DoesNotContain("s3cret", output);
    }
}
