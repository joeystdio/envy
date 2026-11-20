namespace Envy.Core.Tests;

/// <summary>Runs vars.cmd in real cmd.exe, as the AutoRun value does. Windows only.</summary>
public class CmdHookTests : TempDirectoryTest
{
    string ShellDir => Path.Combine(Root, "shell");

    void Generate(EnvConfig config) => new ShellOutput(ShellDir, appPath: @"C:\Program Files\Envy\Envy.exe").Write(config);

    string Run(string script, string? cwd = null, Dictionary<string, string>? env = null) => Shells.RunCmd(script, ShellDir, cwd ?? Root, env);

    /// <summary>Prints every ENVYT_ variable, sorted, which is what "set" does.</summary>
    const string Show = "@set ENVYT_ 2>nul\r\n@echo --\r\n";

    [Fact]
    public void LoadsEnabledScopesAndSkipsDisabledOnes()
    {
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("ENVYT_A", "1"), new EnvVar("ENVYT_B", "2", isEnabled: false)]),
            [new Project("On", variables: [new EnvVar("ENVYT_C", "3")]), new Project("Off", isEnabled: false, variables: [new EnvVar("ENVYT_D", "4")])]));
        Assert.Equal("ENVYT_A=1\nENVYT_C=3\n--\n", Run(Show));
    }

    [Fact]
    public void LiteralValuesWithSpecialCharactersArriveIntact()
    {
        string[] values = ["a & b | c < d > e ^ f", "say \"hi\" & bye", "100% sure %PATH%", "(paren) !bang!", "ünïcødé ✓", @"C:\Users\me;C:\x"];
        Generate(GlobalOnly(values.Select((v, i) => new EnvVar($"ENVYT_V{i}", v)).Append(new EnvVar("ENVYT_MULTI", "two\nlines")).ToArray()));
        var expected = string.Concat(values.Select((v, i) => $"ENVYT_V{i}={v}\n")) + "--\n";
        Assert.Equal(expected, Run(Show));
    }

    [Fact]
    public void ExpandingValuesSurviveSpecialCharactersInWhatTheyReferTo()
    {
        Generate(GlobalOnly(
            new EnvVar("ENVYT_EXP", @"C:\bin;%ENVYT_BASE%", expands: true),
            new EnvVar("ENVYT_HOME", @"~\tools", expands: true)));
        var output = Run(Show, env: new() { ["ENVYT_BASE"] = @"C:\R&D;""C:\Program Files\x""" });
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        Assert.Equal($"ENVYT_BASE=C:\\R&D;\"C:\\Program Files\\x\"\nENVYT_EXP=C:\\bin;C:\\R&D;\"C:\\Program Files\\x\"\nENVYT_HOME={home}\\tools\n--\n", output);
    }

    [Fact]
    public void FolderProjectsLoadForTheFolderTheWindowStartsInAndReloadFollowsCd()
    {
        var proj = Folder("proj");
        var sub = Folder("proj/sub");
        var sibling = Folder("project2");
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("ENVYT_G", "global")]),
            [new Project("Proj", folders: [proj.ToUpperInvariant()], variables: [new EnvVar("ENVYT_IN", "yes"), new EnvVar("ENVYT_PRE", "override")])]));
        var env = new Dictionary<string, string> { ["ENVYT_PRE"] = "original" };
        Assert.Equal("ENVYT_G=global\nENVYT_IN=yes\nENVYT_PRE=override\n--\n", Run(Show, sub, env));
        Assert.Equal("ENVYT_G=global\nENVYT_PRE=original\n--\n", Run(Show, sibling, env));
        var reload = $"@cd /d \"{sibling}\"\r\n@call \"%ENVY_DIR%\\envy.cmd\" reload >nul\r\n" + Show;
        Assert.Equal("ENVYT_G=global\nENVYT_PRE=original\n--\n", Run(reload, proj, env));
    }

    [Fact]
    public void UndoesWhatTheShellItStartedFromApplied()
    {
        Generate(GlobalOnly(new EnvVar("ENVYT_NEW", "1")));
        // As a PowerShell or Git Bash window leaves it: ENVYT_FOO set by Envy, ENVYT_PRE overridden.
        var env = new Dictionary<string, string>
        {
            ["__ENVY_VARS"] = "ENVYT_FOO ENVYT_PRE ", ["ENVYT_FOO"] = "1", ["ENVYT_PRE"] = "override", ["__ENVY_O_ENVYT_PRE"] = "orig",
        };
        Assert.Equal("ENVYT_NEW=1\nENVYT_PRE=orig\n--\nENVYT_NEW \n", Run(Show + "@echo %__ENVY_VARS%\r\n", env: env));
    }

    [Fact]
    public void EnvyStatusNamesWhatIsLoaded()
    {
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("ENVYT_FOO", "1")]),
            [new Project("R&D (mine)", variables: [new EnvVar("ENVYT_BAR", "2")])]));
        Assert.Equal("Envy: Global, R&D (mine)\nVariables: ENVYT_FOO ENVYT_BAR\n", Run("@call \"%ENVY_DIR%\\envy.cmd\" status\r\n"));
    }

    [Fact]
    public void AutoRunValueRunsEnvyAfterWhatWasThere()
    {
        Generate(GlobalOnly(new EnvVar("ENVYT_AUTO", "yes")));
        var value = AutoRun.Added("set ENVYT_FIRST=1", Path.Combine(ShellDir, "init.cmd"));
        var cmd = Shells.Require(Shells.Cmd, "cmd");
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(e => !((string)e.Key).StartsWith("__ENVY", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase);
        var output = Shells.Run(cmd, ["/d", "/c", value + " & set ENVYT_"], Root, env, cmdLine: true);
        Assert.Equal("ENVYT_AUTO=yes\nENVYT_FIRST=1\n", output);
    }
}

