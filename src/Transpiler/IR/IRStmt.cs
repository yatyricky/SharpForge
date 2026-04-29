namespace SharpForge.Transpiler.IR;

/// <summary>
/// Base type for all IR statements.
/// </summary>
public abstract record IRStmt;

public sealed record IRBlock : IRStmt
{
    public List<IRStmt> Statements { get; } = new();
}

public sealed record IRLocalDecl(string Name, IRExpr? Initializer) : IRStmt;
public sealed record IRAssign(IRExpr Target, IRExpr Value) : IRStmt;
public sealed record IRExprStmt(IRExpr Expression) : IRStmt;
public sealed record IRBaseConstructorCall(IRTypeReference BaseType, string InitLuaName, IReadOnlyList<IRExpr> Arguments) : IRStmt;
public sealed record IRReturn(IRExpr? Value) : IRStmt;
public sealed record IRIf(IRExpr Condition, IRBlock Then, IRBlock? Else) : IRStmt;
public sealed record IRWhile(IRExpr Condition, IRBlock Body) : IRStmt;
public sealed record IRFor(IRStmt? Initializer, IRExpr? Condition, IReadOnlyList<IRStmt> Incrementors, IRBlock Body) : IRStmt;
public sealed record IRBreak : IRStmt;
public sealed record IRContinue : IRStmt;

/// <summary>
/// Raw passthrough — used during early bring-up for syntax not yet lowered.
/// Emitted as a Lua comment so the output remains valid.
/// </summary>
public sealed record IRRawComment(string Text) : IRStmt;
