namespace Envy.Core;

/// <summary>
/// App state without UI. Every change to the config should be followed by <see cref="Save"/> (the app debounces it):
/// settings go to %APPDATA%\Envy\config.json, secrets to Credential Manager, and the generated shell files, which open
/// shells pick up at their next prompt.
/// </summary>
public sealed class EnvyStore
{
    public AppPaths Paths { get; }
    public EnvConfig Config { get; private set; }
    public string? Problem { get; private set; }
    public bool SecretsLocked { get; private set; }
    public DateTime? LastWrite { get; private set; }
    public string? AppPath { get; }

    /// <summary>Raised after every change to <see cref="Config"/>; <c>structural</c> is false for edits typed into a field.</summary>
    public event Action<bool>? Changed;
    /// <summary>Raised when <see cref="Problem"/>, saving or shell connections change.</summary>
    public event Action? StatusChanged;

    readonly ISecretVault vault;
    readonly IAutoRunStore autoRun;
    Dictionary<string, string> savedSecrets = [];
    readonly List<(string Name, EnvConfig Snapshot)> undo = [];
    readonly List<(string Name, EnvConfig Snapshot)> redo = [];

    /// <param name="commandSource">The folder holding the app's envy.exe, installed into the shell folder's bin\.</param>
    public EnvyStore(AppPaths paths, string? appPath = null, ISecretVault? vault = null, IAutoRunStore? autoRun = null, string? commandSource = null)
    {
        Paths = paths;
        AppPath = appPath;
        CommandSource = commandSource;
        this.vault = vault ?? paths.Vault;
        this.autoRun = autoRun ?? paths.AutoRunStore;
        var loaded = new EnvConfig();
        string? startupProblem = null;
        try
        {
            loaded = ConfigFile.Read(paths.ConfigFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ConfigFile.SetAside(paths.ConfigFile);
            startupProblem = $"Envy couldn't read its settings ({e.Message}). The unreadable file was kept as config.unreadable.json.";
        }
        try
        {
            savedSecrets = this.vault.Load();
            loaded.FillSecrets(savedSecrets);
        }
        catch (Exception e)
        {
            SecretsLocked = true;
            startupProblem = $"Envy can't read your secrets from {this.vault.Location} ({e.Message}), so your shells won't be updated until it can.";
        }
        Config = loaded;
        Problem = startupProblem;
        // Brings the generated files up to date, e.g. after an app update changed a hook.
        if (startupProblem is null) Save();
    }

    public string? CommandSource { get; }

    public ShellOutput Output => new(Paths.ShellDirectory, AppPath, Paths.Home, CommandSource, Paths.IsSandbox ? Paths.Home : null);

    // Saving

    public void Save()
    {
        try
        {
            ConfigFile.Write(Paths.ConfigFile, Config);
            if (SecretsLocked) return;
            var secrets = Config.SecretValues();
            if (!SameSecrets(secrets, savedSecrets))
            {
                if (vault is CredentialManagerVault credentials && OperatingSystem.IsWindows())
                    credentials.Save(secrets, Config.Scopes.SelectMany(p => p.Variables).Where(v => v.IsSecret).ToDictionary(v => v.Id.ToString(), v => v.Key));
                else
                    vault.Save(secrets);
                savedSecrets = secrets;
            }
            Output.Write(Config);
            LastWrite = DateTime.Now;
            Problem = null;
        }
        catch (Exception e)
        {
            Problem = e.Message;
        }
        StatusChanged?.Invoke();
    }

    static bool SameSecrets(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public void RetrySecrets()
    {
        try
        {
            savedSecrets = vault.Load();
            SecretsLocked = false;
            Config.FillSecrets(savedSecrets);
            Save();
        }
        catch (Exception e)
        {
            Problem = $"Envy still can't read your secrets from {vault.Location} ({e.Message}).";
            StatusChanged?.Invoke();
        }
    }

    // Changes and undo

    /// <summary>Applies <paramref name="change"/> as one undoable step named <paramref name="actionName"/>.</summary>
    public void Perform(string actionName, Action<EnvConfig> change)
    {
        var before = Config.Clone();
        change(Config);
        if (Config.ContentEquals(before)) return;
        undo.Add((actionName, before));
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        Changed?.Invoke(true);
    }

    /// <summary>A change typed into a field: saved, but not its own undo step.</summary>
    public void Edit(Action<EnvConfig> change)
    {
        change(Config);
        Changed?.Invoke(false);
    }

    public string? UndoName => undo.Count > 0 ? undo[^1].Name : null;
    public string? RedoName => redo.Count > 0 ? redo[^1].Name : null;

    public void Undo() => Swap(undo, redo);
    public void Redo() => Swap(redo, undo);

    void Swap(List<(string Name, EnvConfig Snapshot)> from, List<(string Name, EnvConfig Snapshot)> to)
    {
        if (from.Count == 0) return;
        var (name, snapshot) = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add((name, Config.Clone()));
        Config = snapshot;
        Changed?.Invoke(true);
    }

    // The envy command

    /// <summary>
    /// Runs one envy command. A change is one undoable step, saved right away so the next envy exec sees it. The
    /// request, if any, is for the window: a dialog to paste a secret into, or the window itself.
    /// </summary>
    public (CommandResponse Response, AppRequest? Request) HandleCommand(CommandRequest request)
    {
        try
        {
            var command = EnvyCommand.Parse(request.Arguments, request.Context.Directories.FirstOrDefault() ?? Paths.Home, Paths.Home);
            var updated = Config.Clone();
            var outcome = command.Run(updated, request.CommandContext, Paths.Home);
            if (outcome.ActionName is { } actionName && !updated.ContentEquals(Config))
            {
                if (SecretsLocked)
                    return (new CommandResponse { Error = $"Envy can't read your secrets from {vault.Location}, so it can't change anything right now. Open Envy and choose Try Again.", ExitCode = 1 }, null);
                Perform(actionName, c =>
                {
                    c.Global = updated.Global;
                    c.Projects = updated.Projects;
                });
                Save();
                if (Problem is { } problem)
                    return (new CommandResponse { Output = outcome.Output, Error = $"Saved, but terminals won't see it yet: {problem}", ExitCode = 1 }, null);
            }
            return (new CommandResponse { Output = outcome.Output, Activate = outcome.Request is not null }, outcome.Request);
        }
        catch (EnvyUsageException e)
        {
            return (new CommandResponse { Error = e.Message, ExitCode = 2 }, null);
        }
        catch (Exception e)
        {
            return (new CommandResponse { Error = e.Message, ExitCode = 1 }, null);
        }
    }

    // Coding agents

    /// <summary>Claude Code's settings folder when a shell (or your user environment) sets CLAUDE_CONFIG_DIR.</summary>
    public string? ClaudeConfigDirectory =>
        Paths.IsSandbox ? null
        : Inspections.Values.Select(i => i?.ClaudeConfigDirectory).FirstOrDefault(d => !string.IsNullOrEmpty(d))
          ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

    public List<AgentTarget> AgentTargets => AgentInstructions.Targets(Paths.Home, ClaudeConfigDirectory);

    /// <summary>The agents a target reaches that a shell found on its PATH.</summary>
    public List<string> FoundAgents(AgentTarget target)
    {
        var found = Inspections.Values.Where(i => i is not null).SelectMany(i => i!.Agents).ToHashSet();
        return target.Agents.Select(a => a.Name).Where(found.Contains).ToList();
    }

    /// <summary>Whether one of the target's agents is on a shell's PATH or has left its settings folder here.</summary>
    public bool IsPresent(AgentTarget target) => FoundAgents(target).Count > 0 || target.ConfigDirectories.Any(Directory.Exists);

    public void InstallInstructions(IEnumerable<AgentTarget> targets)
    {
        try { foreach (var target in targets) AgentInstructions.Install(target); }
        finally { StatusChanged?.Invoke(); }
    }

    public void RemoveInstructions(AgentTarget target)
    {
        try { AgentInstructions.Uninstall(target); }
        finally { StatusChanged?.Invoke(); }
    }

    public bool AgentOfferAnswered => File.Exists(Paths.AgentOfferMarker);

    /// <summary>For people who set Envy up before it could teach agents: asks once, while none have instructions.</summary>
    public bool ShouldOfferAgentInstructions
    {
        get
        {
            if (!HasOnboarded || AgentOfferAnswered) return false;
            var targets = AgentTargets;
            return targets.Any(IsPresent) && targets.All(t => AgentInstructions.Status(t) == AgentInstructionStatus.NotInstalled);
        }
    }

    /// <summary>Installs instructions for the agents found here (leaving files Envy didn't write alone) and stops asking.</summary>
    public void AcceptAgentOffer()
    {
        try
        {
            InstallInstructions(AgentTargets.Where(t => IsPresent(t) && AgentInstructions.Status(t) != AgentInstructionStatus.Foreign));
        }
        finally
        {
            DismissAgentOffer();
        }
    }

    public void DismissAgentOffer()
    {
        Directory.CreateDirectory(Paths.SettingsDirectory);
        File.WriteAllText(Paths.AgentOfferMarker, "");
        StatusChanged?.Invoke();
    }

    /// <summary>After an update of Envy, rewrites instructions an older version wrote. Never adds new ones.</summary>
    public void UpdateOutdatedInstructions()
    {
        foreach (var target in AgentTargets.Where(t => AgentInstructions.Status(t) == AgentInstructionStatus.Outdated))
        {
            try { AgentInstructions.Install(target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Takes Envy out of every shell and removes its instructions for coding agents, when the app is uninstalled.</summary>
    public void Uninstall()
    {
        DisconnectAll();
        var targets = AgentInstructions.Targets(Paths.Home).Concat(AgentTargets).DistinctBy(t => t.File.ToLowerInvariant());
        foreach (var target in targets)
        {
            try { AgentInstructions.Uninstall(target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // Projects

    public string UniqueProjectName(string baseName)
    {
        var taken = Config.Scopes.Select(p => p.Name).ToHashSet();
        var root = string.IsNullOrWhiteSpace(baseName) ? "New Project" : baseName.Trim();
        if (!taken.Contains(root)) return root;
        var n = 2;
        while (taken.Contains($"{root} {n}")) n++;
        return $"{root} {n}";
    }

    public Project CreateProject(string name, string? folder, Guid? copyFrom, IEnumerable<EnvVar> extra)
    {
        var project = new Project(UniqueProjectName(name), folders: folder is null ? null : [folder]);
        if (copyFrom is { } source && Config.Find(source) is { } original) project.Merge(original.Variables, MergePolicy.Replace);
        project.Merge(extra, MergePolicy.Replace);
        Perform("New Project", c => c.Projects.Add(project));
        return project;
    }

    /// <summary>The copy starts switched off and without folders, so nothing changes in any shell until you set it up.</summary>
    public Project? DuplicateProject(Guid id)
    {
        if (Config.Find(id) is not { IsGlobal: false } original) return null;
        var copy = new Project(UniqueProjectName(original.Name + " copy"), isEnabled: false);
        copy.Merge(original.Variables, MergePolicy.Replace);
        Perform("Duplicate Project", c =>
        {
            var index = c.Projects.FindIndex(p => p.Id == id);
            c.Projects.Insert(index < 0 ? c.Projects.Count : index + 1, copy);
        });
        return copy;
    }

    public void DeleteProject(Guid id)
    {
        if (id == Project.GlobalId) return;
        Perform("Delete Project", c => c.Projects.RemoveAll(p => p.Id == id));
    }

    public void MoveProject(Guid id, int toIndex)
    {
        Perform("Move Project", c =>
        {
            var from = c.Projects.FindIndex(p => p.Id == id);
            if (from < 0) return;
            var project = c.Projects[from];
            c.Projects.RemoveAt(from);
            c.Projects.Insert(Math.Clamp(toIndex, 0, c.Projects.Count), project);
        });
    }

    public void SetProjectEnabled(Guid id, bool enabled) =>
        Perform(enabled ? "Turn On Project" : "Turn Off Project", c => c.Update(id, p => p.IsEnabled = enabled));

    public void RenameProject(Guid id, string name) => Edit(c => c.Update(id, p => { if (!p.IsGlobal) p.Name = name; }));

    public void LinkFolder(string path, Guid id) =>
        Perform("Link Folder", c => c.Update(id, p => { if (!p.IsGlobal && !p.Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) p.Folders.Add(path); }));

    public void UnlinkFolder(string path, Guid id) =>
        Perform("Unlink Folder", c => c.Update(id, p => p.Folders.RemoveAll(f => f == path)));

    public string Subtitle(Project p)
    {
        var count = p.Variables.Count == 1 ? "1 variable" : $"{p.Variables.Count} variables";
        if (!p.IsEnabled) return $"{count} · off";
        if (p.Folders.Count > 0)
        {
            var more = p.Folders.Count > 1 ? $" +{p.Folders.Count - 1}" : "";
            return $"{count} · {Paths.Abbreviate(p.Folders[0])}{more}";
        }
        return $"{count} · every shell";
    }

    // Variables

    public MergeResult AddVariables(IEnumerable<EnvVar> vars, Guid id, MergePolicy policy, string actionName)
    {
        var result = new MergeResult();
        Perform(actionName, c => c.Update(id, p => result = p.Merge(vars, policy)));
        return result;
    }

    public void ReplaceVariable(EnvVar variable, Guid id) =>
        Perform("Edit Variable", c => c.Update(id, p =>
        {
            var i = p.Variables.FindIndex(v => v.Id == variable.Id);
            if (i >= 0) p.Variables[i] = variable with { };
        }));

    public void UpdateVariables(IReadOnlySet<Guid> ids, Guid id, string actionName, Action<EnvVar> change) =>
        Perform(actionName, c => c.Update(id, p => { foreach (var v in p.Variables.Where(v => ids.Contains(v.Id))) change(v); }));

    /// <summary>A single field typed into the table: saved, but not an undo step of its own.</summary>
    public void EditVariable(Guid projectId, Guid variableId, Action<EnvVar> change) =>
        Edit(c => c.Update(projectId, p => { if (p.Variables.Find(v => v.Id == variableId) is { } v) change(v); }));

    public void DeleteVariables(IReadOnlySet<Guid> ids, Guid id) =>
        Perform(ids.Count == 1 ? "Delete Variable" : "Delete Variables", c => c.Update(id, p => p.Variables.RemoveAll(v => ids.Contains(v.Id))));

    public void CopyVariables(IReadOnlySet<Guid> ids, Guid source, Guid target, bool move)
    {
        if (Config.Find(source) is not { } original) return;
        var vars = original.Variables.Where(v => ids.Contains(v.Id)).Select(v => v with { }).ToList();
        Perform(move ? "Move Variables" : "Copy Variables", c =>
        {
            c.Update(target, p => p.Merge(vars, MergePolicy.Replace));
            if (move) c.Update(source, p => p.Variables.RemoveAll(v => ids.Contains(v.Id)));
        });
    }

    public string DotEnvText(IReadOnlySet<Guid>? ids, Guid id) =>
        Config.Find(id) is { } p ? DotEnv.Serialize(p.Variables.Where(v => ids?.Contains(v.Id) ?? true)) : "";

    /// <summary>The enabled variables as .env text, secrets included, written so only you can read the file.</summary>
    public void ExportDotEnv(Guid id, string path)
    {
        if (Config.Find(id) is not { } p) return;
        var text = DotEnv.Serialize(p.Variables.Where(v => v.IsEnabled), $"Exported from Envy: {p.Name}");
        File.WriteAllText(path, text, PrivateFiles.Utf8);
    }

    // Shell integration

    public List<InstalledShell> Shells { get; private set; } = ShellLocator.Find();
    public System.Collections.Concurrent.ConcurrentDictionary<ShellKind, ShellInspection?> Inspections { get; } = [];

    public void RefreshShells()
    {
        Shells = ShellLocator.Find();
        StatusChanged?.Invoke();
    }

    public string InitShPosix => PathStyle.ToPosix(Output.BashHook);

    /// <summary>The PowerShell expression for init.ps1: portable when it's in the usual place, a quoted path otherwise.</summary>
    public string InitPs1Expression
    {
        get
        {
            var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Envy", "shell");
            return !Paths.IsSandbox && string.Equals(Paths.ShellDirectory, standard, StringComparison.OrdinalIgnoreCase)
                ? "\"$env:LOCALAPPDATA\\Envy\\shell\\init.ps1\""
                : PowerShellScript.Quoted(Output.PowerShellHook);
        }
    }

    /// <summary>The files (or registry value) connecting a shell, for display.</summary>
    public List<string> Targets(ShellKind kind) => kind switch
    {
        ShellKind.PowerShell7 => [Paths.PowerShell7Profile],
        ShellKind.WindowsPowerShell => [Paths.WindowsPowerShellProfile],
        ShellKind.GitBash => [Paths.BashRc, .. GitBashLoginFile() is { } login ? new[] { login } : []],
        _ => [autoRun.Location],
    };

    /// <summary>The line Envy adds for <paramref name="kind"/>.</summary>
    public string ConnectLine(ShellKind kind) => kind switch
    {
        ShellKind.PowerShell7 or ShellKind.WindowsPowerShell => StartupFiles.PowerShellLine(InitPs1Expression),
        ShellKind.GitBash => StartupFiles.BashLine(InitShPosix),
        _ => AutoRun.Segment(Output.CmdInit),
    };

    public bool IsConnected(ShellKind kind) => kind switch
    {
        ShellKind.PowerShell7 => StartupFiles.IsInstalled(Paths.PowerShell7Profile),
        ShellKind.WindowsPowerShell => StartupFiles.IsInstalled(Paths.WindowsPowerShellProfile),
        ShellKind.GitBash => StartupFiles.IsInstalled(Paths.BashRc),
        _ => AutoRun.IsInstalled(autoRun),
    };

    public bool AnyConnected => Enum.GetValues<ShellKind>().Any(IsConnected);

    /// <summary>
    /// Git Bash starts as a login shell, which reads the first of ~/.bash_profile, ~/.bash_login and ~/.profile. That
    /// file needs Envy's line too unless it already loads ~/.bashrc. Null when none exists (Envy then creates the
    /// ~/.bash_profile Git for Windows would create) or the one there loads ~/.bashrc.
    /// </summary>
    public string? GitBashLoginFile()
    {
        var login = new[] { Paths.BashProfile, Paths.BashLogin, Paths.Profile }.FirstOrDefault(File.Exists);
        if (login is null) return null;
        var text = StartupFiles.ReadText(StartupFiles.Resolve(login))?.Text ?? "";
        return text.Contains("bashrc") ? null : login;
    }

    public void Connect(ShellKind kind)
    {
        Save();
        switch (kind)
        {
            case ShellKind.PowerShell7:
                StartupFiles.Install(Paths.PowerShell7Profile, ConnectLine(kind), preferCrLf: true);
                break;
            case ShellKind.WindowsPowerShell:
                StartupFiles.Install(Paths.WindowsPowerShellProfile, ConnectLine(kind), preferCrLf: true);
                break;
            case ShellKind.GitBash:
                if (GitBashLoginFile() is { } login) StartupFiles.Install(login, ConnectLine(kind));
                StartupFiles.Install(Paths.BashRc, ConnectLine(kind));
                if (!new[] { Paths.BashProfile, Paths.BashLogin, Paths.Profile }.Any(File.Exists))
                    File.WriteAllText(Paths.BashProfile, "# generated by Envy, as Git for Windows does: login shells load ~/.bashrc\ntest -f ~/.profile && . ~/.profile\ntest -f ~/.bashrc && . ~/.bashrc\n");
                break;
            default:
                AutoRun.Install(autoRun, Output.CmdInit, Paths.AutoRunBackup);
                break;
        }
        Inspections.TryRemove(kind, out _);
        StatusChanged?.Invoke();
    }

    public void Disconnect(ShellKind kind)
    {
        switch (kind)
        {
            case ShellKind.PowerShell7: StartupFiles.Uninstall(Paths.PowerShell7Profile); break;
            case ShellKind.WindowsPowerShell: StartupFiles.Uninstall(Paths.WindowsPowerShellProfile); break;
            case ShellKind.GitBash:
                foreach (var file in new[] { Paths.BashRc, Paths.BashProfile, Paths.BashLogin, Paths.Profile }) StartupFiles.Uninstall(file);
                break;
            default: AutoRun.Uninstall(autoRun, Paths.AutoRunBackup); break;
        }
        Inspections.TryRemove(kind, out _);
        StatusChanged?.Invoke();
    }

    /// <summary>Takes Envy out of every shell, e.g. when the app is uninstalled.</summary>
    public void DisconnectAll()
    {
        foreach (var kind in Enum.GetValues<ShellKind>())
        {
            try { if (IsConnected(kind)) Disconnect(kind); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Opens a fresh shell of this kind in the background to see whether it loads Envy and which agents it can run.</summary>
    public ShellInspection? Inspect(InstalledShell shell)
    {
        ShellInspection? result = shell.Kind switch
        {
            ShellKind.PowerShell7 => ShellProbe.Inspect(shell, Paths.IsSandbox ? Paths.PowerShell7Profile : null),
            ShellKind.WindowsPowerShell => ShellProbe.Inspect(shell, Paths.IsSandbox ? Paths.WindowsPowerShellProfile : null),
            ShellKind.GitBash => ShellProbe.Inspect(shell, home: Paths.IsSandbox ? Paths.Home : null),
            _ => ShellProbe.Inspect(shell, autoRun: Paths.IsSandbox ? autoRun.Read() ?? "rem" : null),
        };
        Inspections[shell.Kind] = result;
        return result;
    }

    // Setup

    public bool HasOnboarded => File.Exists(Paths.OnboardedMarker);

    public void FinishOnboarding()
    {
        Directory.CreateDirectory(Paths.SettingsDirectory);
        File.WriteAllText(Paths.OnboardedMarker, "");
    }

    /// <summary>Variables set in plain text that Global doesn't already have with the same value; the last one per name wins.</summary>
    public List<PlaintextExport> PlaintextExports()
    {
        var found = PlaintextScanner.All(Paths.AllBashFiles, Paths.AllPowerShellProfiles, Paths.UserEnvironment);
        var latest = new Dictionary<string, PlaintextExport>(EnvVar.KeyComparer);
        var order = new List<string>();
        foreach (var e in found)
        {
            if (!latest.ContainsKey(e.Key)) order.Add(e.Key);
            latest[e.Key] = e;
        }
        return order.Select(k => latest[k])
            .Where(e => !Config.Global.Variables.Any(v => EnvVar.SameKey(v.Key, e.Key) && v.Value == e.Value))
            .ToList();
    }

    /// <summary>
    /// Removes the imported variables from where they were found: their lines from startup files (each file keeps an
    /// .envy-backup) and their names from your user environment variables.
    /// </summary>
    public void RemovePlaintext(IEnumerable<PlaintextExport> exports)
    {
        var list = exports.ToList();
        foreach (var group in list.Where(e => !e.IsUserEnvironment).GroupBy(e => e.Source))
            StartupFiles.RemoveLines(group.Key, group.Select(e => e.Line));
        var names = list.Where(e => e.IsUserEnvironment).Select(e => e.Key).ToList();
        if (names.Count > 0) Paths.UserEnvironment.Remove(names);
    }

    public bool IsLinked(string folder) =>
        Config.Projects.Any(p => p.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase));

    /// <summary>One folder-linked project per found folder that isn't linked yet.</summary>
    public int ImportProjects(IEnumerable<FoundProject> found)
    {
        var fresh = found.Where(f => !IsLinked(f.Folder)).ToList();
        Perform(fresh.Count == 1 ? "Import Project" : "Import Projects", c =>
        {
            foreach (var item in fresh)
            {
                var taken = c.Scopes.Select(p => p.Name).ToHashSet();
                var name = item.Name;
                for (var n = 2; taken.Contains(name); n++) name = $"{item.Name} {n}";
                var project = new Project(name, folders: [item.Folder]);
                project.Merge(item.Variables, MergePolicy.Replace);
                c.Projects.Add(project);
            }
        });
        return fresh.Count;
    }
}
