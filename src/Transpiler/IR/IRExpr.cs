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
public sealed record IRTypeReference(IReadOnlyList<string> NamespaceSegments, string Name) : IRExpr;
public sealed record IRMemberAccess(IRExpr Target, string Member) : IRExpr;
public sealed record IRElementAccess(IRExpr Target, IRExpr Index) : IRExpr;
public sealed record IRLength(IRExpr Target) : IRExpr;
public sealed record IRInvocation(IRExpr Callee, IReadOnlyList<IRExpr> Arguments, bool UseColon = false) : IRExpr;
public sealed record IRFunctionExpression(IReadOnlyList<string> Parameters, IRBlock Body) : IRExpr;
public sealed record IRArrayLiteral(IReadOnlyList<IRExpr> Items) : IRExpr;
public sealed record IRArrayNew(IRExpr Size) : IRExpr;
public sealed record IRStringConcat(IReadOnlyList<IRExpr> Parts) : IRExpr;
public sealed record IRLuaRequire(IRExpr ModuleName) : IRExpr;
public sealed record IRLuaTable : IRExpr;
public sealed record IRLuaGlobal(IRExpr Name) : IRExpr;
public sealed record IRLuaAccess(IRExpr Target, IRExpr Name) : IRExpr;
public sealed record IRLuaMethodInvocation(IRExpr Target, IRExpr Name, IReadOnlyList<IRExpr> Arguments) : IRExpr;
public sealed record IRRuntimeInvocation(string Name, IReadOnlyList<IRExpr> Arguments) : IRExpr;
public sealed record IRBinary(string Op, IRExpr Left, IRExpr Right) : IRExpr;
public sealed record IRUnary(string Op, IRExpr Operand) : IRExpr;
public sealed record IRTernary(IRExpr Condition, IRExpr WhenTrue, IRExpr WhenFalse) : IRExpr;
public sealed record IRCoalesceAssignment(IRExpr Target, IRExpr Value) : IRExpr;
public sealed record IRIs(IRExpr Value, IRExpr Type) : IRExpr;
public sealed record IRAs(IRExpr Value, IRExpr Type) : IRExpr;
public sealed record IRTableLiteralNew(IReadOnlyList<(string Key, IRExpr Value)> Fields) : IRExpr;
public sealed record IRStructValueTable(IRExpr Value, IReadOnlyList<string> Fields) : IRExpr;
