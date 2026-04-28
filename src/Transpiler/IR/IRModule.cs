namespace SharpForge.Transpiler.IR;

/// <summary>
/// Root container for all IR produced from a Roslyn compilation.
/// A module is the unit handed to <c>LuaEmitter</c>.
/// </summary>
public sealed class IRModule
{
    public List<IRType> Types { get; } = new();
}

public sealed class IRType
{
    /// <summary>Namespace segments, in declaration order. Empty for global types.</summary>
    public required IReadOnlyList<string> NamespaceSegments { get; init; }

    /// <summary>Simple (unqualified) type name, e.g. <c>Hero</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Display name used for header comments only (e.g. <c>Game.Hero</c>).</summary>
    public required string FullName { get; init; }

    public bool IsStatic { get; init; }

    public List<IRField> Fields { get; } = new();
    public List<IRFunction> Methods { get; } = new();
}

public sealed class IRField
{
    public required string Name { get; init; }
    public IRExpr? Initializer { get; init; }
    public bool IsStatic { get; init; }
}

public sealed class IRFunction
{
    public required string Name { get; init; }
    public required string LuaName { get; init; }
    public List<string> Parameters { get; } = new();
    public IRBlock Body { get; init; } = new();

    /// <summary>Static method/function — emitted with <c>.</c> and no implicit self.</summary>
    public bool IsStatic { get; init; }

    /// <summary>Instance method — emitted with <c>:</c> so Lua passes <c>self</c>.</summary>
    public bool IsInstance { get; init; }

    /// <summary>
    /// True for <c>.New</c>: the emitter prepends
    /// <c>local self = setmetatable({}, { __index = T })</c> and appends <c>return self</c>.
    /// </summary>
    public bool IsConstructor { get; init; }
}
