namespace Envy.Core.Tests;

/// <summary>Runs the generated hook in real PowerShell 7 (pwsh) and, on Windows, Windows PowerShell 5.1.</summary>
public class PowerShellHookTests : TempDirectoryTest
{
    public static TheoryData<string> Editions => ["pwsh", "powershell"];

    string ShellDir => Path.Combine(Root, "shell");

    void Generate(EnvConfig config, string? dir = null) => new ShellOutput(dir ?? ShellDir).Write(config);

    static string Exe(string edition) => edition == "pwsh"
        ? Shells.Require(Shells.Pwsh, "PowerShell 7")
        : Shells.Require(Shells.WindowsPowerShell, "Windows PowerShell");

    string Run(string edition, string script, Dictionary<string, string>? env = null) =>
        Shells.RunPowerShell(Exe(edition), script, ShellDir, Root, env);

    static string SwapIn(string dir) =>
        $"Copy-Item -LiteralPath '{Path.Combine(dir, "vars.ps1")}' -Destination (Join-Path $env:ENVY_DIR 'vars.ps1') -Force\n" +
        $"Copy-Item -LiteralPath '{Path.Combine(dir, "state")}' -Destination (Join-Path $env:ENVY_DIR 'state') -Force\n";

    const string Show = "function s { param([string[]]$n) ($n | ForEach-Object { $v = [Environment]::GetEnvironmentVariable($_); if ($null -eq $v) { 'unset' } else { $v } }) -join ',' }\n";

