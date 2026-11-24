namespace Envy.Core;

/// <summary>What envy exec loads: the scopes the hooks would apply, in their order, with values. Written as vars.json.</summary>
public sealed class ExecData
{
    public List<ExecScope> Scopes { get; set; } = [];
}

public sealed class ExecScope
{
    public string Name { get; set; } = "";
    /// <summary>Empty for scopes that load everywhere.</summary>
    public List<string> Folders { get; set; } = [];
    public List<ExecVariable> Variables { get; set; } = [];
}

public sealed class ExecVariable
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Expands { get; set; }
}

/// <summary>
/// The environment envy exec gives a command: the caller's, with what Envy set there put back first, then the scopes
/// that load in the working folder applied the way the shell hooks apply them, bookkeeping included, so a shell started
/// from the command can undo it.
/// </summary>
public static class ExecEnvironment
{
    public static ExecData Data(EnvConfig config, string? home = null)
    {
        var (alwaysOn, scoped) = Scopes.Ordered(config, home);
        var data = new ExecData();
        foreach (var p in alwaysOn.Concat(scoped))
        {
            var vars = Scopes.Loadable(p);
            if (vars.Count == 0) continue;
            data.Scopes.Add(new ExecScope
            {
                Name = p.Name,
                Folders = [.. p.Folders],
                Variables = vars.Select(v => new ExecVariable { Key = v.Key, Value = v.Value, Expands = v.Expands }).ToList(),
            });
        }
        return data;
    }

    /// <summary>
    /// <paramref name="environment"/> with Envy's earlier changes undone and what loads in <paramref name="directories"/>
    /// applied. Names compare without regard to case, as Windows does.
    /// </summary>
    public static Dictionary<string, string> Compute(ExecData data, IReadOnlyList<string> directories, IReadOnlyDictionary<string, string> environment, string? home = null)
    {
        var env = new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        if (env.TryGetValue("__ENVY_VARS", out var listed))
        {
            foreach (var name in listed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Expansion.IsIdentifier(name)) continue;
                if (env.Remove("__ENVY_O_" + name, out var original)) env[name] = original;
                else env.Remove(name);
            }
            env.Remove("__ENVY_VARS");
        }

        var dirs = FolderMatch.Prefixes(directories, home);
        var managed = new List<string>();
        foreach (var scope in data.Scopes)
        {
            if (scope.Folders.Count > 0 && !FolderMatch.Prefixes(scope.Folders, home).Any(f => dirs.Any(d => d.StartsWith(f, StringComparison.OrdinalIgnoreCase))))
                continue;
            foreach (var v in scope.Variables)
            {
                var value = v.Expands ? Expand(v.Value, env, home) : v.Value;
                if (!managed.Contains(v.Key, EnvVar.KeyComparer))
                {
                    if (env.TryGetValue(v.Key, out var before)) env["__ENVY_O_" + v.Key] = before;
                    managed.Add(v.Key);
                }
                env[v.Key] = value;
            }
        }
        if (managed.Count > 0) env["__ENVY_VARS"] = string.Join(" ", managed) + " ";
        return env;
    }

    static string Expand(string value, Dictionary<string, string> env, string? home) =>
        string.Concat(Expansion.Parse(value).Select(t => t switch
        {
            Expansion.Literal l => l.Text,
            Expansion.Home => env.GetValueOrDefault("USERPROFILE") ?? home ?? "",
            Expansion.Reference r => env.GetValueOrDefault(r.Name) ?? "",
            _ => "",
        }));
}
