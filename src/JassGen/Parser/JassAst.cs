namespace SharpForge.JassGen.Parser;

/// <summary>JASS AST node base.</summary>
internal abstract record JassNode;

/// <summary><c>type Foo extends Bar</c></summary>
internal sealed record TypeDecl(string Name, string Super) : JassNode;

internal sealed record JassParam(string Type, string Name);

/// <summary><c>[constant] native|function Name takes ... returns ...</c></summary>
internal sealed record FuncDecl(
    bool IsConstant,
    bool IsNative,
    string Name,
    IReadOnlyList<JassParam> Params,
    string ReturnType) : JassNode;

/// <summary>Global declaration inside <c>globals ... endglobals</c>.</summary>
internal sealed record GlobalDecl(
    bool IsConstant,
    string Type,
    string Name,
    bool IsArray,
    string? RawValue) : JassNode;
