namespace SharpForge.JassGen.Parser;

/// <summary>
/// JASS lexer. Ported from the reference enhanced-parser.js — same token shape,
/// idiomatic C#. Comments and whitespace are skipped.
/// </summary>
internal static class JassTokenizer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "extends", "native", "constant", "function", "takes", "returns",
        "globals", "endglobals", "endfunction", "local", "set", "call", "return",
        "if", "then", "else", "elseif", "endif", "loop", "endloop", "exitwhen",
        "array", "nothing", "and", "or", "not", "debug", "true", "false",
    };

    public static List<JassToken> Tokenize(string source)
    {
        var tokens = new List<JassToken>(capacity: source.Length / 4);
        int i = 0;
        int line = 1;

        while (i < source.Length)
        {
            char c = source[i];

            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Line comment
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            // String literal
            if (c == '"')
            {
                int start = i;
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { i += 2; continue; }
                    if (source[i] == '\n') line++;
                    i++;
                }
                if (i < source.Length) i++; // closing quote
                tokens.Add(new JassToken(JassTokenType.String, source[start..i], line));
                continue;
            }

            // FourCC literal 'xxxx'
            if (c == '\'')
            {
                int start = i;
                i++;
                while (i < source.Length && source[i] != '\'' && source[i] != '\n') i++;
                if (i < source.Length && source[i] == '\'') i++;
                tokens.Add(new JassToken(JassTokenType.FourCC, source[start..i], line));
                continue;
            }

            // Number
            if (char.IsDigit(c) || (c == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
            {
                int start = i;
                if (c == '0' && i + 1 < source.Length && (source[i + 1] == 'x' || source[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < source.Length && IsHexDigit(source[i])) i++;
                }
                else
                {
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                    if (i < source.Length && source[i] == '.')
                    {
                        i++;
                        while (i < source.Length && char.IsDigit(source[i])) i++;
                    }
                }
                tokens.Add(new JassToken(JassTokenType.Number, source[start..i], line));
                continue;
            }

            // Identifier or keyword
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string val = source[start..i];
                tokens.Add(new JassToken(
                    Keywords.Contains(val) ? JassTokenType.Keyword : JassTokenType.Ident,
                    val,
                    line));
                continue;
            }

            // Punctuation
            switch (c)
            {
                case '(': tokens.Add(new JassToken(JassTokenType.LParen, "(", line)); break;
                case ')': tokens.Add(new JassToken(JassTokenType.RParen, ")", line)); break;
                case ',': tokens.Add(new JassToken(JassTokenType.Comma, ",", line)); break;
                case '=': tokens.Add(new JassToken(JassTokenType.Eq, "=", line)); break;
                case '[': tokens.Add(new JassToken(JassTokenType.LBracket, "[", line)); break;
                case ']': tokens.Add(new JassToken(JassTokenType.RBracket, "]", line)); break;
                default: break; // skip unknown punctuation (operators inside expressions etc.)
            }
            i++;
        }

        tokens.Add(new JassToken(JassTokenType.Eof, string.Empty, line));
        return tokens;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
