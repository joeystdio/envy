namespace Envy.Core.Tests;

/// <summary>The envy command as the app runs it: parsing, targets, secrets kept out of reach, and folder matching.</summary>
public class EnvyCommandTests : TempDirectoryTest
{
    const string Repo = @"C:\Users\me\Developer\api";

    static EnvConfig Config() => new(
        new Project("Global", id: Project.GlobalId, variables:
        [
            new EnvVar("EDITOR", "code --wait"),
            new EnvVar("OPENAI_API_KEY", "sk-live-123", isSecret: true),
        ]),
        [
            new Project("api", folders: [Repo], variables: [new EnvVar("PORT", "8080")]),
            new Project("api web", folders: [Repo + @"\web"]),
            new Project("Tools", isEnabled: false, variables: [new EnvVar("DEBUG", "1")]),
        ]);

    static CommandOutcome Run(EnvConfig config, string[] args, string cwd = Repo, string[]? loaded = null) =>
        EnvyCommand.Parse(args, cwd).Run(config, new CommandContext([cwd], loaded ?? []));

    static string? UsageError(EnvConfig config, string[] args, string cwd = Repo)
    {
        try
        {
            Run(config, args, cwd);
            return null;
        }
        catch (EnvyUsageException e)
        {
            return e.Message;
        }
    }

    // Parsing

    [Fact]
    public void ParsesTargetsAndFolders()
    {
        WindowsOnly.Check();
        Assert.IsType<EnvyCommand.Status>(EnvyCommand.Parse([], Repo));
        var set = Assert.IsType<EnvyCommand.Set>(EnvyCommand.Parse(["set", "A=1", "--global"], Repo));
        Assert.Equal(("A", "1"), set.Assignments.Single());
        Assert.Equal(CommandTarget.Global, set.Target);
        var url = Assert.IsType<EnvyCommand.Set>(EnvyCommand.Parse(["set", "URL=a=b", "-p", "api"], Repo));
        Assert.Equal(("URL", "a=b"), url.Assignments.Single());
        Assert.Equal(CommandTarget.Named("api"), url.Target);

        var project = Assert.IsType<EnvyCommand.AddProject>(EnvyCommand.Parse(["project", "add", "My", "App", "--folder", "."], Repo));
        Assert.Equal("My App", project.Name);
        Assert.Equal([Repo], project.Folders);
        Assert.True(project.Enabled);

        Assert.Equal(new EnvyCommand.Link(@"C:\Users\me\Developer\x", CommandTarget.Named("api")), EnvyCommand.Parse(["link", @"..\x", "--project", "api"], Repo));
        Assert.Equal(new EnvyCommand.Link(@"C:\Users\me\Developer\x", CommandTarget.Named("api")), EnvyCommand.Parse(["link", "../x/", "--project", "api"], Repo));
        // Git Bash's own spelling of a folder, when it passes one along unconverted.
        Assert.Equal(new EnvyCommand.Link(@"C:\code\x", CommandTarget.Named("api")), EnvyCommand.Parse(["link", "/c/code/x", "--project", "api"], Repo));
        Assert.Equal(new EnvyCommand.AddSecret("TOKEN", CommandTarget.Here), EnvyCommand.Parse(["add", "TOKEN", "--secret"], Repo));
        Assert.Equal(new EnvyCommand.ListVariables("Global"), EnvyCommand.Parse(["list", "--project", "global"], Repo));
        Assert.Equal(new EnvyCommand.Import(Repo + @"\.env", CommandTarget.Here, true), EnvyCommand.Parse(["import", ".env", "--keep-existing"], Repo));
    }

    [Fact]
    public void RejectsBadInput()
    {
        WindowsOnly.Check();
        var config = Config();
        var before = config.Clone();
        Assert.NotNull(UsageError(config, ["set", "NOVALUE"]));
        Assert.NotNull(UsageError(config, ["set", "1BAD=x"]));
        Assert.NotNull(UsageError(config, ["set", "__ENVY_VARS=x"]));
        Assert.NotNull(UsageError(config, ["add", "TOKEN=abc", "--secret"]));
        Assert.NotNull(UsageError(config, ["set", "A=1", "--global", "--project", "api"]));
        Assert.NotNull(UsageError(config, ["off"]));
        Assert.NotNull(UsageError(config, ["frobnicate"]));
        Assert.NotNull(UsageError(config, ["set", "A=1", "--wat"]));
        Assert.NotNull(UsageError(config, ["status", "--global"]));
        Assert.NotNull(UsageError(config, ["link", "--global"]));
        Assert.True(config.ContentEquals(before));
    }

    // Targets

    [Fact]
    public void ChangesGoToTheDeepestProjectForThisFolder()
    {
        WindowsOnly.Check();
        var config = Config();
        Run(config, ["set", "A=1"], Repo + @"\web\src");
        Assert.Equal(["A"], config.Projects[1].Variables.Select(v => v.Key));
        Run(config, ["set", "B=2"], Repo + @"\lib");
        Assert.Equal(["PORT", "B"], config.Projects[0].Variables.Select(v => v.Key));
    }

