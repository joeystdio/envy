using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Envy.Core;

/// <summary>A variable set in plain text in a startup file or in your user environment variables.</summary>
public sealed record PlaintextExport(string Source, int Line, string Key, string Value, bool Expands)
{
    /// <summary>The Source of exports from the user environment variables rather than a file.</summary>
    public const string UserEnvironment = "User environment variables";

    public string Id => $"{Source}:{Line}:{Key}";
    public bool IsUserEnvironment => Source == UserEnvironment;

    /// <summary>The variable to import: secret when the name looks like a credential.</summary>
    public EnvVar Variable => new(Key, Value, isSecret: EnvVar.LooksSecret(Key), expands: Expands);
}

/// <summary>Your persistent user environment variables (HKCU\Environment), which every program you start gets.</summary>
public interface IUserEnvironment
{
    IReadOnlyDictionary<string, string> Read();
    void Remove(IEnumerable<string> names);
}

[SupportedOSPlatform("windows")]
public sealed partial class RegistryUserEnvironment : IUserEnvironment
{
    public IReadOnlyDictionary<string, string> Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (key is null) return output;
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value) output[name] = value;
        return output;
    }

    public void Remove(IEnumerable<string> names)
    {
        using (var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true))
        {
            if (key is null) return;
            foreach (var name in names) key.DeleteValue(name, throwOnMissingValue: false);
        }
        // Tells Explorer, and so programs started from now on, that the environment changed.
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 2000, out _);
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

/// <summary>User environment variables kept in a JSON file, for ENVY_HOME sandboxes and tests.</summary>
public sealed class FileUserEnvironment(string path) : IUserEnvironment
{
    public IReadOnlyDictionary<string, string> Read()
    {
        if (!File.Exists(path)) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
    }

    public void Remove(IEnumerable<string> names)
    {
        var current = new Dictionary<string, string>(Read(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in names) current.Remove(name);
        File.WriteAllText(path, JsonSerializer.Serialize(current));
    }
}

/// <summary>Finds variables set in plain text: export lines in bash files, $env: assignments in PowerShell profiles, and user environment variables.</summary>
public static partial class PlaintextScanner
{
    /// <summary>Search paths and Windows' own variables are left alone.</summary>
    static readonly HashSet<string> IgnoredKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "MANPATH", "INFOPATH", "FPATH", "TEMP", "TMP", "OneDrive", "OneDriveConsumer", "OneDriveCommercial",
        "PSModulePath", "ChocolateyLastPathUpdate", "HOME", "USERPROFILE",
    };

    [GeneratedRegex(@"^\$env:([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EnvAssignment();

    [GeneratedRegex(@"^\[(?:System\.)?Environment\]::SetEnvironmentVariable\(\s*(['""])([A-Za-z_][A-Za-z0-9_]*)\1\s*,\s*(.*?)\s*(?:,\s*['""]?\w+['""]?\s*)?\)\s*;?\s*(?:#.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SetEnvironmentVariableCall();

    /// <summary>Single-line export NAME=value statements outside Envy's own block.</summary>
    public static List<PlaintextExport> BashExports(string text, string file)
    {
        var found = new List<PlaintextExport>();
        foreach (var (line, number) in OutsideEnvyBlock(text))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("export ") || !trimmed.Contains('=')) continue;
            // Parsed one line at a time so an unbalanced quote can't swallow the rest of the file.
            foreach (var entry in DotEnv.Parse(trimmed))
            {
                if (EnvVar.KeyProblem(entry.Key) is not null || IgnoredKeys.Contains(entry.Key)) continue;
                found.Add(new PlaintextExport(file, number, entry.Key, entry.Value, entry.Quote != DotEnv.Quote.Single && entry.Value.Contains('$')));
            }
        }
        return found;
    }

    /// <summary>$env:NAME = 'value' and [Environment]::SetEnvironmentVariable('NAME', 'value') with a quoted literal value.</summary>
    public static List<PlaintextExport> PowerShellExports(string text, string file)
    {
        var found = new List<PlaintextExport>();
        foreach (var (line, number) in OutsideEnvyBlock(text))
        {
            var trimmed = line.Trim();
            string key, rest;
            if (EnvAssignment().Match(trimmed) is { Success: true } m) { key = m.Groups[1].Value; rest = m.Groups[2].Value; }
            else if (SetEnvironmentVariableCall().Match(trimmed) is { Success: true } c) { key = c.Groups[2].Value; rest = c.Groups[3].Value; }
            else continue;
            if (EnvVar.KeyProblem(key) is not null || IgnoredKeys.Contains(key)) continue;
            if (PowerShellString(rest) is not { } parsed) continue;
            found.Add(new PlaintextExport(file, number, key, parsed.Value, parsed.Expands));
        }
        return found;
    }

    /// <summary>A PowerShell string literal at the start of <paramref name="text"/>, followed by nothing but a comment.</summary>
    internal static (string Value, bool Expands)? PowerShellString(string text)
    {
        if (text.Length < 2 || text[0] is not ('\'' or '"')) return null;
        var quote = text[0];
        var value = new System.Text.StringBuilder();
        var i = 1;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote) { value.Append(quote); i++; continue; }
                break;
            }
            if (quote == '"' && ch == '`' && i + 1 < text.Length)
            {
                var next = text[++i];
                value.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => next });
                continue;
            }
            value.Append(ch);
        }
        if (i >= text.Length) return null;
        var after = text[(i + 1)..].Trim().TrimEnd(';').Trim();
        if (after.Length > 0 && !after.StartsWith('#')) return null;
        var s = value.ToString();
        // A double-quoted string expands $variables, and only those references are kept: $(…) is not run.
        var expands = quote == '"' && s.Contains('$');
        if (expands && s.Contains("$(")) return null;
        return (s, expands);
    }

    public static List<PlaintextExport> UserEnvironmentExports(IUserEnvironment environment) =>
        environment.Read()
            .Where(kv => EnvVar.KeyProblem(kv.Key) is null && !IgnoredKeys.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PlaintextExport(PlaintextExport.UserEnvironment, 0, kv.Key, kv.Value, kv.Value.Contains('%')))
            .ToList();

    /// <summary>Everything from <paramref name="bashFiles"/>, <paramref name="powerShellFiles"/> and the user environment, in that order.</summary>
    public static List<PlaintextExport> All(IEnumerable<string> bashFiles, IEnumerable<string> powerShellFiles, IUserEnvironment? environment)
    {
        var found = new List<PlaintextExport>();
        foreach (var file in bashFiles)
            if (StartupFiles.ReadText(StartupFiles.Resolve(file)) is { } t) found.AddRange(BashExports(t.Text, file));
        foreach (var file in powerShellFiles)
            if (StartupFiles.ReadText(StartupFiles.Resolve(file)) is { } t) found.AddRange(PowerShellExports(t.Text, file));
        if (environment is not null) found.AddRange(UserEnvironmentExports(environment));
        return found;
    }

    static IEnumerable<(string Line, int Number)> OutsideEnvyBlock(string text)
    {
        var inBlock = false;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == StartupFiles.BeginMarker) { inBlock = true; continue; }
            if (trimmed == StartupFiles.EndMarker) { inBlock = false; continue; }
            if (!inBlock) yield return (lines[i], i + 1);
        }
    }
}