    [Theory, MemberData(nameof(Editions))]
    public void LoadsEnabledScopesAndSkipsDisabledOnes(string edition)
    {
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("A", "1"), new EnvVar("B", "2", isEnabled: false)]),
            [new Project("On", variables: [new EnvVar("C", "3")]), new Project("Off", isEnabled: false, variables: [new EnvVar("D", "4")])]));
        Assert.Equal("1,unset,3,unset\n", Run(edition, Show + "s A, B, C, D"));
    }

    [Theory, MemberData(nameof(Editions))]
    public void LiteralValuesArriveCharacterForCharacter(string edition)
    {
        string[] values = ["it's \"quoted\" $HOME `whoami` ‘curly’", "line1\nline2", "trailing\\", "ünïcødé ✓", "$(Write-Output hacked)", @"C:\Users\me;%PATH%"];
        Generate(GlobalOnly(values.Select((v, i) => new EnvVar($"V{i}", v)).ToArray()));
        var script = string.Join("\n", values.Select((_, i) => $"'<' + $env:V{i} + '>'"));
        Assert.Equal(string.Concat(values.Select(v => $"<{v}>\n")), Run(edition, script));
    }

    [Theory, MemberData(nameof(Editions))]
    public void FolderProjectLoadsOnCdAndRestoresOutside(string edition)
    {
        var proj = Folder("proj");
        Folder("proj/sub");
        var sibling = Folder("project2");
        Generate(new EnvConfig(projects: [new Project("Proj", folders: [proj], variables: [new EnvVar("FOLDER_ONLY", "yes"), new EnvVar("PRE_EXISTING", "override")])]));
        // $null = prompt is what showing the next prompt does.
        var script = Show + """
            s FOLDER_ONLY, PRE_EXISTING
            Set-Location -LiteralPath $env:PROJ; $null = prompt; s FOLDER_ONLY, PRE_EXISTING
            Set-Location sub; $null = prompt; s FOLDER_ONLY, PRE_EXISTING
            Set-Location -LiteralPath $env:SIBLING; $null = prompt; s FOLDER_ONLY, PRE_EXISTING
            Set-Location -LiteralPath ([IO.Path]::GetPathRoot($env:PROJ)); $null = prompt; s FOLDER_ONLY, PRE_EXISTING
            """;
        var output = Run(edition, script, new() { ["PROJ"] = proj, ["SIBLING"] = sibling, ["PRE_EXISTING"] = "original" });
        Assert.Equal("unset,original\nyes,override\nyes,override\nunset,original\nunset,original\n", output);
    }

    [Theory, MemberData(nameof(Editions))]
    public void DeeperFolderWinsOverShallowerAndAlwaysOn(string edition)
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
            $env:FOO
            Set-Location -LiteralPath $env:A; $null = prompt; $env:FOO
            Set-Location b; $null = prompt; $env:FOO
            Set-Location -LiteralPath ([IO.Path]::GetPathRoot($env:A)); $null = prompt; $env:FOO
            """;
        Assert.Equal("everywhere\nshallow\ndeep\neverywhere\n", Run(edition, script, new() { ["A"] = a }));
    }

    [Theory, MemberData(nameof(Editions))]
    public void ExpandingValuesUseOriginalsAndDontPileUpOnReload(string edition)
    {
        var bin = Folder("bin");
        var sep = Path.PathSeparator;
        Generate(GlobalOnly(
            new EnvVar("PATH", $"{bin}{sep}$PATH", expands: true),
            new EnvVar("TOOLS", "~/tools", expands: true),
            new EnvVar("REF", "${TOOLS}/x;%TOOLS%/y;$env:TOOLS", expands: true),
            new EnvVar("LIT", "$(Write-Output not-run) $5", expands: true)));
        var script = """
            envy reload | Out-Null
            envy reload | Out-Null
            ($env:PATH -split [IO.Path]::PathSeparator)[0]
            @($env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -eq $env:BIN }).Count
            $env:TOOLS; $env:REF; $env:LIT
            """;
        var home = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("USERPROFILE")! : Environment.GetEnvironmentVariable("HOME")!;
        Assert.Equal($"{bin}\n1\n{home}/tools\n{home}/tools/x;{home}/tools/y;{home}/tools\n$(Write-Output not-run) $5\n", Run(edition, script, new() { ["BIN"] = bin }));
    }

    [Theory, MemberData(nameof(Editions))]
    public void OpenShellPicksUpChangesAtNextPrompt(string edition)
    {
        var next = Path.Combine(Root, "next");
        Generate(GlobalOnly(new EnvVar("FOO", "changed"), new EnvVar("BAZ", "new")), next);
        Generate(GlobalOnly(new EnvVar("FOO", "1"), new EnvVar("BAR", "2")));
        var script = Show + "s FOO, BAR, BAZ\n" + SwapIn(next) + "$null = prompt\ns FOO, BAR, BAZ\n";
        Assert.Equal("1,2,unset\nchanged,unset,new\n", Run(edition, script));
    }

    [Theory, MemberData(nameof(Editions))]
    public void ChildShellUndoesWhatItsParentApplied(string edition)
    {
        var next = Path.Combine(Root, "next");
        Generate(new EnvConfig(new Project("Global", isEnabled: false, id: Project.GlobalId)), next);
        Generate(GlobalOnly(new EnvVar("FOO", "1"), new EnvVar("PRE", "override")));
        var exe = Exe(edition);
        var child = $"& '{exe}' -NoLogo -NoProfile -NonInteractive -Command \". (Join-Path `$env:ENVY_DIR 'init.ps1'); (@('FOO','PRE') | ForEach-Object {{ `$v = [Environment]::GetEnvironmentVariable(`$_); if (`$null -eq `$v) {{ 'unset' }} else {{ `$v }} }}) -join ','\"\n";
        var script = Show + "s FOO, PRE\n" + SwapIn(next) + child;
        Assert.Equal("1,override\nunset,orig\n", Run(edition, script, new() { ["PRE"] = "orig" }));
    }

    [Fact]
    public void PowerShell7LoadsAFoldersProjectRightOnCd()
    {
        var proj = Folder("proj");
        Generate(new EnvConfig(projects: [new Project("Proj", folders: [proj], variables: [new EnvVar("IN", "yes")])]));
        Assert.Equal("yes\n", Run("pwsh", "Set-Location -LiteralPath $env:PROJ; $env:IN", new() { ["PROJ"] = proj }));
    }

    [Theory, MemberData(nameof(Editions))]
    public void PromptKeepsTheLastStatusAndExitCode(string edition)
    {
        Generate(GlobalOnly(new EnvVar("A", "1")));
        var script = """
            function global:prompt { "status=$? code=$global:LASTEXITCODE" }
            __envy_wrap_prompt
            $global:LASTEXITCODE = 3
            Get-Item -LiteralPath '/does/not/exist' -ErrorAction SilentlyContinue
            prompt
            """;
        Assert.Equal("status=False code=3\n", Run(edition, script));
    }

    [Theory, MemberData(nameof(Editions))]
    public void StatusAndResourcingAreSafe(string edition)
    {
        Generate(new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("FOO", "1")]),
            [new Project("It's mine", variables: [new EnvVar("BAR", "2")])]));
        var script = """
            . (Join-Path $env:ENVY_DIR 'init.ps1')
            envy status
            ([regex]::Matches("$function:prompt", '__envy_hook')).Count
            """;
        Assert.Equal("Envy: Global, It's mine\nVariables: FOO BAR\n1\n", Run(edition, script));
    }

    [Theory, MemberData(nameof(Editions))]
    public void ProfileLineLoadsTheHookAndTheProbeSeesIt(string edition)
    {
        Generate(GlobalOnly(new EnvVar("FROM_PROFILE", "yes")));
        var profile = Path.Combine(Root, "Documents", "PowerShell", "profile.ps1");
        var shell = new InstalledShell(edition == "pwsh" ? ShellKind.PowerShell7 : ShellKind.WindowsPowerShell, Exe(edition));
        Assert.False(ShellProbe.Inspect(shell, profile)!.HookLoaded);

        StartupFiles.Install(profile, StartupFiles.PowerShellLine(PowerShellScript.Quoted(Path.Combine(ShellDir, "init.ps1"))), preferCrLf: true);
        var inspection = ShellProbe.Inspect(shell, profile)!;
        Assert.True(inspection.HookLoaded);
        Assert.NotNull(inspection.ExecutionPolicy);
    }
}
