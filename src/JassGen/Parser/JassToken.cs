namespace SharpForge.JassGen.Parser;

internal enum JassTokenType
{
    Keyword,
    Ident,
    Number,
    String,
    FourCC,
    LParen,
    RParen,
    Comma,
    Eq,
    LBracket,
    RBracket,
    Eof,
}

internal readonly record struct JassToken(JassTokenType Type, string Value, int Line);
