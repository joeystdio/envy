using System.Text.Json;
using System.Text.Json.Serialization;

namespace Envy.Core;

/// <summary>Reads and writes config.json (secrets blanked out; they live in Credential Manager).</summary>
public static class ConfigFile
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(EnvConfig config) => JsonSerializer.Serialize(config.WithoutSecretValues(), Options);

    public static EnvConfig Deserialize(string json)
    {
        var config = JsonSerializer.Deserialize<EnvConfig>(json, Options) ?? new EnvConfig();
        config.Normalize();
        return config;
    }

    /// <summary>The stored config, or a fresh one when there is no file yet. Throws when the file can't be read.</summary>
    public static EnvConfig Read(string path) => File.Exists(path) ? Deserialize(File.ReadAllText(path)) : new EnvConfig();

    public static void Write(string path, EnvConfig config)
    {
        PrivateFiles.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        PrivateFiles.AtomicWrite(path, Serialize(config));
    }

    /// <summary>Moves an unreadable file out of the way as config.unreadable.json, so a fresh start doesn't overwrite it.</summary>
    public static void SetAside(string path)
    {
        var aside = Path.Combine(Path.GetDirectoryName(path)!, "config.unreadable.json");
        try { File.Move(path, aside, overwrite: true); } catch (IOException) { }
    }
}
