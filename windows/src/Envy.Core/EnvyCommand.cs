namespace Envy.Core;

/// <summary>A command that can't run as typed. The message is shown as it is, after "envy: ".</summary>
public sealed class EnvyUsageException(string message) : Exception(message);

/// <summary>Which scope a change goes to: the project linked to the working folder, Global, or a project by name.</summary>
public sealed record CommandTarget(string? Name, bool IsGlobal)
{
    public static readonly CommandTarget Here = new(null, false);
    public static readonly CommandTarget Global = new(null, true);
    public static CommandTarget Named(string name) => new(name, false);
    public bool IsHere => Name is null && !IsGlobal;
}

/// <summary>What the envy command's process knows that the app doesn't.</summary>
/// <param name="Directories">The working folder, as the shell shows it and with links resolved, so linked folders match either way.</param>
/// <param name="Loaded">Names Envy has set in the caller's environment (__ENVY_VARS).</param>
public sealed record CommandContext(IReadOnlyList<string> Directories, IReadOnlyList<string> Loaded);

/// <summary>Something only the app's window can do.</summary>
public abstract record AppRequest
{
    /// <summary>Ask the user to paste the value of a secret into the add-variable dialog.</summary>
    public sealed record PasteSecret(Guid Project, string Key) : AppRequest;
    public sealed record Show(Guid? Project) : AppRequest;
}

/// <summary>What running a command did. <see cref="ActionName"/> names the undo step when the settings changed.</summary>
public sealed record CommandOutcome(string Output, string? ActionName = null, AppRequest? Request = null);

/// <summary>
/// The envy command line, minus what runs in the caller's own process (exec, reload). It is parsed and run against the
/// app's settings inside the app, so the app stays the only writer: the command sends its arguments to the running
/// app, which applies them as one undoable change. Output never contains secret values.
/// </summary>
public abstract record EnvyCommand
{
    public sealed record Help : EnvyCommand;
    public sealed record Status : EnvyCommand;
    public sealed record ListVariables(string? Project) : EnvyCommand;
    public sealed record Set(IReadOnlyList<(string Key, string Value)> Assignments, CommandTarget Target, bool Expand, bool Plain) : EnvyCommand;
    public sealed record AddSecret(string Key, CommandTarget Target) : EnvyCommand;
    public sealed record Remove(IReadOnlyList<string> Keys, CommandTarget Target) : EnvyCommand;
    public sealed record Switch(IReadOnlyList<string> Keys, CommandTarget Target, bool On) : EnvyCommand;
    public sealed record AddProject(string Name, IReadOnlyList<string> Folders, bool Enabled) : EnvyCommand;
    public sealed record Link(string Folder, CommandTarget Target) : EnvyCommand;
    public sealed record Unlink(string Folder, CommandTarget Target) : EnvyCommand;
    public sealed record Import(string File, CommandTarget Target, bool KeepExisting) : EnvyCommand;
    public sealed record Open(string? Project) : EnvyCommand;

    /// <summary>Commands that only read, so they can also run from the settings file when the app can't be reached.</summary>
    public bool IsReadOnly => this is Help or Status or ListVariables;

    // Parsing

    /// <summary>Parses arguments as typed after envy. Relative folders are resolved against <paramref name="cwd"/>.</summary>
    public static EnvyCommand Parse(IReadOnlyList<string> arguments, string cwd, string? home = null)
    {
        var args = arguments.ToList();
        var verb = args.Count == 0 ? "status" : args[0];
        if (args.Count > 0) args.RemoveAt(0);

        var target = CommandTarget.Here;
        var targetGiven = false;
        var flags = new HashSet<string>();
        var folders = new List<string>();
        var positional = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value()
            {
                if (i + 1 >= args.Count) throw new EnvyUsageException($"{arg} needs a value.");
                return args[++i];
            }
            switch (arg)
            {
                case "--project" or "-p":
                {
                    var name = Value();
                    if (targetGiven) throw new EnvyUsageException("Choose one of --project and --global.");
                    target = name.Equals("Global", StringComparison.OrdinalIgnoreCase) ? CommandTarget.Global : CommandTarget.Named(name);
                    targetGiven = true;
                    break;
                }
                case "--global" or "-g":
                    if (targetGiven) throw new EnvyUsageException("Choose one of --project and --global.");
                    target = CommandTarget.Global;
                    targetGiven = true;
                    break;
                case "--folder":
                    folders.Add(Absolute(Value(), cwd, home));
                    break;
                case "--secret" or "--expand" or "--plain" or "--off" or "--keep-existing":
                    flags.Add(arg);
                    break;
                case "--help" or "-h":
                    return new Help();
                default:
                    if (arg.StartsWith('-') && arg.Length > 1 && !arg.Contains('='))
                        throw new EnvyUsageException($"Unknown option {arg}. Run envy help.");
                    positional.Add(arg);
                    break;
            }
        }