    [Fact]
    public void OutsideAnyProjectAsksForATarget()
    {
        WindowsOnly.Check();
        var config = Config();
        var message = UsageError(config, ["set", "A=1"], @"C:\Temp");
        Assert.Contains("envy project add NAME --folder .", message);
        Assert.Contains("--global", message);
        Run(config, ["set", "A=1", "--global"], @"C:\Temp");
        Assert.Contains(config.Global.Variables, v => v.Key == "A");
    }

    // Secrets

    [Fact]
    public void AgentsCantWriteOrSeeSecrets()
    {
        WindowsOnly.Check();
        var config = Config();
        Assert.Contains("envy add GITHUB_TOKEN --secret --project api", UsageError(config, ["set", "GITHUB_TOKEN=ghp_x"]));
        Assert.Contains("is a secret", UsageError(config, ["set", "OPENAI_API_KEY=x", "--global", "--plain"]));
        // Names compare as Windows does, without regard to case.
        Assert.Contains("is a secret", UsageError(config, ["set", "openai_api_key=x", "--global", "--plain"]));
        Assert.Contains("--project \"api web\"", UsageError(config, ["set", "DB_PASSWORD=x"], Repo + @"\web"));
        Run(config, ["set", "PUBLIC_KEY=abc", "--plain"]);
        Assert.False(config.Projects[0].Variables[^1].IsSecret);

        var list = Run(config, ["list"]).Output;
        Assert.DoesNotContain("sk-live-123", list);
        Assert.Contains("  OPENAI_API_KEY  secret", list);
        Assert.Contains("  EDITOR=code --wait", list);
        Assert.Contains("Tools (off, every terminal)", list);
    }

    [Fact]
    public void AddSecretAsksTheApp()
    {
        WindowsOnly.Check();
        var config = Config();
        var before = config.Clone();
        var outcome = Run(config, ["add", "STRIPE_KEY", "--secret"]);
        Assert.Equal(new AppRequest.PasteSecret(config.Projects[0].Id, "STRIPE_KEY"), outcome.Request);
        Assert.Null(outcome.ActionName);
        Assert.Contains("envy list api", outcome.Output);
        Assert.True(config.ContentEquals(before));

        var again = Run(config, ["add", "OPENAI_API_KEY", "--secret", "--global"]);
        Assert.Contains("already has a value there", again.Output);
    }

    [Fact]
    public void ImportKeepsValuesOutOfTheOutput()
    {
        WindowsOnly.Check();
        var file = Write("PORT=9090\nDATABASE_PASSWORD=hunter2\nexport NAME='x y'\n", ".env");
        var config = Config();
        var outcome = Run(config, ["import", file]);
        Assert.DoesNotContain("hunter2", outcome.Output);
        Assert.DoesNotContain("9090", outcome.Output);
        Assert.Contains("DATABASE_PASSWORD", outcome.Output);
        Assert.Contains("Imported 3 variables from .env into api: 2 added, 1 replaced, 0 kept as they were.", outcome.Output);
        Assert.Contains("Stored as secrets in Windows Credential Manager: DATABASE_PASSWORD. Values aren't shown.", outcome.Output);
        var vars = config.Projects[0].Variables;
        Assert.Equal("9090", vars.Single(v => v.Key == "PORT").Value);
        Assert.True(vars.Single(v => v.Key == "DATABASE_PASSWORD").IsSecret);
        Assert.Equal("x y", vars.Single(v => v.Key == "NAME").Value);
        Assert.Equal("PORT=9090\nDATABASE_PASSWORD=hunter2\nexport NAME='x y'\n", File.ReadAllText(file));
    }

    // Other changes

    [Fact]
    public void OnOffRemoveAndProjects()
    {
        WindowsOnly.Check();
        var config = Config();
        Run(config, ["off", "port"]);
        Assert.False(config.Projects[0].Variables[0].IsEnabled);
        Run(config, ["on", "--project", "tools"]);
        Assert.True(config.Projects[2].IsEnabled);
        Run(config, ["remove", "PORT"]);
        Assert.Empty(config.Projects[0].Variables);
        Assert.NotNull(UsageError(config, ["remove", "NOPE"]));

        Run(config, ["project", "add", "cli", "--folder", @"C:\Users\me\Developer\cli"]);
        Assert.Equal([@"C:\Users\me\Developer\cli"], config.Projects[^1].Folders);
        Assert.NotNull(UsageError(config, ["project", "add", "CLI"]));
        // The same folder, spelled differently.
        Assert.Contains("already linked to api", UsageError(config, ["project", "add", "other", "--folder", @"c:/users/ME/developer/API/"]));
        var unlink = Run(config, ["unlink", "--project", "cli"], @"C:\Users\me\Developer\cli");
        Assert.Contains("no folders left", unlink.Output);
        Assert.Empty(config.Projects[^1].Folders);
    }

