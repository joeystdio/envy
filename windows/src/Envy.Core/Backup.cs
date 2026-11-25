using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Envy.Core;

/// <summary>The file around an encrypted backup. See spec/backup-format.md.</summary>
public sealed class BackupFile
{
    public string Format { get; set; } = "";
    public int Version { get; set; }
    public BackupKdf Kdf { get; set; } = new();
    public BackupCipher Cipher { get; set; } = new();
    public string Data { get; set; } = "";
}

public sealed class BackupKdf
{
    public string Name { get; set; } = "";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
}

public sealed class BackupCipher
{
    public string Name { get; set; } = "";
    public string Nonce { get; set; } = "";
}

/// <summary>What a backup holds once decrypted.</summary>
public sealed class BackupContent
{
    public string App { get; set; } = "";
    /// <summary><see cref="Backup.MacOS"/> or <see cref="Backup.Windows"/>: where the backup was made.</summary>
    public string Platform { get; set; } = "";
    public string Created { get; set; } = "";
    public BackupScope Global { get; set; } = new() { Name = "Global" };
    public List<BackupScope> Projects { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<BackupScope> Scopes => Projects.Prepend(Global);
}

public sealed class BackupScope
{
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public List<string> Folders { get; set; } = [];
    public List<BackupVariable> Variables { get; set; } = [];
}

public sealed class BackupVariable
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsSecret { get; set; }
    public bool Expands { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>A backup that can't be opened; the message is for the user.</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Backups of everything Envy manages, in one password-encrypted file that Envy for macOS and Envy for Windows both read
/// and write (spec/backup-format.md): PBKDF2-HMAC-SHA256 for the key, AES-256-GCM for the contents.
/// </summary>
public static class Backup
{
    public const string Extension = ".envybackup";
    public const string MacOS = "macos";
    public const string Windows = "windows";
    public const int MinimumPasswordLength = 12;
    public const int DefaultIterations = 600_000;
    const string FormatName = "envy-backup";
    const int FormatVersion = 1;
    const string KdfName = "PBKDF2-HMAC-SHA256";
    const string CipherName = "AES-256-GCM";
    static readonly byte[] AssociatedData = Encoding.ASCII.GetBytes("envy-backup v1");

    public static string ThisPlatform => OperatingSystem.IsMacOS() ? MacOS : Windows;

    public static string SuggestedFileName(DateTime now) => $"Envy Backup {now:yyyy-MM-dd}{Extension}";

    /// <summary>Why <paramref name="password"/> can't protect a backup, or null.</summary>
    public static string? PasswordProblem(string password, string confirmation)
    {
        if (password.Length < MinimumPasswordLength) return $"Use at least {MinimumPasswordLength} characters.";
        if (password != confirmation) return "The passwords don't match.";
        return null;
    }

    // Writing

    /// <summary>Everything in <paramref name="config"/>, secrets included, with folders under <paramref name="home"/> as ~/….</summary>
    public static BackupContent FromConfig(EnvConfig config, string home, string app, DateTime createdUtc, string? platform = null) => new()
    {
        App = app,
        Platform = platform ?? ThisPlatform,
        Created = createdUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        Global = Scope(config.Global, home),
        Projects = config.Projects.Select(p => Scope(p, home)).ToList(),
    };

    static BackupScope Scope(Project p, string home) => new()
    {
        Name = p.Name,
        IsEnabled = p.IsEnabled,
        Folders = p.Folders.Select(f => PortableFolder(f, home)).ToList(),
        Variables = p.Variables.Select(v => new BackupVariable
        {
            Key = v.Key, Value = v.Value, IsEnabled = v.IsEnabled, IsSecret = v.IsSecret, Expands = v.Expands, Note = v.Note,
        }).ToList(),
    };

    /// <summary>~ or ~/… with forward slashes for a folder inside <paramref name="home"/>; anything else as it is.</summary>
    public static string PortableFolder(string folder, string home)
    {
        var f = PathStyle.TrimTrailing(folder);
        var h = PathStyle.TrimTrailing(home);
        if (f.Equals(h, StringComparison.OrdinalIgnoreCase)) return "~";
        foreach (var separator in new[] { '\\', '/' })
            if (f.StartsWith(h + separator, StringComparison.OrdinalIgnoreCase))
                return "~/" + f[(h.Length + 1)..].Replace('\\', '/');
        return folder;
    }

    /// <summary>A folder from a backup as a path on this machine.</summary>
    public static string LocalFolder(string folder, string home)
    {
        if (folder == "~") return home;
        if (folder.StartsWith("~/")) return Path.Combine(home, folder[2..].Replace('/', Path.DirectorySeparatorChar));
        return folder;
    }

    /// <summary>The encrypted file. <paramref name="iterations"/> is only lowered by tests.</summary>
    public static string Write(BackupContent content, string password, int iterations = DefaultIterations)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(content, EnvyJson.Default.BackupContent);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Key(password, salt, iterations);
        var data = new byte[plain.Length + 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), AssociatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
        var file = new BackupFile
        {
            Format = FormatName,
            Version = FormatVersion,
            Kdf = new BackupKdf { Name = KdfName, Iterations = iterations, Salt = Convert.ToBase64String(salt) },
            Cipher = new BackupCipher { Name = CipherName, Nonce = Convert.ToBase64String(nonce) },
            Data = Convert.ToBase64String(data),
        };
        return JsonSerializer.Serialize(file, EnvyJson.Default.BackupFile).ReplaceLineEndings("\n") + "\n";
    }

    // Reading

    /// <summary>The contents of a backup file. Throws <see cref="BackupException"/> with a message for the user.</summary>
    public static BackupContent Read(string text, string password)
    {
        BackupFile? file;
        try { file = JsonSerializer.Deserialize(text, EnvyJson.Default.BackupFile); }
        catch (JsonException e) { throw new BackupException("This isn't an Envy backup.", e); }
        if (file is null || file.Format != FormatName) throw new BackupException("This isn't an Envy backup.");
        if (file.Version > FormatVersion) throw new BackupException("This backup was made by a newer version of Envy. Update Envy to restore it.");

        byte[] salt, nonce, data;
        try
        {
            salt = Convert.FromBase64String(file.Kdf.Salt);
            nonce = Convert.FromBase64String(file.Cipher.Nonce);
            data = Convert.FromBase64String(file.Data);
        }
        catch (FormatException e) { throw new BackupException("This backup is damaged.", e); }
        if (file.Version < 1 || file.Kdf.Name != KdfName || file.Cipher.Name != CipherName
            || file.Kdf.Iterations is < 100_000 or > 10_000_000 || salt.Length != 16 || nonce.Length != 12 || data.Length < 16)
            throw new BackupException("This backup is damaged.");

        var key = Key(password, salt, file.Kdf.Iterations);
        var plain = new byte[data.Length - 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, AssociatedData);
            return JsonSerializer.Deserialize(plain, EnvyJson.Default.BackupContent) ?? throw new BackupException("This backup is damaged.");
        }
        catch (AuthenticationTagMismatchException e) { throw new BackupException("Wrong password, or the file is damaged.", e); }
        catch (JsonException e) { throw new BackupException("This backup is damaged.", e); }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    static byte[] Key(string password, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public enum RestoreMode { Add, Replace }

/// <summary>What to do with an expanding value from the other system.</summary>
public enum ExpandAction { UseSuggestion, KeepAsIs, TurnOff }

/// <summary>An expanding value from the other system that may not work here. <c>Scope</c> -1 is Global.</summary>
public sealed record ExpandIssue(int Scope, int Index, string ScopeName, string Key, string Value, string Reason, string? Suggestion)
{
    public ExpandAction DefaultAction => Suggestion is not null ? ExpandAction.UseSuggestion
        : Reason == BackupRestore.CommandFromWindows ? ExpandAction.TurnOff : ExpandAction.KeepAsIs;
}

/// <summary>The choice made for one <see cref="ExpandIssue"/>; <c>Value</c> is the (possibly edited) suggestion.</summary>
public sealed record ExpandFix(int Scope, int Index, ExpandAction Action, string? Value = null);

/// <summary>What a restore did, or would do.</summary>
public sealed class RestoreReport
{
    public List<string> ProjectsAdded { get; } = [];
    public List<string> ProjectsMerged { get; } = [];
    public List<string> ProjectsRemoved { get; } = [];
    public int VariablesAdded { get; set; }
    public int VariablesReplaced { get; set; }
    public int Secrets { get; set; }
    /// <summary>Folders restored that don't exist on this machine.</summary>
    public List<(string Project, string Folder)> MissingFolders { get; } = [];
    /// <summary>Folders not linked because another project here already has them.</summary>
    public List<(string Project, string Folder, string Owner)> SkippedFolders { get; } = [];
    /// <summary>New projects restored switched off because all their folders were skipped.</summary>
    public List<string> TurnedOff { get; } = [];
}

/// <summary>Restoring a backup into the settings, and the suggestions for expanding values from the other system.</summary>
public static partial class BackupRestore
{
    internal const string CommandFromMac = "Runs a command. Windows doesn't run commands in values.";
    internal const string CommandFromWindows = "This would run a command on a Mac; on Windows it was plain text.";

    /// <summary>
    /// Applies <paramref name="backup"/> to <paramref name="config"/> (pass a copy to preview). Names compare as Windows
    /// does, without regard to case; <paramref name="folderExists"/> decides which folders are missing here.
    /// </summary>
    public static RestoreReport Apply(EnvConfig config, BackupContent backup, RestoreMode mode, string home,
        IReadOnlyList<ExpandFix>? fixes = null, Func<string, bool>? folderExists = null)
    {
        folderExists ??= Directory.Exists;
        var report = new RestoreReport();
        var global = ToProject(backup.Global, home, fixes, -1, Project.GlobalId);
        var projects = backup.Projects.Select((s, i) => ToProject(s, home, fixes, i, Guid.NewGuid())).ToList();
        report.Secrets = projects.Prepend(global).Sum(p => p.Variables.Count(v => v.IsSecret));

        if (mode == RestoreMode.Replace)
        {
            report.ProjectsRemoved.AddRange(config.Projects.Select(p => p.Name));
            report.ProjectsAdded.AddRange(projects.Select(p => p.Name));
            report.VariablesAdded = projects.Prepend(global).Sum(p => p.Variables.Count);
            config.Global = global;
            config.Projects = projects;
        }
        else
        {
            Merge(config.Global, global.Variables, report);
            foreach (var incoming in projects)
            {
                var existing = config.Projects.FirstOrDefault(p => p.Name.Equals(incoming.Name, StringComparison.OrdinalIgnoreCase));
                var folders = new List<string>();
                foreach (var folder in incoming.Folders)
                {
                    if (existing is not null && existing.Folders.Any(f => FolderMatch.Same(f, folder, home))) continue;
                    if (config.Projects.FirstOrDefault(p => p != existing && p.Folders.Any(f => FolderMatch.Same(f, folder, home))) is { } owner)
                    {
                        report.SkippedFolders.Add((incoming.Name, folder, owner.Name));
                        continue;
                    }
                    folders.Add(folder);
                }
                if (existing is not null)
                {
                    Merge(existing, incoming.Variables, report);
                    existing.Folders.AddRange(folders);
                    report.ProjectsMerged.Add(existing.Name);
                }
                else
                {
                    if (incoming.Folders.Count > 0 && folders.Count == 0 && incoming.IsEnabled)
                    {
                        incoming.IsEnabled = false;
                        report.TurnedOff.Add(incoming.Name);
                    }
                    incoming.Folders = folders;
                    config.Projects.Add(incoming);
                    report.ProjectsAdded.Add(incoming.Name);
                    report.VariablesAdded += incoming.Variables.Count;
                }
            }
        }

        foreach (var p in config.Projects)
            foreach (var folder in p.Folders)
                if (projects.Any(r => r.Folders.Contains(folder)) && !folderExists(folder)) report.MissingFolders.Add((p.Name, folder));
        return report;
    }

    static void Merge(Project project, List<EnvVar> variables, RestoreReport report)
    {
        var result = project.Merge(variables, MergePolicy.Replace);
        report.VariablesAdded += result.Added;
        report.VariablesReplaced += result.Replaced;
    }

    static Project ToProject(BackupScope scope, string home, IReadOnlyList<ExpandFix>? fixes, int index, Guid id) => new(
        id == Project.GlobalId ? "Global" : scope.Name,
        scope.IsEnabled,
        id == Project.GlobalId ? [] : scope.Folders.Select(f => Backup.LocalFolder(f, home)).Distinct(StringComparer.OrdinalIgnoreCase),
        scope.Variables.Select((v, i) =>
        {
            var variable = new EnvVar(v.Key, v.Value, v.IsEnabled, v.IsSecret, v.Expands, v.Note);
            if (fixes?.FirstOrDefault(f => f.Scope == index && f.Index == i) is { } fix)
            {
                if (fix.Action == ExpandAction.TurnOff) variable.Expands = false;
                else if (fix.Action == ExpandAction.UseSuggestion && fix.Value is not null) variable.Value = fix.Value;
            }
            return variable;
        }),
        id);

    // Expanding values from the other system

    /// <summary>Expanding values in a backup from the other system that may not work here, with suggested rewrites.</summary>
    public static List<ExpandIssue> ExpandIssues(BackupContent backup, string here)
    {
        var issues = new List<ExpandIssue>();
        if (backup.Platform == here || backup.Platform is not (Backup.MacOS or Backup.Windows)) return issues;
        var scopes = backup.Projects.Prepend(backup.Global).ToList();
        for (var s = 0; s < scopes.Count; s++)
        {
            for (var i = 0; i < scopes[s].Variables.Count; i++)
            {
                var v = scopes[s].Variables[i];
                if (!v.Expands) continue;
                if (Suggest(v.Key, v.Value, backup.Platform, here) is { } issue)
                    issues.Add(new ExpandIssue(s - 1, i, s == 0 ? "Global" : scopes[s].Name, v.Key, v.Value, issue.Reason, issue.Suggestion));
            }
        }
        return issues;
    }

    [GeneratedRegex(@"\$\{HOME\}|\$HOME(?![A-Za-z0-9_])")]
    private static partial Regex MacHome();

    [GeneratedRegex(@"%([A-Za-z_][A-Za-z0-9_]*)%")]
    private static partial Regex PercentName();

    [GeneratedRegex(@"\$env:([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellName();

    /// <summary>The reason a value from <paramref name="from"/> may not work on <paramref name="to"/>, and a rewrite if there is one.</summary>
    public static (string Reason, string? Suggestion)? Suggest(string key, string value, string from, string to)
    {
        var pathList = key.EndsWith("PATH", StringComparison.OrdinalIgnoreCase);
        var runsCommand = value.Contains("$(") || value.Contains('`');
        var reasons = new List<string>();
        var suggestion = value;

        if (from == Backup.MacOS && to == Backup.Windows)
        {
            if (runsCommand) return (CommandFromMac, null);
            if (MacHome().IsMatch(suggestion))
            {
                reasons.Add("$HOME isn't set in PowerShell or Command Prompt.");
                suggestion = MacHome().Replace(suggestion, "%USERPROFILE%");
            }
            if (pathList && suggestion.Contains(':'))
            {
                reasons.Add("Windows separates folders with ;.");
                suggestion = suggestion.Replace(':', ';');
            }
            if (reasons.Count == 0) return null;
            if (!value.Contains("://")) suggestion = suggestion.Replace('/', '\\');
        }
        else if (from == Backup.Windows && to == Backup.MacOS)
        {
            if (runsCommand) return (CommandFromWindows, null);
            if (PercentName().IsMatch(suggestion) || PowerShellName().IsMatch(suggestion))
            {
                reasons.Add("A Mac doesn't understand %NAME% or $env:NAME.");
                suggestion = PercentName().Replace(suggestion, m => m.Groups[1].Value.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase) ? "$HOME" : "${" + m.Groups[1].Value + "}");
                suggestion = PowerShellName().Replace(suggestion, m => "${" + m.Groups[1].Value + "}");
            }
            if (pathList && suggestion.Contains(';'))
            {
                reasons.Add("A Mac separates folders with :.");
                suggestion = suggestion.Replace(';', ':');
            }
            if (reasons.Count == 0) return null;
            if (!value.Contains("://")) suggestion = suggestion.Replace('\\', '/');
        }
        else
        {
            return null;
        }
        return (string.Join(" ", reasons), suggestion);
    }
}