        void Allow(string[] allowed, bool targetAllowed = true)
        {
            if (flags.Except(allowed).Order(StringComparer.Ordinal).FirstOrDefault() is { } extra)
                throw new EnvyUsageException($"{extra} doesn't apply to envy {verb}.");
            if (!targetAllowed && targetGiven) throw new EnvyUsageException($"envy {verb} doesn't take --project or --global.");
            if (folders.Count > 0 && verb != "project") throw new EnvyUsageException("--folder only applies to envy project add.");
        }

        string? Scope(string? positionalName) =>
            target.IsGlobal ? "Global" : target.Name ?? positionalName;

        switch (verb)
        {
            case "help" or "-h" or "--help":
                return new Help();
            case "status":
                Allow([], targetAllowed: false);
                return new Status();
            case "list" or "ls":
                Allow([]);
                if (positional.Count > 1) throw new EnvyUsageException("Usage: envy list [PROJECT]");
                return new ListVariables(Scope(positional.FirstOrDefault()));
            case "set" or "add":
            {
                if (flags.Contains("--secret"))
                {
                    Allow(["--secret"]);
                    if (positional.Count != 1 || positional[0].Contains('='))
                        throw new EnvyUsageException("Usage: envy add NAME --secret  (no value: the user pastes it into Envy)");
                    CheckKey(positional[0]);
                    return new AddSecret(positional[0], target);
                }
                Allow(["--expand", "--plain"]);
                if (positional.Count == 0) throw new EnvyUsageException("Usage: envy set NAME=VALUE [NAME=VALUE…]");
                var assignments = positional.Select(item =>
                {
                    var eq = item.IndexOf('=');
                    if (eq < 0) throw new EnvyUsageException($"{item} has no value. Use NAME=VALUE, or envy add {item} --secret for a secret.");
                    var key = item[..eq];
                    CheckKey(key);
                    return (key, item[(eq + 1)..]);
                }).ToList();
                return new Set(assignments, target, flags.Contains("--expand"), flags.Contains("--plain"));
            }
            case "remove" or "rm" or "unset":
                Allow([]);
                if (positional.Count == 0) throw new EnvyUsageException("Usage: envy remove NAME… [--project PROJECT | --global]");
                return new Remove(positional, target);
            case "on" or "off":
                Allow([]);
                if (positional.Count == 0 && !targetGiven)
                    throw new EnvyUsageException("Name the variables, or the project with --project PROJECT (or --global).");
                return new Switch(positional, target, verb == "on");
            case "project":
            {
                if (positional.FirstOrDefault() is not ("add" or "new")) throw new EnvyUsageException("Usage: envy project add NAME [--folder PATH]");
                Allow(["--off"], targetAllowed: false);
                var name = string.Join(' ', positional.Skip(1)).Trim();
                if (name.Length == 0) throw new EnvyUsageException("Usage: envy project add NAME [--folder PATH]");
                return new AddProject(name, folders, !flags.Contains("--off"));
            }
            case "link" or "unlink":
            {
                Allow([]);
                if (positional.Count > 1) throw new EnvyUsageException($"Usage: envy {verb} [FOLDER] --project PROJECT");
                if (target.Name is null) throw new EnvyUsageException($"Name the project: envy {verb} [FOLDER] --project PROJECT");
                var folder = Absolute(positional.FirstOrDefault() ?? ".", cwd, home);
                return verb == "link" ? new Link(folder, target) : new Unlink(folder, target);
            }
            case "import":
                Allow(["--keep-existing"]);
                if (positional.Count != 1) throw new EnvyUsageException("Usage: envy import FILE [--project PROJECT | --global]");
                return new Import(Absolute(positional[0], cwd, home), target, flags.Contains("--keep-existing"));
            case "open":
                Allow([]);
                return new Open(Scope(positional.FirstOrDefault()));
            default:
                throw new EnvyUsageException($"Unknown command {verb}. Run envy help.");
        }
    }

    static void CheckKey(string key)
    {
        if (EnvVar.KeyProblem(key) is { } problem) throw new EnvyUsageException($"{key}: {problem}");
    }

    /// <summary>
    /// <paramref name="path"/> as a full Windows path: ~ is the user folder, /c/… (Git Bash) becomes C:\…, and a relative
    /// path is taken from <paramref name="cwd"/>, not from the app's own working folder.
    /// </summary>
    public static string Absolute(string path, string cwd, string? home = null)
    {
        var p = PathStyle.FromPosix(PathStyle.ExpandHome(path, home)).Replace('/', '\\');
        var basePath = PathStyle.FromPosix(cwd).Replace('/', '\\');
        try { return PathStyle.TrimTrailing(Path.GetFullPath(p, Path.GetFullPath(basePath))); }
        catch (ArgumentException) { throw new EnvyUsageException($"{path} isn't a valid folder or file name."); }
    }

    // Running

    /// <summary>Runs the command against <paramref name="config"/>, which it changes in place. Reads a file only for import.</summary>
    public CommandOutcome Run(EnvConfig config, CommandContext context, string? home = null)
    {
        switch (this)
        {
            case Help:
                return new(HelpText);

            case Status:
                return new(StatusText(config, context, home));

            case ListVariables(var name):
                if (name is not null) return new(Describe(FindProject(name, config)));
                return new(string.Join("\n", config.Scopes.Select(Describe)));

            case Set(var assignments, var target, var expand, var plain):
            {
                var p = Resolve(target, config, context, home);
                foreach (var (key, _) in assignments)
                {
                    if (p.Variables.Any(v => EnvVar.SameKey(v.Key, key) && v.IsSecret))
                        throw new EnvyUsageException($"{key} is a secret in {p.Name}. Only the user changes secrets: run envy add {key} --secret{Flag(p)} to ask them.");
                    if (!plain && EnvVar.LooksSecret(key))
                        throw new EnvyUsageException($"{key} looks like a secret. Run envy add {key} --secret{Flag(p)} so the user pastes it into Envy, or add --plain if it isn't one.");
                }
                List<string> added = [], changed = [];
                config.Update(p.Id, project =>
                {
                    foreach (var (key, value) in assignments)
                    {
                        var existing = project.Variables.Where(v => EnvVar.SameKey(v.Key, key)).ToList();
                        if (existing.Count == 0)
                        {
                            project.Variables.Add(new EnvVar(key, value, expands: expand));
                            added.Add(key);
                            continue;
                        }
                        foreach (var v in existing)
                        {
                            v.Value = value;
                            v.IsEnabled = true;
                            if (expand) v.Expands = true;
                        }
                        changed.Add(existing[0].Key);
                    }
                });
                var lines = new List<string>();
                if (added.Count > 0) lines.Add($"Added {Names(added)} to {p.Name}.");
                if (changed.Count > 0) lines.Add($"Updated {Names(changed)} in {p.Name}.");
                lines.AddRange(Notes(p.Id, config));
                return new(string.Join("\n", lines), assignments.Count == 1 ? "Set Variable" : "Set Variables");
            }

            case AddSecret(var key, var target):
            {
                var p = Resolve(target, config, context, home);
                var has = p.Variables.Any(v => EnvVar.SameKey(v.Key, key) && v.IsSecret && v.Value.Length > 0);
                var output = $"Opened Envy so the user can paste the value of {key} into {p.Name}. It's kept in Windows Credential Manager; you won't see it.\n"
                    + (has ? $"{key} already has a value there; saving replaces it.\n" : "")
                    + $"Tell the user it's waiting for them, then check with: envy list {Quoted(p.Name)}";
                return new(output, Request: new AppRequest.PasteSecret(p.Id, key));
            }

            case Remove(var keys, var target):
            {
                var p = Resolve(target, config, context, home);
                var missing = keys.Where(k => !p.Variables.Any(v => EnvVar.SameKey(v.Key, k))).ToList();
                if (missing.Count > 0) throw new EnvyUsageException($"{p.Name} has no {Names(missing)}.");
                config.Update(p.Id, project => project.Variables.RemoveAll(v => keys.Any(k => EnvVar.SameKey(k, v.Key))));
                return new($"Removed {Names(keys)} from {p.Name}.", keys.Count == 1 ? "Delete Variable" : "Delete Variables");
            }

            case Switch(var keys, var target, var on):
            {
                var p = Resolve(target, config, context, home);
                if (keys.Count == 0)
                {
                    config.Update(p.Id, project => project.IsEnabled = on);
                    return new($"{p.Name} is {(on ? "on" : "off")}.", on ? "Turn On Project" : "Turn Off Project");
                }
                var missing = keys.Where(k => !p.Variables.Any(v => EnvVar.SameKey(v.Key, k))).ToList();
                if (missing.Count > 0) throw new EnvyUsageException($"{p.Name} has no {Names(missing)}.");
                config.Update(p.Id, project =>
                {
                    foreach (var v in project.Variables.Where(v => keys.Any(k => EnvVar.SameKey(k, v.Key)))) v.IsEnabled = on;
                });
                var lines = new List<string> { $"Switched {Names(keys)} {(on ? "on" : "off")} in {p.Name}." };
                if (on) lines.AddRange(Notes(p.Id, config));
                return new(string.Join("\n", lines), on ? "Turn On Variables" : "Turn Off Variables");
            }

            case AddProject(var name, var folders, var enabled):
            {
                if (config.Scopes.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new EnvyUsageException($"There's already a project called {name}.");
                foreach (var folder in folders)
                    if (Owner(folder, config, home) is { } owner) throw new EnvyUsageException($"{folder} is already linked to {owner.Name}.");
                config.Projects.Add(new Project(name, enabled, folders));
                var scope = folders.Count == 0 ? "It loads in every terminal" : $"It loads inside {string.Join(", ", folders)}";
                return new($"Created {name}. {scope}{(enabled ? "" : " once it's switched on")}.", "New Project");
            }

            case Link(var folder, var target):
            {
                var p = Resolve(target, config, context, home);
                if (Owner(folder, config, home) is { } owner)
                {
                    if (owner.Id == p.Id) return new($"{folder} is already linked to {p.Name}.");
                    throw new EnvyUsageException($"{folder} is already linked to {owner.Name}.");
                }
                if (p.IsGlobal) throw new EnvyUsageException("Global loads in every terminal, so it has no folders. Pick a project with --project NAME.");
                config.Update(p.Id, project => project.Folders.Add(folder));
                return new($"Linked {folder} to {p.Name}.", "Link Folder");
            }

            case Unlink(var folder, var target):
            {
                var p = Resolve(target, config, context, home);
                var linked = p.Folders.Where(f => FolderMatch.Same(f, folder, home)).ToList();
                if (linked.Count == 0) throw new EnvyUsageException($"{folder} isn't linked to {p.Name}.");
                var left = p.Folders.Count == linked.Count ? " It has no folders left, so it now loads in every terminal while it's on." : "";
                config.Update(p.Id, project => project.Folders.RemoveAll(linked.Contains));
                return new($"Unlinked {folder} from {p.Name}.{left}", "Unlink Folder");
            }

            case Import(var path, var target, var keepExisting):
            {
                var p = Resolve(target, config, context, home);
                string text;
                try { text = File.ReadAllText(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new EnvyUsageException($"Couldn't read {path}."); }
                var vars = VariablesFromFile(path, text);
                if (vars.Count == 0) throw new EnvyUsageException($"{path} has no variables Envy can use.");
                var result = new MergeResult();
                config.Update(p.Id, project => result = project.Merge(vars, keepExisting ? MergePolicy.KeepExisting : MergePolicy.Replace));
                var secrets = vars.Where(v => v.IsSecret).Select(v => v.Key).ToList();
                var lines = new List<string>
                {
                    $"Imported {vars.Count} variable{(vars.Count == 1 ? "" : "s")} from {Path.GetFileName(path)} into {p.Name}: {result.Added} added, {result.Replaced} replaced, {result.Skipped} kept as they were.",
                    $"Names: {Names(vars.Select(v => v.Key))}",
                };
                if (secrets.Count > 0) lines.Add($"Stored as secrets in Windows Credential Manager: {Names(secrets)}. Values aren't shown.");
                lines.Add("The file is unchanged. Ask the user before deleting it.");
                lines.AddRange(Notes(p.Id, config));
                return new(string.Join("\n", lines), "Import .env");
            }

            case Open(var name):
                return new("", Request: new AppRequest.Show(name is null ? null : FindProject(name, config).Id));

            default:
                throw new InvalidOperationException(GetType().Name);
        }
    }

    // Lookup

    static Project FindProject(string name, EnvConfig config)
    {
        if ((config.Scopes.FirstOrDefault(p => p.Name == name) ?? config.Scopes.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) is { } found)
            return found;
        throw new EnvyUsageException($"No project called {name}. Projects: {string.Join(", ", config.Scopes.Select(p => Quoted(p.Name)))}.");
    }

    static Project Resolve(CommandTarget target, EnvConfig config, CommandContext context, string? home)
    {
        if (target.IsGlobal) return config.Global;
        if (target.Name is { } name) return FindProject(name, config);
        if (config.FolderProjects(context.Directories, home).LastOrDefault() is { } p) return p;
        var here = context.Directories.FirstOrDefault() ?? "this folder";
        throw new EnvyUsageException(
            $"No Envy project is linked to {here}. Add --global for every terminal, --project NAME for an existing project, " +
            "or create one for this folder first: envy project add NAME --folder .");
    }

    static Project? Owner(string folder, EnvConfig config, string? home) =>
        config.Projects.FirstOrDefault(p => p.Folders.Any(f => FolderMatch.Same(f, folder, home)));

    // Output

    static string StatusText(EnvConfig config, CommandContext context, string? home)
    {
        var scopes = config.LoadedScopes(context.Directories, home);
        var lines = new List<string>
        {
            scopes.Count == 0 ? "Envy loads nothing here." : $"Envy loads here: {string.Join(", ", scopes.Select(p => p.Name))} (later ones win).",
        };
        var wanted = new List<string>();
        foreach (var p in scopes)
            foreach (var v in Scopes.Loadable(p))
                if (!wanted.Contains(v.Key, EnvVar.KeyComparer)) wanted.Add(v.Key);
        if (wanted.Count > 0) lines.Add($"Variables: {string.Join(" ", wanted)}");
        var missing = wanted.Where(k => !context.Loaded.Contains(k, EnvVar.KeyComparer)).ToList();
        var stale = context.Loaded.Where(k => !wanted.Contains(k, EnvVar.KeyComparer)).ToList();
        if (missing.Count > 0 || stale.Count > 0)
        {
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add($"missing {string.Join(" ", missing)}");
            if (stale.Count > 0) parts.Add($"still has {string.Join(" ", stale)}");
            lines.Add($"This process started before the latest changes ({string.Join("; ", parts)}). Run commands with: envy exec -- <command>");
        }
        if (config.FolderProjects(context.Directories, home).LastOrDefault() is { IsEnabled: false } off)
            lines.Add($"{off.Name} is linked to this folder but switched off.");
        return string.Join("\n", lines);
    }

    static string Describe(Project p)
    {
        var scope = p.Folders.Count == 0 ? "every terminal" : string.Join(", ", p.Folders);
        var lines = new List<string> { $"{p.Name} ({(p.IsEnabled ? "on" : "off")}, {scope})" };
        if (p.Variables.Count == 0) lines.Add("  no variables");
        foreach (var v in p.Variables)
        {
            var line = "  " + v.Key;
            if (v.IsSecret) line += v.Value.Length == 0 ? "  secret, no value yet" : "  secret";
            else
            {
                line += "=" + v.Value.Replace("\r", "\\r").Replace("\n", "\\n");
                if (v.Expands) line += "  (expands)";
            }
            if (!v.IsEnabled) line += "  [off]";
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>Why a change might not show up in the caller's shell.</summary>
    static List<string> Notes(Guid id, EnvConfig config)
    {
        if (config.Find(id) is not { } p) return [];
        var notes = new List<string>();
        if (!p.IsEnabled) notes.Add($"{p.Name} is switched off, so this doesn't load anywhere yet.");
        notes.Add("New terminals and prompts pick this up. Your own environment doesn't: run commands with envy exec -- <command>.");
        return notes;
    }

    static string Names(IEnumerable<string> keys) => string.Join(", ", keys);

    static string Quoted(string name) => name.Contains(' ') ? $"\"{name}\"" : name;

    static string Flag(Project p) => p.IsGlobal ? " --global" : $" --project {Quoted(p.Name)}";

    /// <summary>Variables from a .env, .envrc or appsettings*.json file, secrets marked by name.</summary>
    public static List<EnvVar> VariablesFromFile(string path, string text)
    {
        var name = Path.GetFileName(path);
        var isJson = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || AppSettings.LooksLikeJson(text);
        var entries = isJson ? AppSettings.EntriesFromJson(text) ?? [] : DotEnv.Parse(text);
        var output = new List<EnvVar>();
        foreach (var entry in entries.Where(e => EnvVar.KeyProblem(e.Key) is null))
        {
            var v = new EnvVar(entry.Key, entry.Value, isSecret: EnvVar.LooksSecret(entry.Key),
                expands: name == ".envrc" && entry.Quote != DotEnv.Quote.Single && entry.Value.Contains('$'));
            var i = output.FindIndex(x => EnvVar.SameKey(x.Key, v.Key));
            if (i >= 0) output[i] = v; else output.Add(v);
        }
        return output;
    }

    public const string HelpText = """
        envy: environment variables and secrets from Envy

        Envy loads Global, then projects that load everywhere, then the project linked to the folder you're in
        (deepest folder last). Changes reach new terminals and the next prompt of open ones, in PowerShell,
        Git Bash and Command Prompt.

        See what's there
          envy status                      what loads here, and whether this process has it
          envy list [PROJECT]              projects and their variables (secret values are never shown)

        Change it (the change goes to the project linked to this folder; pick another with
        --project NAME, or --global for every terminal)
          envy set NAME=VALUE [...]        add or update plain variables; --expand evaluates %VARS% and $VARS in the value
          envy add NAME --secret           ask the user to paste a secret into Envy (it goes to Windows Credential Manager)
          envy remove NAME [...]           delete variables
          envy on|off NAME [...]           switch variables on or off; with only --project, the whole project
          envy import FILE                 move a .env, .envrc or appsettings.json into Envy; secrets go to Credential Manager
          envy project add NAME [--folder PATH]
                                           new project; with a folder it loads only inside it
          envy link|unlink [FOLDER] --project NAME

        Run with the latest values
          envy exec -- COMMAND [ARGS...]   run a command with what Envy loads here right now. A process keeps the
                                           environment it started with, so use this after a change.
          envy open [PROJECT]              open the app

        For coding agents
          - Never print secret values: no echo of secret variables, no unfiltered env, printenv, set or
            Get-ChildItem Env:, and don't read the files in %LOCALAPPDATA%\Envy\shell (they hold them in plain text).
          - Never put secrets in .env files, PowerShell profiles, ~/.bashrc, setx, code or commits, and never ask the
            user to paste one into the chat. Run envy add NAME --secret and let the user paste it into Envy.
          - Prefer Envy over creating .env files, running setx or adding variables to shell profiles. Found a .env?
            Offer envy import.
          - Ask the user before removing variables or switching things off.
          - If envy says a sandbox blocks it, run the same command again with permission to run outside the
            sandbox, or ask the user to run it. envy status and envy list still work from inside one.
        """;
}

/// <summary>
/// Whether a folder is inside a project's folders, as Windows sees it: without regard to case, with either separator,
/// and through links (symlinks, junctions) and 8.3 short names on either side.
/// </summary>
public static class FolderMatch
{
    /// <summary>Folder projects whose folders contain one of <paramref name="directories"/>, shallowest first.</summary>
    public static List<Project> FolderProjects(this EnvConfig config, IReadOnlyList<string> directories, string? home = null)
    {
        var dirs = Prefixes(directories, home);
        return config.Projects
            .Select((p, index) => (p, index))
            .Where(x => x.p.IsFolderScoped && Prefixes(x.p.Folders, home).Any(f => dirs.Any(d => d.StartsWith(f, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(x => PathStyle.Depth(x.p, home)).ThenBy(x => x.index)
            .Select(x => x.p)
            .ToList();
    }

    /// <summary>What a shell in one of <paramref name="directories"/> loads, in the order the hooks apply it.</summary>
    public static List<Project> LoadedScopes(this EnvConfig config, IReadOnlyList<string> directories, string? home = null)
    {
        var output = new List<Project>();
        if (config.Global.IsEnabled) output.Add(config.Global);
        output.AddRange(config.Projects.Where(p => p.IsEnabled && !p.IsFolderScoped));
        output.AddRange(config.FolderProjects(directories, home).Where(p => p.IsEnabled));
        return output.Where(p => Scopes.Loadable(p).Count > 0).ToList();
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name the same folder.</summary>
    public static bool Same(string a, string b, string? home = null)
    {
        var left = Prefixes([a], home);
        return Prefixes([b], home).Any(p => left.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Each folder as given, with links resolved and with long names, in Windows form ending in a backslash.</summary>
    public static List<string> Prefixes(IEnumerable<string> folders, string? home = null) =>
        folders
            .SelectMany(f => PathStyle.Variants(Windows(f), home))
            .Select(v => PathStyle.AsPrefix(Windows(v)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    static string Windows(string path) => OperatingSystem.IsWindows() ? PathStyle.FromPosix(path).Replace('/', '\\') : path;
}
