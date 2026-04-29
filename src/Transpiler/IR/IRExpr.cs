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
public sealed record IRArrayLiteral(IReadOnlyList<IRExpr> Items) : IRExpr;
public sealed record IRArrayNew(IRExpr Size) : IRExpr;
public sealed record IRStringConcat(IReadOnlyList<IRExpr> Parts) : IRExpr;
public sealed record IRDictionaryNew : IRExpr;
public sealed record IRDictionaryGet(IRExpr Table, IRExpr Key) : IRExpr;
public sealed record IRDictionarySet(IRExpr Table, IRExpr Key, IRExpr Value) : IRExpr;
public sealed record IRDictionaryRemove(IRExpr Table, IRExpr Key) : IRExpr;
public sealed record IRListSort(IRExpr List, IRExpr? Comparer) : IRExpr;
public sealed record IRBinary(string Op, IRExpr Left, IRExpr Right) : IRExpr;
public sealed record IRUnary(string Op, IRExpr Operand) : IRExpr;
public sealed record IRIs(IRExpr Value, IRTypeReference Type) : IRExpr;
public sealed record IRAs(IRExpr Value, IRTypeReference Type) : IRExpr;