    [Fact]
    public void StatusReportsWhatThisProcessIsMissing()
    {
        WindowsOnly.Check();
        var config = Config();
        var fresh = Run(config, ["status"], Repo + @"\src", ["EDITOR", "OPENAI_API_KEY", "PORT"]).Output;
        Assert.Contains("Envy loads here: Global, api (later ones win).", fresh);
        Assert.DoesNotContain("envy exec", fresh);

        var stale = Run(config, ["status"], Repo, ["EDITOR", "OLD"]).Output;
        Assert.Contains("missing OPENAI_API_KEY PORT; still has OLD", stale);
        Assert.Contains("envy exec -- <command>", stale);

        config.Projects[0].IsEnabled = false;
        var off = Run(config, ["status"], Repo, ["EDITOR", "OPENAI_API_KEY"]).Output;
        Assert.Contains("api is linked to this folder but switched off.", off);
        Assert.Equal("Envy loads nothing here.", Run(new EnvConfig(), ["status"]).Output);
    }

    // Folders

    [Fact]
    public void FolderMatchingIsByWholeFolderIgnoringCaseAndSeparators()
    {
        WindowsOnly.Check();
        var config = Config();
        Assert.Equal(["api"], config.FolderProjects([Repo]).Select(p => p.Name));
        Assert.Equal(["api", "api web"], config.FolderProjects([Repo + @"\web\x"]).Select(p => p.Name));
        Assert.Equal(["api"], config.FolderProjects([@"c:/USERS/me/developer/Api/src"]).Select(p => p.Name));
        Assert.Empty(config.FolderProjects([Repo + "-old"]));
    }

    [Fact]
    public void AJunctionAndItsTargetAreTheSameFolder()
    {
        WindowsOnly.Check();
        if (!OperatingSystem.IsWindows()) Assert.Skip("Junctions are Windows only.");
        var real = Folder("real");
        Folder("real/src");
        var link = Path.Combine(Root, "link");
        Shells.Run(Shells.Require(Shells.Cmd, "cmd"), ["/d", "/c", "mklink", "/J", link, real], Root, Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.OrdinalIgnoreCase), cmdLine: true);
        Assert.True(Directory.Exists(Path.Combine(link, "src")));
        foreach (var (linked, cwd) in new[] { (link, Path.Combine(real, "src")), (real, Path.Combine(link, "src")) })
        {
            var config = new EnvConfig(projects: [new Project("p", folders: [linked])]);
            Assert.Equal(["p"], config.FolderProjects([cwd]).Select(p => p.Name));
        }
    }
}

/// <summary>The app's side of the envy command: one undoable step, saved right away.</summary>
public class CommandHandlingTests : TempDirectoryTest
{
    CommandRequest Request(string cwd, params string[] args) => new() { Arguments = [.. args], Context = new RequestContext { Directories = [cwd] } };

    [Fact]
    public void AChangeIsOneUndoStepAndReachesTheShellFilesAtOnce()
    {
        var store = new EnvyStore(new AppPaths(Root));
        var folder = Folder("code/api");
        store.CreateProject("api", folder, null, [new EnvVar("PORT", "1")]);

        var (response, request) = store.HandleCommand(Request(Path.Combine(folder, "src"), "set", "PORT=2", "HOST=localhost"));
        Assert.Null(request);
        Assert.Equal(0, response.ExitCode);
        Assert.Equal("Updated PORT in api.", response.Output.Split('\n')[1]);
        Assert.Equal("Set Variables", store.UndoName);
        Assert.Contains("localhost", File.ReadAllText(store.Output.ExecVars));
        Assert.Contains("localhost", File.ReadAllText(store.Output.PowerShellVars));

        store.Undo();
        Assert.Equal(["PORT"], store.Config.Projects[0].Variables.Select(v => v.Key));
        Assert.Equal("New Project", store.UndoName);
    }

    [Fact]
    public void ReadsAndMistakesChangeNothing()
    {
        var store = new EnvyStore(new AppPaths(Root));
        var (list, _) = store.HandleCommand(Request(Root, "list"));
        Assert.Equal("Global (on, every terminal)\n  no variables", list.Output);
        var (wrong, _) = store.HandleCommand(Request(Root, "set", "A=1"));
        Assert.Equal(2, wrong.ExitCode);
        Assert.Contains("No Envy project is linked to", wrong.Error);
        Assert.Null(store.UndoName);

        var (secret, request) = store.HandleCommand(Request(Root, "add", "API_TOKEN", "--secret", "--global"));
        Assert.True(secret.Activate);
        Assert.Equal(new AppRequest.PasteSecret(Project.GlobalId, "API_TOKEN"), request);
        Assert.Null(store.UndoName);
    }
}
