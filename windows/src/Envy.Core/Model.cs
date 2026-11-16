using System.Text.Json.Serialization;

namespace Envy.Core;

/// <summary>One environment variable inside a project (or Global).</summary>
public sealed record EnvVar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    /// <summary>Secret values live in Windows Credential Manager instead of the settings file and are masked in the UI.</summary>
    public bool IsSecret { get; set; }
    /// <summary>Expand %NAME%, $NAME, ${NAME} and a leading ~ when the shell loads the value, instead of using it literally.</summary>
    public bool Expands { get; set; }
    public string Note { get; set; } = "";

    public EnvVar() { }

    public EnvVar(string key, string value = "", bool isEnabled = true, bool isSecret = false, bool expands = false, string note = "")
    {
        Key = key;
        Value = value;
        IsEnabled = isEnabled;
        IsSecret = isSecret;
        Expands = expands;
        Note = note;
    }

    /// <summary>
    /// Names a shell owns or that would break it when set: bash internals, and cmd's dynamic variables, which stop
    /// working once a real variable of the same name exists.
    /// </summary>
    static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "IFS", "PWD", "OLDPWD", "SHLVL", "_", "UID", "EUID", "GID", "EGID", "PPID", "LINENO",
        "RANDOM", "SECONDS", "BASHOPTS", "SHELLOPTS", "BASH_VERSINFO", "ZSH_VERSION",
        "CD", "DATE", "TIME", "ERRORLEVEL", "CMDEXTVERSION", "CMDCMDLINE", "HIGHESTNUMANODENUMBER",
    };

    /// <summary>Why <paramref name="key"/> can't be set, or null when it's a usable name.</summary>
    public static string? KeyProblem(string key)
    {
        if (key.Length == 0) return "Name is required.";
        foreach (var c in key)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
                return "Use only letters, digits and underscores.";
        }
        if (char.IsAsciiDigit(key[0])) return "Names can't start with a digit.";
        if (key.StartsWith("__envy", StringComparison.OrdinalIgnoreCase) || ReservedKeys.Contains(key))
            return $"{key} is reserved by the shell.";
        return null;
    }

    [JsonIgnore] public string? Problem => KeyProblem(Key);
    [JsonIgnore] public bool HasValidKey => Problem is null;

    /// <summary>Heuristic for names that usually hold credentials, used to pre-tick "Secret".</summary>
    public static bool LooksSecret(string key)
    {
        var k = key.ToUpperInvariant();
        string[] fragments = ["SECRET", "TOKEN", "PASSWORD", "PASSWD", "PRIVATE", "CREDENTIAL", "APIKEY", "API_KEY", "CONNECTIONSTRING", "CONNECTION_STRING"];
        return fragments.Any(k.Contains) || k.EndsWith("_KEY") || k.EndsWith("_PASS") || k.EndsWith("_PWD");
    }

    /// <summary>Windows variable names ignore case: Path and PATH are the same variable.</summary>
    public static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    public static bool SameKey(string a, string b) => KeyComparer.Equals(a, b);
}

/// <summary>A named set of variables. Without folders it loads in every shell; with folders it loads only inside them.</summary>
public sealed record Project
{
    public static readonly Guid GlobalId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled";
    public bool IsEnabled { get; set; } = true;
    /// <summary>Absolute paths. The project's variables load while the shell's working directory is inside one of them.</summary>
    public List<string> Folders { get; set; } = [];
    public List<EnvVar> Variables { get; set; } = [];

    public Project() { }

    public Project(string name, bool isEnabled = true, IEnumerable<string>? folders = null, IEnumerable<EnvVar>? variables = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        IsEnabled = isEnabled;
        Folders = folders?.ToList() ?? [];
        Variables = variables?.ToList() ?? [];
    }

    public static Project NewGlobal() => new("Global", id: GlobalId);

    [JsonIgnore] public bool IsGlobal => Id == GlobalId;
    [JsonIgnore] public bool IsFolderScoped => Folders.Count > 0;

    /// <summary>Keys that appear more than once in this project (the last one wins in the shell).</summary>
    [JsonIgnore]
    public HashSet<string> DuplicateKeys
    {
        get
        {
            var seen = new HashSet<string>(EnvVar.KeyComparer);
            var dupes = new HashSet<string>(EnvVar.KeyComparer);
            foreach (var v in Variables) if (!seen.Add(v.Key)) dupes.Add(v.Key);
            return dupes;
        }
    }

