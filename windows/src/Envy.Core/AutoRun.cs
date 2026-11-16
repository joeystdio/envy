using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Envy.Core;

/// <summary>Where Command Prompt's AutoRun command lives: the registry, or a file in a sandbox and in tests.</summary>
public interface IAutoRunStore
{
    /// <summary>The current value, or null when there is none.</summary>
    string? Read();
    /// <summary>Stores <paramref name="value"/>, or removes the value when it's null.</summary>
    void Write(string? value);
    /// <summary>Where it lives, for display.</summary>
    string Location { get; }
}

/// <summary>HKCU\Software\Microsoft\Command Processor\AutoRun, which cmd.exe runs whenever it starts (unless started with /d).</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryAutoRunStore : IAutoRunStore
{
    const string KeyPath = @"Software\Microsoft\Command Processor";
    public string Location => @"HKEY_CURRENT_USER\" + KeyPath + " › AutoRun";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("AutoRun", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (value is null) { key.DeleteValue("AutoRun", throwOnMissingValue: false); return; }
        // Keeps the kind of value it was, so %VARIABLES% in an expandable one keep working.
        var kind = key.GetValueNames().Contains("AutoRun", StringComparer.OrdinalIgnoreCase) ? key.GetValueKind("AutoRun") : RegistryValueKind.String;
        key.SetValue("AutoRun", value, kind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }
}

/// <summary>AutoRun kept in a text file, for ENVY_HOME sandboxes and tests.</summary>
public sealed class FileAutoRunStore(string path) : IAutoRunStore
{
    public string Location => path;

    public string? Read()
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Write(string? value)
    {
        if (value is null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }
}

/// <summary>
/// Chains Envy onto the AutoRun command without replacing what's there: other tools (conda, clink, …) keep running
/// first, and disconnecting takes out only Envy's part.
/// </summary>
public static partial class AutoRun
{
    [GeneratedRegex(@"(?:\s*&\s*)?if exist ""([^""]*[\\/]Envy[\\/]shell[\\/]init\.cmd)"" call ""\1""", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentRegex();

    [GeneratedRegex(@"^\s*&\s*")]
    private static partial Regex LeadingAmpersand();

    public static string Segment(string initCmd) => $"if exist \"{initCmd}\" call \"{initCmd}\"";

    public static bool IsInstalled(IAutoRunStore store) => store.Read() is { } value && SegmentRegex().IsMatch(value);

    /// <summary>The value with Envy added after whatever runs already.</summary>
    public static string Added(string? existing, string initCmd)
    {
        var current = (Removed(existing) ?? "").Trim();
        // No space before &: in "set A=1 & …" the space would become part of A's value.
        return current.Length == 0 ? Segment(initCmd) : $"{current}& {Segment(initCmd)}";
    }

    /// <summary>The value without Envy's part, or null when nothing else is left.</summary>
    public static string? Removed(string? existing)
    {
        if (existing is null) return null;
        // Envy's part takes the " & " in front of it along; when it came first, the one after it is left over.
        var rest = LeadingAmpersand().Replace(SegmentRegex().Replace(existing, ""), "").Trim();
        return rest.Length == 0 ? null : rest;
    }

    /// <summary>Adds Envy, keeping a copy of the previous value at <paramref name="backupFile"/>.</summary>
    public static void Install(IAutoRunStore store, string initCmd, string backupFile)
    {
        var existing = store.Read();
        if (existing is not null && SegmentRegex().IsMatch(existing) && existing.Contains(Segment(initCmd))) return;
        Backup(existing, backupFile);
        store.Write(Added(existing, initCmd));
    }

    public static void Uninstall(IAutoRunStore store, string backupFile)
    {
        var existing = store.Read();
        if (existing is null || !SegmentRegex().IsMatch(existing)) return;
        Backup(existing, backupFile);
        store.Write(Removed(existing));
    }

    static void Backup(string? value, string backupFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        File.WriteAllText(backupFile, value ?? "");
    }
}
