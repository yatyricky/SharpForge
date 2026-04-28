namespace SharpForge.Transpiler.IR;

/// <summary>
/// Base type for all IR expressions.
/// </summary>
public abstract record IRExpr;

public sealed record IRLiteral(object? Value, IRLiteralKind Kind) : IRExpr;

public enum IRLiteralKind
{
    Nil,
    Boolean,
    Integer,
    Real,
    String,
}

public sealed record IRIdentifier(string Name) : IRExpr;
public sealed record IRMemberAccess(IRExpr Target, string Member) : IRExpr;
public sealed record IRInvocation(IRExpr Callee, IReadOnlyList<IRExpr> Arguments) : IRExpr;
public sealed record IRBinary(string Op, IRExpr Left, IRExpr Right) : IRExpr;
public sealed record IRUnary(string Op, IRExpr Operand) : IRExpr;
