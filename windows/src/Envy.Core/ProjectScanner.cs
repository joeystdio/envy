namespace Envy.Core;

/// <summary>A folder with environment or .NET configuration files, ready to become a folder-linked project.</summary>
public sealed record FoundProject(string Folder, List<string> Files, List<EnvVar> Variables)
{
    public string Id => Folder;
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));
}

/// <summary>Walks a folder tree looking for projects with .env-style files or .NET appsettings.json and user secrets.</summary>
public static class ProjectScanner
{
    /// <summary>Read in this order, so .env.local overrides .env. Templates such as .env.example are ignored.</summary>
    public static readonly string[] EnvFileNames = [".env", ".env.local", ".envrc"];

    /// <summary>
    /// Dependency, build and system folders that never hold a project of their own and can be huge.
    /// bin and obj also hold stale copies of appsettings.json.
    /// </summary>
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "vendor", "Pods", "Carthage", "DerivedData", "build", "dist", "target", "out",
        "bin", "obj", "packages", "TestResults", "venv", "__pycache__", "AppData", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information",
    };

    /// <summary>
    /// Breadth-first, at most <paramref name="maxDepth"/> folders below <paramref name="root"/>, never following
    /// symlinks or junctions or entering hidden folders. <paramref name="appData"/> is where .NET user secrets live.
    /// Results are sorted by path.
    /// </summary>
    public static List<FoundProject> Scan(string root, string appData, int maxDepth = 4, Func<bool>? isCancelled = null)
    {
        isCancelled ??= () => false;
        var found = new List<FoundProject>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && !isCancelled())
        {
            var (dir, depth) = queue.Dequeue();
            if (Project(dir, appData) is { } project) found.Add(project);
            if (depth >= maxDepth) continue;
            IEnumerable<DirectoryInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateDirectories().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            foreach (var child in children)
            {
                var skip = child.Name.StartsWith('.') || SkippedDirectories.Contains(child.Name)
                    || child.Attributes.HasFlag(FileAttributes.Hidden) || child.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || child.Attributes.HasFlag(FileAttributes.System);
                if (!skip) queue.Enqueue((child.FullName, depth + 1));
            }
        }
        return found.OrderBy(p => p.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The project in <paramref name="folder"/> itself, if it has any usable configuration.</summary>
    public static FoundProject? Project(string folder, string appData)
    {
        var files = new List<string>();
        var variables = new List<EnvVar>();

        void Add(IEnumerable<DotEnv.Entry> entries, string source, bool expandShellValues = false, bool allSecret = false)
        {
            var usable = entries.Where(e => EnvVar.KeyProblem(e.Key) is null).ToList();
            if (usable.Count == 0) return;
            files.Add(source);
            foreach (var entry in usable)
            {
                var variable = new EnvVar(entry.Key, entry.Value,
                    isSecret: allSecret || EnvVar.LooksSecret(entry.Key),
                    expands: expandShellValues && entry.Quote != DotEnv.Quote.Single && entry.Value.Contains('$'));
                var i = variables.FindIndex(v => EnvVar.SameKey(v.Key, variable.Key));
                if (i >= 0) variables[i] = variable; else variables.Add(variable);
            }
        }

        string? Read(string name)
        {
            try { return File.ReadAllText(Path.Combine(folder, name)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        foreach (var name in EnvFileNames)
        {
            // .envrc is a shell script, so its values expand; .env files are taken literally.
            if (Read(name) is { } text) Add(DotEnv.Parse(text), name, expandShellValues: name == ".envrc");
        }
        foreach (var name in AppSettings.FileNames)
        {
            if (Read(name) is { } text && AppSettings.EntriesFromJson(text) is { } entries) Add(entries, name);
        }
        if (AppSettings.UserSecrets(folder, appData) is { } secrets) Add(secrets, "user secrets", allSecret: true);
        return files.Count == 0 ? null : new FoundProject(Path.GetFullPath(folder), files, variables);
    }
}