/// <summary>A shell started from another one inherits what Envy set there and must clean it up, PATH included. Windows only.</summary>
public class CrossShellTests : TempDirectoryTest
{
    string ShellDir => Path.Combine(Root, "shell");

    [Fact]
    public void GitBashStartedFromPowerShellKeepsAWorkingPath()
    {
        Shells.Require(Shells.Cmd, "Windows");
        var bash = Shells.Require(Shells.Bash, "Git Bash");
        var pwsh = Shells.Require(Shells.PowerShells.FirstOrDefault(), "PowerShell");
        var bin = Folder("bin");
        new ShellOutput(ShellDir).Write(GlobalOnly(new EnvVar("PATH", bin + ";%PATH%", expands: true)));
        // The child bash reads its script from a file, which keeps quoting out of PowerShell's argument passing.
        var childScript = Path.Combine(Root, "child.sh");
        File.WriteAllText(childScript, """
            export ENVY_DIR="$(cygpath -u "$ENVY_DIR")"
            . "$ENVY_DIR/init.sh"
            [ "${PATH%%:*}" = "$(cygpath -u "$BIN")" ] && echo first
            command -v ls >/dev/null && echo works
            __envy_revert
            case ":$PATH:" in *":$(cygpath -u "$BIN"):"*) echo still ;; *) echo gone ;; esac
            command -v ls >/dev/null && echo works

            """.ReplaceLineEndings("\n"));
        var child = $"& '{bash}' --norc --noprofile '{PathStyle.ToPosix(childScript)}'";
        // As on a PC where Git for Windows put only its cmd folder on PATH (CI runners have usr\bin there too), so bash
        // has ls and cygpath only from the folders it adds itself at startup.
        var git = Path.GetDirectoryName(Path.GetDirectoryName(bash)!)!;
        var path = string.Join(';', (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d =>
            !d.StartsWith(git + "\\", StringComparison.OrdinalIgnoreCase) || d.TrimEnd('\\').EndsWith("\\cmd", StringComparison.OrdinalIgnoreCase)));
        var output = Shells.RunPowerShell(pwsh, $"($env:PATH -split ';')[0]\n{child}", ShellDir, Root, new() { ["BIN"] = bin, ["PATH"] = path });
        Assert.Equal($"{bin}\nfirst\nworks\ngone\nworks\n", output);
    }

    [Fact]
    public void CommandPromptStartedFromGitBashGetsAWindowsPathBack()
    {
        Shells.Require(Shells.Cmd, "Windows");
        var bash = Shells.Require(Shells.Bash, "Git Bash");
        var bin = Folder("bin");
        new ShellOutput(ShellDir).Write(GlobalOnly(new EnvVar("ENVYT_X", "1")));
        // Bash applies PATH and remembers the original in Windows form, so cmd can put it back.
        var script = $"""
            __envy_set PATH "{PathStyle.ToPosix(bin)}:$PATH"; export __ENVY_VARS
            cmd //d //c 'echo %__ENVY_O_PATH%' | head -c 3
            """;
        var output = Shells.RunBash(script, ShellDir, Root, bash: bash);
        Assert.Matches(@"^[A-Za-z]:\\", output);
    }
}

public class CredentialManagerTests
{
    [Fact]
    public void StoresReadsSplitsAndDeletesSecrets()
    {
        if (OperatingSystem.IsWindows()) RoundTrip();
        else Assert.Skip("Credential Manager is Windows only.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void RoundTrip()
    {
        var vault = new CredentialManagerVault($"EnvyTest-{Guid.NewGuid():N}/");
        var longValue = string.Concat(Enumerable.Repeat("-----BEGIN KEY-----ünï", 300));
        try
        {
            vault.Save(new Dictionary<string, string> { ["a"] = "s3cret", ["b"] = longValue, ["c"] = "" }, new Dictionary<string, string> { ["a"] = "OPENAI_API_KEY" });
            var loaded = vault.Load();
            Assert.Equal("s3cret", loaded["a"]);
            Assert.Equal(longValue, loaded["b"]);
            Assert.Equal("", loaded["c"]);

            vault.Save(new Dictionary<string, string> { ["b"] = "short now" });
            Assert.Equal(new Dictionary<string, string> { ["b"] = "short now" }, vault.Load());
        }
        finally
        {
            vault.Save(new Dictionary<string, string>());
        }
        Assert.Empty(vault.Load());
    }
}
