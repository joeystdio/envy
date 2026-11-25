using System.Text.Json;

namespace Envy.Core.Tests;

/// <summary>Backups (spec/backup-format.md): the encrypted file, the shared samples, restoring and the Expand suggestions.</summary>
public class BackupTests : TempDirectoryTest
{
    const string SamplePassword = "correct horse battery";
    const int FastIterations = 100_000;

    static string Samples
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var samples = Path.Combine(dir.FullName, "spec", "backup-samples");
                if (Directory.Exists(samples)) return samples;
            }
            throw new DirectoryNotFoundException("spec/backup-samples");
        }
    }

    static string Sample(string name) => File.ReadAllText(Path.Combine(Samples, name));

    static string Json(BackupContent content) => JsonSerializer.Serialize(content, EnvyJson.Default.BackupContent).ReplaceLineEndings("\n") + "\n";

    // The file

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    public void OpensTheSharedSamples(string name)
    {
        var content = Backup.Read(Sample(name + Backup.Extension), SamplePassword);
        Assert.Equal(Sample(name + ".expected.json").ReplaceLineEndings("\n"), Json(content));
    }

    [Fact]
    public void RefusesWithAMessageForTheUser()
    {
        string Error(string text, string password = SamplePassword) => Assert.Throws<BackupException>(() => Backup.Read(text, password)).Message;
        Assert.Equal("Wrong password, or the file is damaged.", Error(Sample("windows.envybackup"), "correct horse batterY"));
        Assert.Equal("Wrong password, or the file is damaged.", Error(Sample("damaged.envybackup")));
        Assert.Equal("This backup was made by a newer version of Envy. Update Envy to restore it.", Error(Sample("newer-version.envybackup")));
        Assert.Equal("This isn't an Envy backup.", Error("PORT=1\n"));
        Assert.Equal("This isn't an Envy backup.", Error("{\"format\": \"something-else\"}"));
        Assert.Equal("This backup is damaged.", Error(Sample("windows.envybackup").Replace("\"iterations\": 600000", "\"iterations\": 1000")));
    }

    [Fact]
    public void RoundTripsWithAFreshSaltAndNonceEachTime()
    {
        var content = Backup.FromConfig(new EnvConfig(), Root, "Envy test", new DateTime(2026, 11, 25, 5, 0, 0, DateTimeKind.Utc));
        content.Global.Variables.Add(new BackupVariable { Key = "TOKEN", Value = "ünïcødé secret ✓", IsSecret = true });
        var first = Backup.Write(content, "twelve chars!", FastIterations);
        var second = Backup.Write(content, "twelve chars!", FastIterations);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("secret", first);
        Assert.DoesNotContain("TOKEN", first);
        Assert.Equal(Json(content), Json(Backup.Read(second, "twelve chars!")));
        // The same password typed in decomposed form opens it too.
        Assert.Equal(Json(content), Json(Backup.Read(Backup.Write(content, "café au lait!", FastIterations), "cafe\u0301 au lait!")));
    }

    [Fact]
    public void PasswordRules()
    {
        Assert.Equal("Use at least 12 characters.", Backup.PasswordProblem("short", "short"));
        Assert.Equal("The passwords don't match.", Backup.PasswordProblem("twelve chars!", "twelve chars?"));
        Assert.Null(Backup.PasswordProblem("twelve chars!", "twelve chars!"));
        Assert.Equal("Envy Backup 2026-11-25.envybackup", Backup.SuggestedFileName(new DateTime(2026, 11, 25)));
    }

    [Fact]
    public void FoldersUnderHomeTravelAsTildePaths()
    {
        WindowsOnly.Check();
        Assert.Equal("~/code/api", Backup.PortableFolder(@"C:\Users\me\code\api", @"C:\Users\me"));
        Assert.Equal("~/code/api", Backup.PortableFolder(@"c:\users\ME\code\api\", @"C:\Users\me"));
        Assert.Equal("~", Backup.PortableFolder(@"C:\Users\me", @"C:\Users\me\"));
        Assert.Equal(@"C:\Users\meow\x", Backup.PortableFolder(@"C:\Users\meow\x", @"C:\Users\me"));
        Assert.Equal(@"D:\work", Backup.PortableFolder(@"D:\work", @"C:\Users\me"));
        Assert.Equal(@"C:\Users\you\Developer\api", Backup.LocalFolder("~/Developer/api", @"C:\Users\you"));
        Assert.Equal(@"C:\Users\you", Backup.LocalFolder("~", @"C:\Users\you"));
        Assert.Equal("/Volumes/Code/api-old", Backup.LocalFolder("/Volumes/Code/api-old", @"C:\Users\you"));
    }

    // Restoring

    static BackupContent MacSample => Backup.Read(Sample("macos.envybackup"), SamplePassword);

    [Fact]
    public void AddMergesByNameAndNeverDeletes()
    {
        WindowsOnly.Check();
        var home = Root;
        var web = Folder("Developer/web");
        var config = new EnvConfig(
            new Project("Global", id: Project.GlobalId, variables: [new EnvVar("path", "mine"), new EnvVar("KEEP", "1")]),
            [
                new Project("api", isEnabled: false, variables: [new EnvVar("PORT", "1"), new EnvVar("LOCAL", "x")]),
                new Project("frontend", folders: [web]),
                new Project("Mine"),
            ]);

        var report = BackupRestore.Apply(config, MacSample, RestoreMode.Add, home);

        // Global: PATH (any case) replaced by the backup's; KEEP stays.
        Assert.Equal(["PATH", "KEEP", "TOOLS", "APP_CONFIG", "CACHE", "GITHUB_TOKEN"], config.Global.Variables.Select(v => v.Key));
        Assert.True(config.Global.Variables.Single(v => v.Key == "GITHUB_TOKEN").IsSecret);
        // "API" merged into "api": the backup wins, local variables stay, the project stays off, folders are added.
        var api = config.Projects[0];
        Assert.Equal("api", api.Name);
        Assert.False(api.IsEnabled);
        Assert.Equal(["PORT=5000", "LOCAL=x", "NODE_ENV=development"], api.Variables.Select(v => $"{v.Key}={v.Value}"));
        Assert.Equal([Path.Combine(home, "Developer", "api"), "/Volumes/Code/api-old"], api.Folders);
        // "web" is new, but its only folder belongs to "frontend" here: not linked again, and switched off.
        var restoredWeb = config.Projects.Single(p => p.Name == "web");
        Assert.Empty(restoredWeb.Folders);
        Assert.False(restoredWeb.IsEnabled);
        Assert.Equal([web], config.Projects.Single(p => p.Name == "frontend").Folders);
        Assert.Contains(config.Projects, p => p.Name == "Mine");

        Assert.Equal(["api"], report.ProjectsMerged);
        Assert.Equal(["web"], report.ProjectsAdded);
        Assert.Empty(report.ProjectsRemoved);
        Assert.Equal([("web", web, "frontend")], report.SkippedFolders);
        Assert.Equal(["web"], report.TurnedOff);
        Assert.Equal(1, report.Secrets);
        Assert.Equal(2, report.VariablesReplaced); // PATH, PORT
        Assert.Equal(6, report.VariablesAdded);    // 4 in Global, NODE_ENV, PUBLIC_URL
        Assert.Equal([("api", Path.Combine(home, "Developer", "api")), ("api", "/Volumes/Code/api-old")], report.MissingFolders);
    }

    [Fact]
    public void ReplaceMakesItExactlyTheBackup()
    {
        WindowsOnly.Check();
        var config = new EnvConfig(new Project("Global", id: Project.GlobalId, variables: [new EnvVar("KEEP", "1")]), [new Project("Mine")]);
        var report = BackupRestore.Apply(config, MacSample, RestoreMode.Replace, Root, folderExists: _ => true);
        Assert.Equal(Project.GlobalId, config.Global.Id);
        Assert.DoesNotContain(config.Global.Variables, v => v.Key == "KEEP");
        Assert.Equal(["API", "web"], config.Projects.Select(p => p.Name));
        Assert.Equal(["Mine"], report.ProjectsRemoved);
        Assert.Equal(["API", "web"], report.ProjectsAdded);
        Assert.Empty(report.MissingFolders);
        Assert.Equal(config.Projects.Count, config.Projects.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void SuggestsRewritesForMacValuesOnWindows()
    {
        var issues = BackupRestore.ExpandIssues(MacSample, Backup.Windows);
        Assert.Equal(
            [
                ("Global", "PATH", @"%USERPROFILE%\bin;$PATH", ExpandAction.UseSuggestion),
                ("Global", "TOOLS", null, ExpandAction.KeepAsIs),
                ("Global", "APP_CONFIG", @"%USERPROFILE%\.config\app", ExpandAction.UseSuggestion),
            ],
            issues.Select(i => (i.ScopeName, i.Key, i.Suggestion, i.DefaultAction)));
        Assert.Equal("$HOME isn't set in PowerShell or Command Prompt. Windows separates folders with ;.", issues[0].Reason);
        Assert.Empty(BackupRestore.ExpandIssues(MacSample, Backup.MacOS));
    }

    [Fact]
    public void SuggestsRewritesForWindowsValuesOnAMac()
    {
        var windows = Backup.Read(Sample("windows.envybackup"), SamplePassword);
        var issues = BackupRestore.ExpandIssues(windows, Backup.MacOS);
        Assert.Equal(
            [
                ("Global", "PATH", "$HOME/bin:${PATH}", ExpandAction.UseSuggestion),
                ("Global", "JAVA_BIN", "${JAVA_HOME}/bin", ExpandAction.UseSuggestion),
                ("Global", "WHO", null, ExpandAction.TurnOff),
                ("api", "API_URL", "http://localhost:${PORT}/api", ExpandAction.UseSuggestion),
            ],
            issues.Select(i => (i.ScopeName, i.Key, i.Suggestion, i.DefaultAction)));
        Assert.Empty(BackupRestore.ExpandIssues(windows, Backup.Windows));
    }

    [Fact]
    public void AppliesTheChoicesForExpandingValues()
    {
        var config = new EnvConfig();
        var fixes = new List<ExpandFix>
        {
            new(-1, 0, ExpandAction.UseSuggestion, @"%USERPROFILE%\my-bin;%PATH%"),
            new(-1, 1, ExpandAction.TurnOff),
            new(-1, 2, ExpandAction.KeepAsIs, "ignored"),
        };
        BackupRestore.Apply(config, MacSample, RestoreMode.Replace, Root, fixes, _ => true);
        var global = config.Global.Variables;
        Assert.Equal(@"%USERPROFILE%\my-bin;%PATH%", global[0].Value);
        Assert.True(global[0].Expands);
        Assert.Equal("$(brew --prefix)/bin", global[1].Value);
        Assert.False(global[1].Expands);
        Assert.Equal("${HOME}/.config/app", global[2].Value);
        Assert.True(global[2].Expands);
    }

    // The app's side

    [Fact]
    public void BackUpOnOneMachineAndRestoreOnAnother()
    {
        var first = new EnvyStore(new AppPaths(Path.Combine(Root, "one")));
        var api = Folder("one/code/api");
        first.CreateProject("api", api, null, [new EnvVar("PORT", "4000"), new EnvVar("DB_PASSWORD", "s3cret", isSecret: true), new EnvVar("OFF", "1", isEnabled: false)]);
        first.AddVariables([new EnvVar("EDITOR", "code")], Project.GlobalId, MergePolicy.Replace, "Add");
        var file = first.CreateBackup("twelve chars!", "Envy test", FastIterations);

        var second = new EnvyStore(new AppPaths(Path.Combine(Root, "two")));
        second.CreateProject("Mine", null, null, []);
        var content = Backup.Read(file, "twelve chars!");
        var preview = second.PreviewRestore(content, RestoreMode.Replace);
        Assert.Equal(["Mine"], preview.ProjectsRemoved);
        Assert.Equal("Mine", second.Config.Projects.Single().Name); // a preview changes nothing

        second.RestoreBackup(content, RestoreMode.Replace);
        Assert.Equal("Restore Backup", second.UndoName);
        var restored = second.Config.Projects.Single();
        Assert.Equal("api", restored.Name);
        Assert.Equal([Path.Combine(Root, "two", "code", "api")], restored.Folders); // ~/code/api under the new home
        Assert.Equal(["PORT=4000", "DB_PASSWORD=s3cret", "OFF=1"], restored.Variables.Select(v => $"{v.Key}={v.Value}"));
        Assert.True(restored.Variables[1].IsSecret);
        Assert.False(restored.Variables[2].IsEnabled);
        Assert.Contains("s3cret", File.ReadAllText(Path.Combine(second.Paths.SettingsDirectory, "secrets.json")));
        Assert.DoesNotContain("s3cret", File.ReadAllText(second.Paths.ConfigFile));

        second.Undo();
        Assert.Equal("Mine", second.Config.Projects.Single().Name);
    }
}
