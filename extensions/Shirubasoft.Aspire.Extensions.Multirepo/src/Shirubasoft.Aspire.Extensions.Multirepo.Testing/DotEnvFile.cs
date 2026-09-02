using System.Text;

namespace Aspire.Hosting.Testing;

internal static class DotEnvFile
{
    private static readonly Dictionary<char, char> EscapedCharacters =
        new Dictionary<char, char>
        {
            ['n'] = '\n',
            ['r'] = '\r',
            ['t'] = '\t',
            ['"'] = '"',
            ['\\'] = '\\'
        };

    public static Dictionary<string, string> Load(string filePath)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(filePath))
        {
            lineNumber++;
            var entry = ParseLine(line, filePath, lineNumber);
            if (entry is null)
            {
                continue;
            }

            AddEntry(values, entry.Value, filePath, lineNumber);
        }

        return values;
    }

    private static KeyValuePair<string, string>? ParseLine(
        string line,
        string filePath,
        int lineNumber)
    {
        var content = line.TrimStart('\uFEFF').TrimStart();
        if (IsIgnored(content))
        {
            return null;
        }

        content = TrimExportPrefix(content);
        return ParseEntry(content, filePath, lineNumber);
    }

    private static bool IsIgnored(string content) =>
        content.Length == 0 || content[0] == '#';

    private static string TrimExportPrefix(string content)
    {
        const string export = "export";
        if (!content.StartsWith(export, StringComparison.Ordinal))
        {
            return content;
        }

        return TrimMatchedExportPrefix(content, export);
    }

    private static string TrimMatchedExportPrefix(string content, string export)
    {
        if (content.Length == export.Length)
        {
            return content;
        }

        return char.IsWhiteSpace(content[export.Length])
            ? content[export.Length..].TrimStart()
            : content;
    }

    private static KeyValuePair<string, string> ParseEntry(
        string content,
        string filePath,
        int lineNumber)
    {
        var separator = content.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            throw InvalidLine(filePath, lineNumber, "expected KEY=VALUE");
        }

        var key = content[..separator].Trim();
        ValidateKey(key, filePath, lineNumber);
        var value = ParseValue(content[(separator + 1)..], filePath, lineNumber);
        return KeyValuePair.Create(key, value);
    }

    private static void ValidateKey(string key, string filePath, int lineNumber)
    {
        if (!IsValidKey(key))
        {
            throw InvalidLine(filePath, lineNumber, $"'{key}' is not a valid environment variable name");
        }
    }

    private static void AddEntry(
        Dictionary<string, string> values,
        KeyValuePair<string, string> entry,
        string filePath,
        int lineNumber)
    {
        if (!values.TryAdd(entry.Key, entry.Value))
        {
            throw InvalidLine(
                filePath,
                lineNumber,
                $"environment variable '{entry.Key}' is defined more than once");
        }
    }

    private static string ParseValue(string value, string filePath, int lineNumber)
    {
        var content = value.TrimStart();
        if (content.Length == 0)
        {
            return string.Empty;
        }

        return ParseNonEmptyValue(content, filePath, lineNumber);
    }

    private static string ParseNonEmptyValue(string content, string filePath, int lineNumber)
    {
        if (content[0] == '\'')
        {
            return ParseSingleQuotedValue(content, filePath, lineNumber);
        }

        return ParseNonSingleQuotedValue(content, filePath, lineNumber);
    }

    private static string ParseNonSingleQuotedValue(string content, string filePath, int lineNumber) =>
        content[0] == '"'
            ? ParseDoubleQuotedValue(content, filePath, lineNumber)
            : ParseUnquotedValue(content);

    private static string ParseSingleQuotedValue(string content, string filePath, int lineNumber)
    {
        var closingQuote = content.IndexOf('\'', 1);
        if (closingQuote < 0)
        {
            throw InvalidLine(filePath, lineNumber, "single-quoted value is not terminated");
        }

        ValidateRemainder(content[(closingQuote + 1)..], filePath, lineNumber);
        return content[1..closingQuote];
    }

    private static string ParseDoubleQuotedValue(string content, string filePath, int lineNumber)
    {
        var value = new StringBuilder(content.Length);
        for (var index = 1; index < content.Length; index++)
        {
            if (content[index] == '"')
            {
                return CompleteDoubleQuotedValue(content, value, index, filePath, lineNumber);
            }

            index = AppendDoubleQuotedCharacter(content, value, index, filePath, lineNumber);
        }

        throw InvalidLine(filePath, lineNumber, "double-quoted value is not terminated");
    }

    private static string CompleteDoubleQuotedValue(
        string content,
        StringBuilder value,
        int closingQuoteIndex,
        string filePath,
        int lineNumber)
    {
        ValidateRemainder(content[(closingQuoteIndex + 1)..], filePath, lineNumber);
        return value.ToString();
    }

    private static int AppendDoubleQuotedCharacter(
        string content,
        StringBuilder value,
        int index,
        string filePath,
        int lineNumber)
    {
        var character = content[index];
        if (character != '\\')
        {
            value.Append(character);
            return index;
        }

        return AppendEscapedCharacter(content, value, index + 1, filePath, lineNumber);
    }

    private static int AppendEscapedCharacter(
        string content,
        StringBuilder value,
        int escapedIndex,
        string filePath,
        int lineNumber)
    {
        if (escapedIndex >= content.Length)
        {
            throw InvalidLine(filePath, lineNumber, "double-quoted value ends with an incomplete escape");
        }

        var escaped = content[escapedIndex];
        if (EscapedCharacters.TryGetValue(escaped, out var replacement))
        {
            value.Append(replacement);
            return escapedIndex;
        }

        value.Append('\\').Append(escaped);
        return escapedIndex;
    }

    private static string ParseUnquotedValue(string content)
    {
        for (var index = 0; index < content.Length; index++)
        {
            if (IsCommentStart(content, index))
            {
                return content[..index].TrimEnd();
            }
        }

        return content.TrimEnd();
    }

    private static bool IsCommentStart(string content, int index)
    {
        if (content[index] != '#')
        {
            return false;
        }

        return index == 0 || char.IsWhiteSpace(content[index - 1]);
    }

    private static void ValidateRemainder(string remainder, string filePath, int lineNumber)
    {
        var trailing = remainder.TrimStart();
        if (trailing.Length > 0 && trailing[0] != '#')
        {
            throw InvalidLine(filePath, lineNumber, "quoted value has unexpected trailing content");
        }
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        return IsValidFirstKeyCharacter(key[0]) && key.All(IsValidKeyCharacter);
    }

    private static bool IsValidFirstKeyCharacter(char character) =>
        char.IsLetter(character) || character == '_';

    private static bool IsValidKeyCharacter(char character) =>
        char.IsLetterOrDigit(character) || "_.-".Contains(character, StringComparison.Ordinal);

    private static InvalidOperationException InvalidLine(string filePath, int lineNumber, string detail) =>
        new($"The environment file '{filePath}' is invalid at line {lineNumber}: {detail}.");
}
