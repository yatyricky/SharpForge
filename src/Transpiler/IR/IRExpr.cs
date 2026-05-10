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
public sealed record IRDictionaryNew(bool UseLinearKeys = false, IRExpr? KeyComparer = null) : IRExpr;
public sealed record IRDictionaryCount(IRExpr Table, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionaryGet(IRExpr Table, IRExpr Key, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionaryAdd(IRExpr Table, IRExpr Key, IRExpr Value, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionarySet(IRExpr Table, IRExpr Key, IRExpr Value, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionaryRemove(IRExpr Table, IRExpr Key, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionaryContainsKey(IRExpr Table, IRExpr Key, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionaryClear(IRExpr Table, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionaryKeys(IRExpr Table, bool UseLinearKeys = false) : IRExpr;
public sealed record IRDictionaryValues(IRExpr Table, bool UseLinearKeys = false) : IRExpr;
public sealed record IRListNew(IReadOnlyList<IRExpr> Items) : IRExpr;
public sealed record IRListCount(IRExpr List) : IRExpr;
public sealed record IRListGet(IRExpr List, IRExpr Index) : IRExpr;
public sealed record IRListSet(IRExpr List, IRExpr Index, IRExpr Value) : IRExpr;
public sealed record IRListAdd(IRExpr List, IRExpr Value) : IRExpr;
public sealed record IRListAddRange(IRExpr List, IRExpr Items) : IRExpr;
public sealed record IRListClear(IRExpr List) : IRExpr;
public sealed record IRListContains(IRExpr List, IRExpr Value, IRExpr? EqualityComparer = null) : IRExpr;
public sealed record IRListIndexOf(IRExpr List, IRExpr Value, IRExpr? EqualityComparer = null) : IRExpr;
public sealed record IRListInsert(IRExpr List, IRExpr Index, IRExpr Value) : IRExpr;
public sealed record IRListRemove(IRExpr List, IRExpr Value, IRExpr? EqualityComparer = null) : IRExpr;
public sealed record IRListRemoveAt(IRExpr List, IRExpr Index) : IRExpr;
public sealed record IRListReverse(IRExpr List) : IRExpr;
public sealed record IRListSort(IRExpr List, IRExpr? Comparer) : IRExpr;
public sealed record IRListToArray(IRExpr List) : IRExpr;
public sealed record IRLuaRequire(IRExpr ModuleName) : IRExpr;
public sealed record IRLuaTable : IRExpr;
public sealed record IRLuaGlobal(IRExpr Name) : IRExpr;
public sealed record IRLuaAccess(IRExpr Target, IRExpr Name) : IRExpr;
public sealed record IRLuaMethodInvocation(IRExpr Target, IRExpr Name, IReadOnlyList<IRExpr> Arguments) : IRExpr;
public sealed record IRRuntimeInvocation(string Name, IReadOnlyList<IRExpr> Arguments) : IRExpr;
public sealed record IRBinary(string Op, IRExpr Left, IRExpr Right) : IRExpr;
public sealed record IRUnary(string Op, IRExpr Operand) : IRExpr;
public sealed record IRTernary(IRExpr Condition, IRExpr WhenTrue, IRExpr WhenFalse) : IRExpr;
public sealed record IRIs(IRExpr Value, IRTypeReference Type) : IRExpr;
public sealed record IRAs(IRExpr Value, IRTypeReference Type) : IRExpr;
public sealed record IRTableLiteralNew(IReadOnlyList<(string Key, IRExpr Value)> Fields) : IRExpr;
public sealed record IRStructValueTable(IRExpr Value, IReadOnlyList<string> Fields) : IRExpr;
