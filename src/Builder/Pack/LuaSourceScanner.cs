namespace SharpForge.Builder.Pack;

using System.Text;

internal static class LuaSourceScanner
{
    public static IReadOnlyList<string> ExtractDependencies(string source)
    {
        var dependencies = new List<string>();
        var forced = new StringBuilder();
        Scan(source, dependencies, forced, includeForcedDirectives: true);
        if (forced.Length > 0)
        {
            Scan(forced.ToString(), dependencies, new StringBuilder(), includeForcedDirectives: false);
        }

        return dependencies;
    }

    private static void Scan(
        string source,
        List<string> dependencies,
        StringBuilder forced,
        bool includeForcedDirectives)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch is '\'' or '"')
            {
                i = SkipQuotedString(source, i);
                continue;
            }

            if (ch == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                if (i + 3 < source.Length && source[i + 2] == '[' && source[i + 3] == '[')
                {
                    i = SkipLongComment(source, i + 4);
                    continue;
                }

                i = SkipLineComment(source, i + 2, forced, includeForcedDirectives);
                continue;
            }

            if (!IsIdentifierStart(ch))
            {
                continue;
            }

            var calleeStart = i;
            if (!TryReadCallee(source, ref i, out var callee) || !IsDependencyDirective(callee))
            {
                i = calleeStart;
                continue;
            }

            if (TryReadLiteralArgument(source, i + 1, out var dependency, out var endIndex))
            {
                dependencies.Add(dependency);
                i = endIndex;
            }
        }
    }

    private static bool TryReadCallee(string source, ref int index, out string callee)
    {
        var parts = new List<string>();
        while (index < source.Length)
        {
            SkipWhitespace(source, ref index);
            if (index >= source.Length || !IsIdentifierStart(source[index]))
            {
                break;
            }

            var start = index;
            index++;
            while (index < source.Length && IsIdentifierPart(source[index]))
            {
                index++;
            }

            parts.Add(source[start..index]);
            var dotIndex = index;
            SkipWhitespace(source, ref dotIndex);
            if (dotIndex >= source.Length || source[dotIndex] != '.')
            {
                index--;
                break;
            }

            dotIndex++;
            var afterDot = dotIndex;
            SkipWhitespace(source, ref afterDot);
            if (afterDot >= source.Length || !IsIdentifierStart(source[afterDot]))
            {
                index--;
                break;
            }

            index = afterDot;
        }

        callee = string.Join('.', parts);
        return parts.Count > 0;
    }

    private static bool TryReadLiteralArgument(string source, int index, out string literal, out int endIndex)
    {
        SkipWhitespace(source, ref index);
        if (index < source.Length && source[index] == '(')
        {
            index++;
            SkipWhitespace(source, ref index);
        }

        if (index >= source.Length || source[index] is not ('\'' or '"'))
        {
            literal = string.Empty;
            endIndex = index;
            return false;
        }

        literal = ReadQuotedString(source, index, out endIndex);
        return true;
    }

    private static string ReadQuotedString(string source, int index, out int endIndex)
    {
        var quote = source[index];
        var sb = new StringBuilder();
        for (var i = index + 1; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch == '\\' && i + 1 < source.Length)
            {
                i++;
                sb.Append(source[i]);
                continue;
            }

            if (ch == quote)
            {
                endIndex = i;
                return sb.ToString();
            }

            sb.Append(ch);
        }

        endIndex = source.Length - 1;
        return sb.ToString();
    }

    private static int SkipQuotedString(string source, int index)
    {
        var quote = source[index];
        for (var i = index + 1; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch == '\\')
            {
                if (i + 1 < source.Length)
                {
                    i++;
                }

                continue;
            }

            if (ch == quote)
            {
                return i;
            }
        }

        return source.Length - 1;
    }

    private static int SkipLongComment(string source, int index)
    {
        for (var i = index; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch == ']' && i + 1 < source.Length && source[i + 1] == ']')
            {
                return i + 1;
            }
        }

        return source.Length - 1;
    }

    private static int SkipLineComment(string source, int index, StringBuilder forced, bool includeForcedDirectives)
    {
        var comment = new StringBuilder();
        for (var i = index; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch is '\r' or '\n')
            {
                AppendForcedDirective(comment, forced, includeForcedDirectives);
                return i;
            }

            comment.Append(ch);
        }

        AppendForcedDirective(comment, forced, includeForcedDirectives);
        return source.Length - 1;
    }

    private static void AppendForcedDirective(StringBuilder comment, StringBuilder forced, bool includeForcedDirectives)
    {
        if (!includeForcedDirectives)
        {
            return;
        }

        var text = comment.ToString().TrimStart();
        if (text.StartsWith('!'))
        {
            forced.AppendLine(text[1..]);
        }
    }

    private static bool IsDependencyDirective(string callee)
        => callee is "require"
            or "dofile"
            or "doFile"
            or "loadfile"
            or "loadFile"
            or "load"
            or "package.load"
            or "include"
            or "import";

    private static void SkipWhitespace(string source, ref int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }
    }

    private static bool IsIdentifierStart(char ch)
        => ch == '_' || char.IsAsciiLetter(ch);

    private static bool IsIdentifierPart(char ch)
        => ch == '_' || char.IsAsciiLetterOrDigit(ch);
}