    /// <summary>
    /// Adds <paramref name="incoming"/> by name. Copies always get fresh IDs (secrets are keyed by ID), and a replaced
    /// variable keeps the ID of the one it replaces.
    /// </summary>
    public MergeResult Merge(IEnumerable<EnvVar> incoming, MergePolicy policy)
    {
        var result = new MergeResult();
        foreach (var original in incoming)
        {
            var v = original with { };
            var i = Variables.FindIndex(x => EnvVar.SameKey(x.Key, v.Key));
            if (i >= 0)
            {
                if (policy != MergePolicy.Replace) { result.Skipped++; continue; }
                v.Id = Variables[i].Id;
                Variables[i] = v;
                result.Replaced++;
            }
            else
            {
                v.Id = Guid.NewGuid();
                Variables.Add(v);
                result.Added++;
            }
        }
        return result;
    }

    /// <summary>A deep copy, so snapshots for undo don't share lists.</summary>
    public Project Copy() => this with
    {
        Folders = [.. Folders],
        Variables = Variables.Select(v => v with { }).ToList(),
    };

    public bool ContentEquals(Project other) =>
        Id == other.Id && Name == other.Name && IsEnabled == other.IsEnabled
        && Folders.SequenceEqual(other.Folders) && Variables.SequenceEqual(other.Variables);
}

public enum MergePolicy { Replace, KeepExisting }

public sealed class MergeResult
{
    public int Added { get; set; }
    public int Replaced { get; set; }
    public int Skipped { get; set; }
}

/// <summary>Everything Envy stores: the Global scope plus projects in sidebar order.</summary>
public sealed class EnvConfig
{
    public int Version { get; set; } = 1;
    public Project Global { get; set; } = Project.NewGlobal();
    public List<Project> Projects { get; set; } = [];

    public EnvConfig() { }

    public EnvConfig(Project? global = null, IEnumerable<Project>? projects = null)
    {
        Global = global ?? Project.NewGlobal();
        Projects = projects?.ToList() ?? [];
    }

    /// <summary>Global first, then projects.</summary>
    [JsonIgnore] public IEnumerable<Project> Scopes => Projects.Prepend(Global);

    public Project? Find(Guid id) => id == Project.GlobalId ? Global : Projects.FirstOrDefault(p => p.Id == id);

    public void Update(Guid id, Action<Project> change)
    {
        if (Find(id) is { } project) change(project);
    }

    /// <summary>Enabled scopes other than <paramref name="projectId"/> that also define an enabled <paramref name="key"/>.</summary>
    public List<Project> OtherDefinitions(string key, Guid projectId) =>
        Scopes.Where(p => p.Id != projectId && p.IsEnabled && p.Variables.Any(v => v.IsEnabled && EnvVar.SameKey(v.Key, key))).ToList();

    /// <summary>Makes sure Global keeps its fixed ID and has no folders, whatever the file said.</summary>
    public void Normalize()
    {
        Global ??= Project.NewGlobal();
        Global.Id = Project.GlobalId;
        Global.Folders = [];
        Projects ??= [];
        foreach (var p in Scopes)
        {
            p.Folders ??= [];
            p.Variables ??= [];
        }
    }

    public EnvConfig Clone() => new(Global.Copy(), Projects.Select(p => p.Copy())) { Version = Version };

    public bool ContentEquals(EnvConfig other) =>
        Global.ContentEquals(other.Global) && Projects.Count == other.Projects.Count
        && Projects.Zip(other.Projects).All(pair => pair.First.ContentEquals(pair.Second));

    // Secrets

    /// <summary>Values of all secret variables keyed by variable ID, for Credential Manager.</summary>
    public Dictionary<string, string> SecretValues()
    {
        var output = new Dictionary<string, string>();
        foreach (var p in Scopes)
            foreach (var v in p.Variables.Where(v => v.IsSecret))
                output[v.Id.ToString()] = v.Value;
        return output;
    }

    /// <summary>The config as written to disk: secret values blanked out.</summary>
    public EnvConfig WithoutSecretValues()
    {
        var copy = Clone();
        foreach (var p in copy.Scopes)
            foreach (var v in p.Variables.Where(v => v.IsSecret))
                v.Value = "";
        return copy;
    }

    public void FillSecrets(IReadOnlyDictionary<string, string> secrets)
    {
        foreach (var p in Scopes)
            foreach (var v in p.Variables.Where(v => v.IsSecret))
                if (secrets.TryGetValue(v.Id.ToString(), out var value)) v.Value = value;
    }
}
