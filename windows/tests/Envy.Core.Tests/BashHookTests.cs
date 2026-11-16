namespace Envy.Core.Tests;

/// <summary>Runs the generated hook in real bash: Git Bash on Windows, /bin/bash elsewhere.</summary>
public class BashHookTests : TempDirectoryTest
{
    string ShellDir => Path.Combine(Root, "shell");

    void Generate(EnvConfig config, string? dir = null) => new ShellOutput(dir ?? ShellDir).Write(config);

    string Run(string script, Dictionary<string, string>? env = null) => Shells.RunBash(script, ShellDir, Root, env);

    /// <summary>Shell lines that swap in the files generated into <paramref name="dir"/>, as the app does when settings change.</summary>
    static string SwapIn(string dir) =>
        $"cp \"{Shells.ForBash(dir)}/vars.sh\" \"$ENVY_DIR/vars.sh\"; cp \"{Shells.ForBash(dir)}/state\" \"$ENVY_DIR/state\"\n";

    [Fact]
    public void LoadsEnabledScopesAndSkipsDisabledOnes()
    {
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("A", "1"), new EnvVar("B", "2", isEnabled: false)]),
            [new Project("On", variables: [new EnvVar("C", "3")]), new Project("Off", isEnabled: false, variables: [new EnvVar("D", "4")])]));
        Assert.Equal("1|unset|3|unset\n", Run("""printf '%s|%s|%s|%s\n' "${A-unset}" "${B-unset}" "${C-unset}" "${D-unset}" """));
    }

    [Fact]
    public void LiteralValuesArriveByteForByte()
    {
        string[] values = ["it's \"quoted\" $HOME `whoami` \\n", "line1\nline2", "trailing\\", "ünïcødé ✓", "$(echo hacked)", @"C:\Users\me;%PATH%"];
        Generate(GlobalOnly(values.Select((v, i) => new EnvVar($"V{i}", v)).ToArray()));
        var script = string.Join("\n", values.Select((_, i) => $"printf '<%s>\\n' \"$V{i}\""));
        Assert.Equal(string.Concat(values.Select(v => $"<{v}>\n")), Run(script));
    }

    [Fact]
    public void FolderProjectLoadsInsideAndRestoresOutside()
    {
        var proj = Folder("proj");
        Folder("proj/sub");
        var sibling = Folder("project2");
        Generate(new EnvConfig(projects: [new Project("Proj", folders: [proj], variables: [new EnvVar("FOLDER_ONLY", "yes"), new EnvVar("PRE_EXISTING", "override")])]));
        var script = """
            s() { printf '%s,%s\n' "${FOLDER_ONLY-unset}" "${PRE_EXISTING-unset}"; }
            s
            cd "$PROJ"; __envy_hook; s
            cd sub; __envy_hook; s
            cd "$SIBLING"; __envy_hook; s
            cd /; __envy_hook; s
            """;
        var output = Run(script, new() { ["PROJ"] = Shells.ForBash(proj), ["SIBLING"] = Shells.ForBash(sibling), ["PRE_EXISTING"] = "original" });
        Assert.Equal("unset,original\nyes,override\nyes,override\nunset,original\nunset,original\n", output);
    }

    [Fact]
    public void FolderMatchingIgnoresCaseAsWindowsDoes()
    {
        var proj = Folder("Proj");
        Generate(new EnvConfig(projects: [new Project("Proj", folders: [proj.ToUpperInvariant()], variables: [new EnvVar("IN", "yes")])]));
        var output = Run("""cd "$PROJ"; __envy_hook; printf '%s\n' "${IN-unset}"; shopt -q nocasematch && echo leaked || echo restored""",
            new() { ["PROJ"] = Shells.ForBash(proj) });
        Assert.Equal("yes\nrestored\n", output);
    }

    [Fact]
    public void DeeperFolderWinsOverShallowerAndAlwaysOn()
    {
        var a = Folder("a");
        var ab = Folder("a/b");
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("FOO", "global")]),
            [
                new Project("Deep", folders: [ab], variables: [new EnvVar("FOO", "deep")]),
                new Project("Everywhere", variables: [new EnvVar("FOO", "everywhere")]),
                new Project("Shallow", folders: [a], variables: [new EnvVar("FOO", "shallow")]),
            ]));
        var script = """
            printf '%s\n' "$FOO"
            cd "$A"; __envy_hook; printf '%s\n' "$FOO"
            cd b; __envy_hook; printf '%s\n' "$FOO"
            cd /; __envy_hook; printf '%s\n' "$FOO"
            """;
        Assert.Equal("everywhere\nshallow\ndeep\neverywhere\n", Run(script, new() { ["A"] = Shells.ForBash(a) }));
    }

    [Fact]
    public void ExpandingValuesUseOriginalsAndDontPileUpOnReload()
    {
        Generate(GlobalOnly(
            new EnvVar("TOOLS", "~/tools", expands: true),
            new EnvVar("REF", "${TOOLS}/x;%TOOLS%/y;$env:TOOLS", expands: true),
            new EnvVar("LIT", "$(echo not run) $5", expands: true),
            new EnvVar("GREETING", "hi $NAME", expands: true)));
        var script = """
            envy reload >/dev/null
            envy reload >/dev/null
            printf '%s\n' "$TOOLS" "$REF" "$LIT" "$GREETING"
            """;
        var home = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("USERPROFILE")! : Environment.GetEnvironmentVariable("HOME")!;
        Assert.Equal($"{home}/tools\n{home}/tools/x;{home}/tools/y;{home}/tools\n$(echo not run) $5\nhi you\n", Run(script, new() { ["NAME"] = "you" }));
    }

    [Fact]
    public void PathAddsToTheFrontAndComesBackAfterwards()
    {
        var bin = Folder("bin");
        // On Windows PATH is a ; list of C:\ paths and Git Bash converts it; elsewhere the value passes through as is.
        var value = OperatingSystem.IsWindows() ? bin + ";%PATH%" : bin + ":$PATH";
        Generate(GlobalOnly(new EnvVar("Path", value, expands: true)));
        var script = """
            envy reload >/dev/null; envy reload >/dev/null
            printf '%s\n' "${PATH%%:*}"
            case ":$PATH:" in *":$BIN:"*":$BIN:"*) echo twice ;; *) echo once ;; esac
            __envy_revert
            case ":$PATH:" in *":$BIN:"*) echo still ;; *) echo gone ;; esac
            command -v ls >/dev/null && echo works
            """;
        Assert.Equal($"{Shells.ForBash(bin)}\nonce\ngone\nworks\n", Run(script, new() { ["BIN"] = Shells.ForBash(bin) }));
    }

    [Fact]
    public void OpenShellPicksUpChangesAtNextPrompt()
    {
        var next = Path.Combine(Root, "next");
        Generate(GlobalOnly(new EnvVar("FOO", "changed"), new EnvVar("BAZ", "new")), next);
        Generate(GlobalOnly(new EnvVar("FOO", "1"), new EnvVar("BAR", "2")));
        var script = """s() { printf '%s,%s,%s\n' "${FOO-unset}" "${BAR-unset}" "${BAZ-unset}"; }""" + "\ns\n" + SwapIn(next) + "__envy_hook\ns\n";
        Assert.Equal("1,2,unset\nchanged,unset,new\n", Run(script));
    }

    [Fact]
    public void ChildShellUndoesWhatItsParentApplied()
    {
        var next = Path.Combine(Root, "next");
        Generate(new EnvConfig(new Project("Global", isEnabled: false, id: Project.GlobalId)), next);
        Generate(GlobalOnly(new EnvVar("FOO", "1"), new EnvVar("PRE", "override")));
        // The parent applied FOO and PRE, then settings changed before its next prompt; a new tab started from it
        // inherits both and must still end up with the current settings.
        var child = $"\"{Shells.ForBash(Shells.Require(Shells.Bash, "bash"))}\" --norc --noprofile -c '. \"$ENVY_DIR/init.sh\"; printf \"%s,%s\\n\" \"${{FOO-unset}}\" \"${{PRE-unset}}\"'";
        var script = """printf '%s,%s\n' "${FOO-unset}" "${PRE-unset}" """ + "\n" + SwapIn(next) + child + "\n";
        Assert.Equal("1,override\nunset,orig\n", Run(script, new() { ["PRE"] = "orig" }));
    }

    [Fact]
    public void HookKeepsTheLastExitStatus()
    {
        Generate(new EnvConfig());
        Assert.Equal("rc=1\n", Run("false; __envy_hook; echo \"rc=$?\""));
    }

    [Fact]
    public void StatusAndResourcingAreSafe()
    {
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("FOO", "1")]),
            [new Project("It's mine", variables: [new EnvVar("BAR", "2")])]));
        var script = """
            . "$ENVY_DIR/init.sh"
            envy status
            printf '%s\n' "${PROMPT_COMMAND-}"
            """;
        Assert.Equal("Envy: Global, It's mine\nVariables: FOO BAR\n__envy_hook\n", Run(script));
    }

    [Fact]
    public void StartupLineSourcesTheHook()
    {
        Generate(GlobalOnly(new EnvVar("FROM_RC", "yes")));
        var rc = Path.Combine(Root, ".bashrc");
        StartupFiles.Install(rc, StartupFiles.BashLine(Shells.ForBash(Path.Combine(ShellDir, "init.sh"))));
        var bash = Shells.Require(Shells.Bash, "bash");
        var output = Shells.Run(bash, ["--norc", "--noprofile", "-c", $". '{Shells.ForBash(rc)}'; printf '%s' \"$FROM_RC\""], Root,
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!)
                : new() { ["PATH"] = "/usr/bin:/bin", ["HOME"] = Root });
        Assert.Equal("yes", output);
    }
}
