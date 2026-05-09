using System.Text;

namespace SharpForge.Transpiler.Frontend;

internal static class LuaRegexPatternCompiler
{
    private static readonly HashSet<char> RegexEscapableLiterals = new()
    {
        '.', '^', '$', '*', '+', '?', '{', '}', '[', ']', '\\', '|', '(', ')', '-',
    };

    public static bool TryCompile(string pattern, out string luaPattern, out string diagnostic)
    {
        var builder = new StringBuilder(pattern.Length);
        var canQuantify = false;

        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            switch (current)
            {
                case '^':
                    if (index != 0)
                    {
                        return Fail("regex anchor '^' is only supported at the start of the pattern", out luaPattern, out diagnostic);
                    }
                    builder.Append('^');
                    canQuantify = false;
                    break;
                case '$':
                    if (index != pattern.Length - 1)
                    {
                        return Fail("regex anchor '$' is only supported at the end of the pattern", out luaPattern, out diagnostic);
                    }
                    builder.Append('$');
                    canQuantify = false;
                    break;
                case '.':
                    builder.Append('.');
                    canQuantify = true;
                    break;
                case '*':
                case '+':
                case '?':
                    if (!canQuantify)
                    {
                        return Fail("regex quantifiers must follow a literal, '.', escape, or character class", out luaPattern, out diagnostic);
                    }
                    if (index + 1 < pattern.Length && pattern[index + 1] == '?')
                    {
                        return Fail("lazy regex quantifiers are not supported by Lua patterns", out luaPattern, out diagnostic);
                    }
                    builder.Append(current);
                    canQuantify = false;
                    break;
                case '[':
                    if (!TryAppendCharacterClass(pattern, ref index, builder, out diagnostic))
                    {
                        luaPattern = string.Empty;
                        return false;
                    }
                    canQuantify = true;
                    break;
                case '\\':
                    if (!TryAppendEscape(pattern, ref index, builder, inCharacterClass: false, out diagnostic))
                    {
                        luaPattern = string.Empty;
                        return false;
                    }
                    canQuantify = true;
                    break;
                case '|':
                    return Fail("regex alternation is not supported by Lua patterns", out luaPattern, out diagnostic);
                case '(':
                case ')':
                    return Fail("regex grouping and lookaround are not supported by Lua patterns", out luaPattern, out diagnostic);
                case '{':
                case '}':
                    return Fail("regex counted quantifiers are not supported by Lua patterns", out luaPattern, out diagnostic);
                default:
                    AppendLuaLiteral(builder, current);
                    canQuantify = true;
                    break;
            }
        }

        luaPattern = builder.ToString();
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryAppendCharacterClass(string pattern, ref int index, StringBuilder builder, out string diagnostic)
    {
        builder.Append('[');
        index++;
        if (index >= pattern.Length)
        {
            diagnostic = "unterminated regex character class";
            return false;
        }

        if (pattern[index] == '^')
        {
            builder.Append('^');
            index++;
        }

        var closed = false;
        for (; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == ']')
            {
                builder.Append(']');
                closed = true;
                break;
            }

            if (current == '[')
            {
                diagnostic = "nested or subtractive regex character classes are not supported by Lua patterns";
                return false;
            }

            if (current == '\\')
            {
                if (!TryAppendEscape(pattern, ref index, builder, inCharacterClass: true, out diagnostic))
                {
                    return false;
                }
                continue;
            }

            AppendLuaCharacterClassLiteral(builder, current);
        }

        if (!closed)
        {
            diagnostic = "unterminated regex character class";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryAppendEscape(string pattern, ref int index, StringBuilder builder, bool inCharacterClass, out string diagnostic)
    {
        index++;
        if (index >= pattern.Length)
        {
            diagnostic = "trailing regex escape is not supported";
            return false;
        }

        var escaped = pattern[index];
        switch (escaped)
        {
            case 'd': builder.Append("%d"); break;
            case 'D': builder.Append("%D"); break;
            case 's': builder.Append("%s"); break;
            case 'S': builder.Append("%S"); break;
            case 'w': builder.Append(inCharacterClass ? "%w_" : "[%w_]"); break;
            case 'W':
                if (inCharacterClass)
                {
                    diagnostic = "regex escape '\\W' is not supported inside Lua character classes";
                    return false;
                }
                builder.Append("[^%w_]");
                break;
            case 'n': AppendEscapedLiteral(builder, '\n', inCharacterClass); break;
            case 'r': AppendEscapedLiteral(builder, '\r', inCharacterClass); break;
            case 't': AppendEscapedLiteral(builder, '\t', inCharacterClass); break;
            case 'p':
            case 'P':
                diagnostic = "Unicode regex categories are not supported by Lua patterns";
                return false;
            default:
                if (char.IsDigit(escaped))
                {
                    diagnostic = "regex backreferences are not supported by Lua patterns";
                    return false;
                }
                if (!RegexEscapableLiterals.Contains(escaped))
                {
                    diagnostic = $"regex escape '\\{escaped}' is not supported by Lua patterns";
                    return false;
                }
                AppendEscapedLiteral(builder, escaped, inCharacterClass);
                break;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static void AppendEscapedLiteral(StringBuilder builder, char value, bool inCharacterClass)
    {
        if (inCharacterClass)
        {
            AppendLuaCharacterClassLiteral(builder, value);
            return;
        }

        AppendLuaLiteral(builder, value);
    }

    private static void AppendLuaLiteral(StringBuilder builder, char value)
    {
        if (IsLuaMagic(value))
        {
            builder.Append('%');
        }
        builder.Append(value);
    }

    private static void AppendLuaCharacterClassLiteral(StringBuilder builder, char value)
    {
        if (value is '%' or ']')
        {
            builder.Append('%');
        }
        builder.Append(value);
    }

    private static bool IsLuaMagic(char value)
        => value is '^' or '$' or '(' or ')' or '%' or '.' or '[' or ']' or '*' or '+' or '-' or '?';

    private static bool Fail(string message, out string luaPattern, out string diagnostic)
    {
        luaPattern = string.Empty;
        diagnostic = message;
        return false;
    }
}
