using System.Text;

namespace Envy.Core;

/// <summary>Reads and writes .env text: KEY=value lines, optional export, quotes, comments and multi-line quoted values.</summary>
public static class DotEnv
{
    public enum Quote { None, Single, Double }

    public sealed record Entry(string Key, string Value, Quote Quote, int Line);

    public static List<Entry> Parse(string text)
    {
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var entries = new List<Entry>();
        int i = 0, line = 1;

        static bool IsBlank(char c) => c is ' ' or '\t';
        void SkipBlanks() { while (i < s.Length && IsBlank(s[i])) i++; }
        void SkipToLineEnd() { while (i < s.Length && s[i] != '\n') i++; }

        while (i < s.Length)
        {
            SkipBlanks();
            if (i >= s.Length) break;
            if (s[i] == '\n') { i++; line++; continue; }
            if (s[i] == '#') { SkipToLineEnd(); continue; }

            var startLine = line;
            var keyStart = i;
            while (i < s.Length && s[i] != '=' && s[i] != '\n') i++;
            // A line without "=" isn't an assignment; the newline is consumed by the loop.
            if (i >= s.Length || s[i] != '=') continue;
            var key = s[keyStart..i].Trim(' ', '\t');
            i++;
            if ((key.StartsWith("export ") || key.StartsWith("export\t")) && key.Length > 7)
                key = key[7..].Trim(' ', '\t');

            var valueStart = i;
            SkipBlanks();
            var skippedBlank = i > valueStart;

            var value = new StringBuilder();
            var quote = Quote.None;
            if (i < s.Length && s[i] is '"' or '\'' or '`')
            {
                var q = s[i];
                quote = q == '"' ? Quote.Double : Quote.Single;
                i++;
                while (i < s.Length)
                {
                    var c = s[i];
                    if (c == q) { i++; break; }
                    if (c == '\n') line++;
                    if (q == '"' && c == '\\' && i + 1 < s.Length)
                    {
                        var next = s[i + 1];
                        switch (next)
                        {
                            case 'n': value.Append('\n'); break;
                            case 't': value.Append('\t'); break;
                            case 'r': value.Append('\r'); break;
                            case '"' or '\\' or '$': value.Append(next); break;
                            default: value.Append(c).Append(next); break;
                        }
                        if (next == '\n') line++;
                        i += 2;
                        continue;
                    }
                    value.Append(c);
                    i++;
                }
                SkipToLineEnd(); // anything after the closing quote, e.g. a comment
            }
            else
            {
                while (i < s.Length && s[i] != '\n')
                {
                    // " #" starts a comment; so does "#" right after "=" when there was whitespace before it.
                    if (s[i] == '#' && (value.Length > 0 ? IsBlank(value[^1]) : skippedBlank)) break;
                    value.Append(s[i]);
                    i++;
                }
                SkipToLineEnd();
                var trimmed = value.ToString().Trim(' ', '\t');
                value.Clear().Append(trimmed);
            }

            if (key.Length > 0) entries.Add(new Entry(key, value.ToString(), quote, startLine));
        }
        return entries;
    }

    /// <summary>.env text for <paramref name="vars"/>, quoting only where needed so the file reads back identically.</summary>
    public static string Serialize(IEnumerable<EnvVar> vars, string? header = null)
    {
        var output = new StringBuilder();
        if (header is not null)
            foreach (var line in header.Split('\n')) output.Append("# ").Append(line).Append('\n');
        foreach (var v in vars) output.Append(v.Key).Append('=').Append(Quoted(v.Value, v.Expands)).Append('\n');
        return output.ToString();
    }

    internal static string Quoted(string value, bool expands)
    {
        const string plain = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-./:@%+,";
        if (value.Length > 0 && value.All(plain.Contains)) return value;
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        if (expands) return $"\"{escaped}\"";
        if (!value.Contains('\'') && !value.Contains('\n')) return $"'{value}'";
        return $"\"{escaped.Replace("$", "\\$")}\"";
    }

    /// <summary>.env text, or a JSON object such as appsettings.json.</summary>
    public static List<Entry> ParseAny(string text)
    {
        if (AppSettings.LooksLikeJson(text) && AppSettings.EntriesFromJson(text) is { } entries) return entries;
        return Parse(text);
    }
}
