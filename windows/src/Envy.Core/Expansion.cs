using System.Text;
using System.Text.RegularExpressions;

namespace Envy.Core;

/// <summary>
/// The value language behind "Expand": %NAME%, $NAME, ${NAME} and $env:NAME refer to other variables, and ~ at the start
/// (or after ; or :) is your user folder. Everything else is literal, so backslashes in Windows paths need no escaping.
/// Each shell resolves the references against its own environment when it loads the value, which lets a value build
/// on anything set before it. Commands such as $(…) are not run.
/// </summary>
public static partial class Expansion
{
    public abstract record Token;
    public sealed record Literal(string Text) : Token;
    public sealed record Reference(string Name) : Token;
    public sealed record Home : Token;

    [GeneratedRegex(@"\G%([A-Za-z_][A-Za-z0-9_().\-]*)%")]
    private static partial Regex PercentRef();

    [GeneratedRegex(@"\G\$\{([A-Za-z_][A-Za-z0-9_().\-]*)\}")]
    private static partial Regex BraceRef();

    [GeneratedRegex(@"\G\$env:([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellRef();

    [GeneratedRegex(@"\G\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DollarRef();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    public static bool IsIdentifier(string name) => IdentifierRegex().IsMatch(name);

    public static List<Token> Parse(string value)
    {
        var tokens = new List<Token>();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0) tokens.Add(new Literal(literal.ToString()));
            literal.Clear();
        }

        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            var atListStart = i == 0 || value[i - 1] is ';' or ':';
            if (c == '~' && atListStart && (i + 1 == value.Length || value[i + 1] is '/' or '\\' or ';' or ':'))
            {
                Flush();
                tokens.Add(new Home());
                i++;
                continue;
            }
            Match m;
            if (c == '%' && (m = PercentRef().Match(value, i)).Success
                || c == '$' && ((m = BraceRef().Match(value, i)).Success || (m = PowerShellRef().Match(value, i)).Success || (m = DollarRef().Match(value, i)).Success))
            {
                Flush();
                tokens.Add(new Reference(m.Groups[1].Value));
                i += m.Length;
                continue;
            }
            literal.Append(c);
            i++;
        }
        Flush();
        return tokens;
    }
}
