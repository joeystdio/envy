using System.Text.Json;

namespace Envy.Core;

/// <summary>
/// .NET configuration as environment variables. Nested keys are joined with __, which is how .NET reads configuration
/// from the environment: {"ConnectionStrings": {"Default": …}} becomes ConnectionStrings__Default, and a set variable
/// overrides the value in the file.
/// </summary>
public static class AppSettings
{
    /// <summary>Read in this order; later files win, as in the Development environment.</summary>
    public static readonly string[] FileNames = ["appsettings.json", "appsettings.Development.json"];

    /// <summary>Framework boilerplate that is rarely worth exporting.</summary>
    static readonly HashSet<string> BoilerplateSections = ["Logging", "AllowedHosts"];

    static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static bool LooksLikeJson(string text) => text.TrimStart().StartsWith('{');

    /// <summary>
    /// Flattened entries in key order, or null when <paramref name="text"/> isn't a JSON object. Comments and trailing
    /// commas are accepted, as .NET accepts them. Strings come back single-quoted so they're never expanded.
    /// </summary>
    public static List<DotEnv.Entry>? EntriesFromJson(string text, bool skipBoilerplate = true)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, Options); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var output = new List<DotEnv.Entry>();
            foreach (var property in Sorted(doc.RootElement))
            {
                if (skipBoilerplate && BoilerplateSections.Contains(property.Name)) continue;
                Flatten(property.Value, property.Name, output);
            }
            return output;
        }
    }

    // Duplicate keys keep the last value, as .NET configuration does.
    static IEnumerable<JsonProperty> Sorted(JsonElement obj) =>
        obj.EnumerateObject().GroupBy(p => p.Name).Select(g => g.Last()).OrderBy(p => p.Name, StringComparer.Ordinal);

    static void Flatten(JsonElement value, string path, List<DotEnv.Entry> output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in Sorted(value)) Flatten(p.Value, $"{path}__{p.Name}", output);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray()) Flatten(item, $"{path}__{index++}", output);
                break;
            case JsonValueKind.String:
                output.Add(new DotEnv.Entry(path, value.GetString()!, DotEnv.Quote.Single, 0));
                break;
            case JsonValueKind.Number:
                output.Add(new DotEnv.Entry(path, value.GetRawText(), DotEnv.Quote.None, 0));
                break;
            case JsonValueKind.True or JsonValueKind.False:
                output.Add(new DotEnv.Entry(path, value.ValueKind == JsonValueKind.True ? "true" : "false", DotEnv.Quote.None, 0));
                break;
        }
    }

    /// <summary>
    /// The project's dotnet user-secrets: the UserSecretsId from a .csproj in <paramref name="folder"/>, read from
    /// %APPDATA%\Microsoft\UserSecrets\&lt;id&gt;\secrets.json, with Section:Key names turned into Section__Key.
    /// </summary>
    public static List<DotEnv.Entry>? UserSecrets(string folder, string appData)
    {
        if (UserSecretsId(folder) is not { } id) return null;
        var file = Path.Combine(appData, "Microsoft", "UserSecrets", id, "secrets.json");
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return EntriesFromJson(text, skipBoilerplate: false)?.Select(e => e with { Key = e.Key.Replace(":", "__") }).ToList();
    }

    internal static string? UserSecretsId(string folder)
    {
        string[] projects;
        try { projects = Directory.GetFiles(folder, "*.csproj"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var project in projects.Order(StringComparer.Ordinal))
        {
            string text;
            try { text = File.ReadAllText(project); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var start = text.IndexOf("<UserSecretsId>", StringComparison.Ordinal);
            if (start < 0) continue;
            start += "<UserSecretsId>".Length;
            var end = text.IndexOf("</UserSecretsId>", start, StringComparison.Ordinal);
            if (end < 0) continue;
            var id = text[start..end].Trim();
            // It becomes a path component, so only accept what dotnet itself generates.
            if (id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return id;
        }
        return null;
    }
}
