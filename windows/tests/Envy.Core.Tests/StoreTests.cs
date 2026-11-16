namespace Envy.Core.Tests;

/// <summary>The app state in an ENVY_HOME sandbox: settings, secrets, shell files and startup files all inside it.</summary>
public class StoreTests : TempDirectoryTest
{
    EnvyStore NewStore() => new(new AppPaths(Root), appPath: @"C:\Program Files\Envy\Envy.exe");

    [Fact]
    public void SavesSettingsWithoutSecretsAndReadsThemBack()
    {
        var store = NewStore();
        store.AddVariables([new EnvVar("TOKEN", "s3cret", isSecret: true), new EnvVar("PLAIN", "p")], Project.GlobalId, MergePolicy.Replace, "Add");
        store.Save();
        var json = File.ReadAllText(store.Paths.ConfigFile);
        Assert.DoesNotContain("s3cret", json);
        Assert.Contains("s3cret", File.ReadAllText(Path.Combine(store.Paths.SettingsDirectory, "secrets.json")));
        Assert.Contains("s3cret", File.ReadAllText(store.Output.BashVars));

        var again = NewStore();
        Assert.Equal(["s3cret", "p"], again.Config.Global.Variables.Select(v => v.Value));
        Assert.Null(again.Problem);
    }

    [Fact]
    public void UnreadableSettingsAreSetAsideNotOverwritten()
    {
        var paths = new AppPaths(Root);
        Directory.CreateDirectory(paths.SettingsDirectory);
        File.WriteAllText(paths.ConfigFile, "{ not json");
        var store = new EnvyStore(paths);
        Assert.Contains("config.unreadable.json", store.Problem);
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(paths.SettingsDirectory, "config.unreadable.json")));
    }

    [Fact]
    public void UndoAndRedoWholeStepsButNotTyping()
    {
        var store = NewStore();
        var project = store.CreateProject("api", null, null, [new EnvVar("A", "1")]);
        store.RenameProject(project.Id, "api-renamed");
        store.SetProjectEnabled(project.Id, false);
        Assert.Equal("Turn Off Project", store.UndoName);

        store.Undo();
        Assert.True(store.Config.Find(project.Id)!.IsEnabled);
        Assert.Equal("api-renamed", store.Config.Find(project.Id)!.Name);
        store.Undo();
        Assert.Empty(store.Config.Projects);
        store.Redo();
        Assert.Single(store.Config.Projects);
        Assert.Equal("Turn Off Project", store.RedoName);
    }

    [Fact]
    public void ProjectsGetUniqueNamesAndDuplicatesStartOff()
    {
        var store = NewStore();
        var first = store.CreateProject("web", Folder("web"), null, [new EnvVar("PORT", "3000")]);
        var second = store.CreateProject("web", null, first.Id, []);
        Assert.Equal("web 2", second.Name);
        Assert.Equal(["PORT"], second.Variables.Select(v => v.Key));
        Assert.NotEqual(first.Variables[0].Id, second.Variables[0].Id);

        var copy = store.DuplicateProject(first.Id)!;
        Assert.Equal("web copy", copy.Name);
        Assert.False(copy.IsEnabled);
        Assert.Empty(copy.Folders);
        Assert.Equal(1, store.Config.Projects.FindIndex(p => p.Id == copy.Id));

        store.MoveProject(copy.Id, 0);
        Assert.Equal(copy.Id, store.Config.Projects[0].Id);
        store.DeleteProject(copy.Id);
        Assert.Equal(2, store.Config.Projects.Count);
    }

    [Fact]
    public void CopiesAndMovesVariablesBetweenProjects()
    {
        var store = NewStore();
        var a = store.CreateProject("a", null, null, [new EnvVar("X", "1"), new EnvVar("Y", "2")]);
        var b = store.CreateProject("b", null, null, [new EnvVar("X", "old")]);
        var x = a.Variables[0].Id;
        store.CopyVariables(new HashSet<Guid> { x }, a.Id, b.Id, move: false);
        Assert.Equal("1", store.Config.Find(b.Id)!.Variables.Single().Value);
        store.CopyVariables(new HashSet<Guid> { a.Variables[1].Id }, a.Id, b.Id, move: true);
        Assert.Equal(["X"], store.Config.Find(a.Id)!.Variables.Select(v => v.Key));
        Assert.Equal(["X", "Y"], store.Config.Find(b.Id)!.Variables.Select(v => v.Key));
        Assert.Equal("X=1\nY=2\n", store.DotEnvText(null, b.Id));
    }

    [Fact]
    public void ConnectsEveryShellInsideTheSandboxAndDisconnectsCleanly()
    {
        var store = NewStore();
        foreach (var kind in Enum.GetValues<ShellKind>()) store.Connect(kind);
        Assert.All(Enum.GetValues<ShellKind>(), kind => Assert.True(store.IsConnected(kind), kind.ToString()));
        Assert.Contains("init.ps1", File.ReadAllText(Path.Combine(Root, "Documents", "PowerShell", "profile.ps1")));
        Assert.Contains("init.ps1", File.ReadAllText(Path.Combine(Root, "Documents", "WindowsPowerShell", "profile.ps1")));
        Assert.Contains("init.sh", File.ReadAllText(Path.Combine(Root, ".bashrc")));
        // No login file existed, so Envy wrote the one Git for Windows would, which loads ~/.bashrc.
        Assert.Contains(". ~/.bashrc", File.ReadAllText(Path.Combine(Root, ".bash_profile")));
        Assert.Contains("init.cmd", File.ReadAllText(Path.Combine(Root, "AutoRun.txt")));

        foreach (var kind in Enum.GetValues<ShellKind>()) store.Disconnect(kind);
        Assert.All(Enum.GetValues<ShellKind>(), kind => Assert.False(store.IsConnected(kind), kind.ToString()));
        Assert.False(File.Exists(Path.Combine(Root, "AutoRun.txt")));
    }

    [Fact]
    public void GitBashLoginFileGetsTheLineOnlyWhenItDoesntLoadBashrc()
    {
        var store = NewStore();
        Write("export A=1\n", ".bash_profile");
        Assert.Equal(Path.Combine(Root, ".bash_profile"), store.GitBashLoginFile());
        store.Connect(ShellKind.GitBash);
        Assert.True(StartupFiles.IsInstalled(Path.Combine(Root, ".bash_profile")));

        Write("test -f ~/.bashrc && . ~/.bashrc\n", ".bash_profile");
        Assert.Null(store.GitBashLoginFile());
    }

    [Fact]
    public void FindsPlaintextSecretsAndTakesThemOut()
    {
        Write("export GITHUB_TOKEN=ghp_1\nexport EDITOR=vim\n", ".bashrc");
        Write("$env:OPENAI_API_KEY = 'sk-1'\r\n", "Documents/PowerShell/Microsoft.PowerShell_profile.ps1");
        File.WriteAllText(Path.Combine(Root, "UserEnvironment.json"), """{ "AZURE_CLIENT_SECRET": "az", "Path": "C:\\x" }""");
        var store = NewStore();
        store.AddVariables([new EnvVar("EDITOR", "vim")], Project.GlobalId, MergePolicy.Replace, "Add");

        var found = store.PlaintextExports();
        Assert.Equal(["GITHUB_TOKEN", "OPENAI_API_KEY", "AZURE_CLIENT_SECRET"], found.Select(f => f.Key));
        Assert.All(found, f => Assert.True(f.Variable.IsSecret));

        store.RemovePlaintext(found);
        Assert.Equal("export EDITOR=vim\n", File.ReadAllText(Path.Combine(Root, ".bashrc")));
        Assert.True(File.Exists(Path.Combine(Root, ".bashrc.envy-backup")));
        Assert.Equal("", File.ReadAllText(Path.Combine(Root, "Documents", "PowerShell", "Microsoft.PowerShell_profile.ps1")));
        Assert.Equal(["Path"], new FileUserEnvironment(Path.Combine(Root, "UserEnvironment.json")).Read().Keys);
    }

    [Fact]
    public void ImportsFoundProjectsOnce()
    {
        var store = NewStore();
        Write("A=1\n", "code/api/.env");
        var found = ProjectScanner.Scan(Path.Combine(Root, "code"), store.Paths.UserSecretsRoot);
        Assert.Equal(1, store.ImportProjects(found));
        Assert.Equal(0, store.ImportProjects(found));
        Assert.True(store.IsLinked(found[0].Folder));
        Assert.Equal("1 variable · ~" + Path.DirectorySeparatorChar + Path.Combine("code", "api"), store.Subtitle(store.Config.Projects[0]));
    }

    [Fact]
    public void ExportWritesEnabledVariablesWithAHeader()
    {
        var store = NewStore();
        var p = store.CreateProject("api", null, null, [new EnvVar("A", "1"), new EnvVar("B", "2", isEnabled: false)]);
        var file = Path.Combine(Root, "out.env");
        store.ExportDotEnv(p.Id, file);
        Assert.Equal("# Exported from Envy: api\nA=1\n", File.ReadAllText(file));
    }
}
