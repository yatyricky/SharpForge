namespace SharpForge.JassGen.Parser;

/// <summary>
/// Recursive-descent JASS parser. Only extracts the declarations needed for
/// C# binding generation: <c>type</c>, <c>native</c>/<c>function</c> signatures,
/// and globals. Function bodies are skipped.
///
/// Recovery: on any parse error, advance until the next top-level keyword.
/// </summary>
internal sealed class JassParser
{
    private readonly IReadOnlyList<JassToken> _tokens;
    private int _pos;

    public List<string> Errors { get; } = new();

    public JassParser(IReadOnlyList<JassToken> tokens) => _tokens = tokens;

    private JassToken Peek() => _tokens[_pos];
    private JassToken Advance() => _tokens[_pos++];

    private JassToken Expect(JassTokenType type, string? value = null)
    {
        var t = Advance();
        if (t.Type != type || (value is not null && !string.Equals(t.Value, value, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Expected {type}({value ?? "any"}) but got {t.Type}({t.Value}) at line {t.Line}");
        }
        return t;
    }

    private bool Match(JassTokenType type, string? value = null)
    {
        var t = Peek();
        if (t.Type == type && (value is null || string.Equals(t.Value, value, StringComparison.Ordinal)))
        {
            _pos++;
            return true;
        }
        return false;
    }

    public List<JassNode> Parse()
    {
        var nodes = new List<JassNode>();
        while (Peek().Type != JassTokenType.Eof)
        {
            try
            {
                ParseTopLevel(nodes);
            }
            catch (Exception ex)
            {
                Errors.Add(ex.Message);
                Recover();
            }
        }
        return nodes;
    }

    private void Recover()
    {
        while (Peek().Type != JassTokenType.Eof)
        {
            var t = Peek();
            if (t.Type == JassTokenType.Keyword &&
                t.Value is "type" or "constant" or "native" or "function" or "globals")
            {
                return;
            }
            _pos++;
        }
    }

    private void ParseTopLevel(List<JassNode> nodes)
    {
        var t = Peek();
        if (t.Type != JassTokenType.Keyword) { _pos++; return; }

        switch (t.Value)
        {
            case "type": nodes.Add(ParseTypeDecl()); break;
            case "globals": ParseGlobals(nodes); break;
            case "native": nodes.Add(ParseNative(isConstant: false)); break;
            case "constant": ParseConstant(nodes); break;
            case "function": nodes.Add(ParseFunction()); break;
            default: _pos++; break;
        }
    }

    private TypeDecl ParseTypeDecl()
    {
        Expect(JassTokenType.Keyword, "type");
        string name = Expect(JassTokenType.Ident).Value;
        Expect(JassTokenType.Keyword, "extends");
        string super = Expect(JassTokenType.Ident).Value;
        return new TypeDecl(name, super);
    }

    private void ParseGlobals(List<JassNode> nodes)
    {
        Expect(JassTokenType.Keyword, "globals");
        while (!(Peek().Type == JassTokenType.Keyword && Peek().Value == "endglobals"))
        {
            if (Peek().Type == JassTokenType.Eof) return;
            try
            {
                nodes.Add(ParseGlobalEntry());
            }
            catch (Exception ex)
            {
                Errors.Add(ex.Message);
                // skip to end of line by advancing until we find a token whose line differs
                int line = Peek().Line;
                while (Peek().Type != JassTokenType.Eof && Peek().Line == line) _pos++;
            }
        }
        Expect(JassTokenType.Keyword, "endglobals");
    }

    private GlobalDecl ParseGlobalEntry()
    {
        bool isConst = Match(JassTokenType.Keyword, "constant");
        // type token may be a keyword (e.g. 'string' is not a keyword in JASS, but be defensive) or ident.
        string type = Advance().Value;
        bool isArray = false;
        if (Peek().Type == JassTokenType.Keyword && Peek().Value == "array")
        {
            _pos++;
            isArray = true;
        }
        string name = Expect(JassTokenType.Ident).Value;
        string? value = null;
        if (Match(JassTokenType.Eq))
        {
            value = ReadExpr();
        }
        return new GlobalDecl(isConst, type, name, isArray, value);
    }

    private void ParseConstant(List<JassNode> nodes)
    {
        Expect(JassTokenType.Keyword, "constant");
        if (Peek().Type == JassTokenType.Keyword && Peek().Value == "native")
        {
            nodes.Add(ParseNative(isConstant: true));
            return;
        }
        // top-level `constant <type> <name> = <expr>` (rare outside globals; treat as global)
        string type = Advance().Value;
        string name = Expect(JassTokenType.Ident).Value;
        Expect(JassTokenType.Eq);
        string value = ReadExpr();
        nodes.Add(new GlobalDecl(IsConstant: true, type, name, IsArray: false, RawValue: value));
    }

    private FuncDecl ParseNative(bool isConstant)
    {
        Expect(JassTokenType.Keyword, "native");
        string name = Expect(JassTokenType.Ident).Value;
        Expect(JassTokenType.Keyword, "takes");
        var pars = ParseParams();
        Expect(JassTokenType.Keyword, "returns");
        string ret = ParseReturnType();
        return new FuncDecl(isConstant, IsNative: true, name, pars, ret);
    }

    private FuncDecl ParseFunction()
    {
        Expect(JassTokenType.Keyword, "function");
        string name = Expect(JassTokenType.Ident).Value;
        Expect(JassTokenType.Keyword, "takes");
        var pars = ParseParams();
        Expect(JassTokenType.Keyword, "returns");
        string ret = ParseReturnType();
        SkipFunctionBody();
        return new FuncDecl(IsConstant: false, IsNative: false, name, pars, ret);
    }

    private List<JassParam> ParseParams()
    {
        var pars = new List<JassParam>();
        if (Peek().Type == JassTokenType.Keyword && Peek().Value == "nothing")
        {
            _pos++;
            return pars;
        }
        while (true)
        {
            string type = Advance().Value;
            string name = Expect(JassTokenType.Ident).Value;
            pars.Add(new JassParam(type, name));
            if (!Match(JassTokenType.Comma)) break;
        }
        return pars;
    }

    private string ParseReturnType()
    {
        if (Peek().Type == JassTokenType.Keyword && Peek().Value == "nothing")
        {
            _pos++;
            return "nothing";
        }
        return Advance().Value;
    }

    private void SkipFunctionBody()
    {
        while (Peek().Type != JassTokenType.Eof)
        {
            var t = Advance();
            if (t.Type == JassTokenType.Keyword && t.Value == "endfunction") return;
        }
    }

    private static readonly HashSet<string> ExprStopKeywords = new(StringComparer.Ordinal)
    {
        "type", "constant", "native", "function", "globals", "endglobals",
        "endfunction", "local", "set", "call", "if", "else", "elseif",
        "endif", "loop", "endloop", "exitwhen", "return", "debug",
    };

    private string ReadExpr()
    {
        var sb = new StringBuilder();
        int startLine = Peek().Line;
        while (Peek().Type != JassTokenType.Eof)
        {
            var t = Peek();
            if (t.Type == JassTokenType.Keyword && ExprStopKeywords.Contains(t.Value)) break;
            // Stop at end of logical line — JASS expressions don't span newlines at top level.
            if (t.Line != startLine) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t.Type == JassTokenType.FourCC ? $"FourCC({t.Value})" : t.Value);
            _pos++;
        }
        return sb.ToString().Trim();
    }
}
