namespace SharpForge.Transpiler.IR;

/// <summary>
/// Root container for all IR produced from a Roslyn compilation.
/// A module is the unit handed to <c>LuaEmitter</c>.
/// </summary>
public sealed class IRModule
{
    public List<IREnum> Enums { get; } = new();
    public List<IRType> Types { get; } = new();
    public List<string> Diagnostics { get; } = new();
}

public sealed class IREnum
{
    public List<string> Comments { get; } = new();

    public required IReadOnlyList<string> NamespaceSegments { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }

    public List<IREnumMember> Members { get; } = new();
}

public sealed class IREnumMember
{
    public List<string> Comments { get; } = new();

    public required string Name { get; init; }
    public required IRLiteral Value { get; init; }
}

public sealed class IRType
{
    public List<string> Comments { get; } = new();

    /// <summary>Namespace segments, in declaration order. Empty for global types.</summary>
    public required IReadOnlyList<string> NamespaceSegments { get; init; }

    /// <summary>Simple (unqualified) type name, e.g. <c>Hero</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Display name used for header comments only (e.g. <c>Game.Hero</c>).</summary>
    public required string FullName { get; init; }

    public bool IsStatic { get; init; }

    public bool IsInterface { get; init; }

    public bool IsStruct { get; init; }

    public bool IsTableLiteral { get; init; }

    public IRTypeReference? BaseType { get; init; }

    public IRLuaClass? LuaClass { get; init; }

    public List<string> LuaRequires { get; } = new();

    public List<IRTypeReference> Interfaces { get; } = new();

    public List<IRField> Fields { get; } = new();
    public List<IRFunction> Methods { get; } = new();
}

public sealed record IRLuaClass(string ClassName, IRExpr? BaseType, IReadOnlyList<IRLuaModuleBinding> ModuleBindings);

public sealed record IRLuaModuleBinding(string LocalName, string ModuleName);

public sealed class IRField
{
    public List<string> Comments { get; } = new();

    public required string Name { get; init; }
    public IRExpr? Initializer { get; init; }
    public bool IsStatic { get; init; }
}

public sealed class IRFunction
{
    public List<string> Comments { get; } = new();

    public required string Name { get; init; }
    public required string LuaName { get; init; }
    public List<string> Parameters { get; } = new();
    public IRBlock Body { get; init; } = new();

    public string? InitLuaName { get; init; }

    public IRBaseConstructorCall? BaseConstructorCall { get; init; }

    /// <summary>Static method/function — emitted with <c>.</c> and no implicit self.</summary>
    public bool IsStatic { get; init; }

    /// <summary>Instance method — emitted with <c>:</c> so Lua passes <c>self</c>.</summary>
    public bool IsInstance { get; init; }

    /// <summary>
    /// True for <c>.New</c>: the emitter prepends
    /// <c>local self = setmetatable({}, { __index = T })</c> and appends <c>return self</c>.
    /// </summary>
    public bool IsConstructor { get; init; }

    public bool IsStaticConstructor { get; init; }

    public bool IsCoroutine { get; init; }

    public bool IsEntryPoint { get; init; }
}
