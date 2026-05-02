using SharpForge.JassGen.Parser;

namespace SharpForge.JassGen.Emit;

/// <summary>
/// Emits C# binding stubs from a JASS AST.
///
/// MVP shape (no namespace, no visible prefix at the call site):
///  - <c>Handles.g.cs</c>   — handle hierarchy as classes (no namespace).
///  - <c>Natives.g.cs</c>   — every native/function on <c>static partial class War3</c>.
///  - <c>Globals.g.cs</c>   — every global on <c>static partial class War3</c>.
///  - <c>NativeExt.g.cs</c> — hand-authored native helpers missing from JASS source.
///  - <c>GlobalUsings.g.cs</c> — <c>global using static War3;</c> so user code
///    just writes <c>BJDebugMsg("hi")</c> with no prefix.
///
/// JASS primitive types map to C# built-ins (<c>integer</c>=<c>int</c>, <c>real</c>=<c>float</c>, …);
/// custom JASS types stay lowercase to mirror source. Bodies are <c>=&gt; throw null!;</c>
/// stubs — the transpiler rewrites the calls to bare Lua references.
/// </summary>
internal sealed class CSharpEmitter
{
    public const string DefaultHostClass = "JASS";

    private readonly string _hostClass;

    public CSharpEmitter(string? hostClass = null)
    {
        _hostClass = string.IsNullOrWhiteSpace(hostClass) ? DefaultHostClass : hostClass;
    }

    public sealed record EmitResult(string Handles, string Natives, string Globals, string NativeExt, string GlobalUsings);

    public EmitResult Emit(IEnumerable<JassNode> nodes)
    {
        var types = new List<TypeDecl>();
        var funcs = new List<FuncDecl>();
        var globals = new List<GlobalDecl>();
        foreach (var n in nodes)
        {
            switch (n)
            {
                case TypeDecl t: types.Add(t); break;
                case FuncDecl f: funcs.Add(f); break;
                case GlobalDecl g: globals.Add(g); break;
            }
        }

        return new EmitResult(
            Handles: EmitHandles(types),
            Natives: EmitNatives(funcs),
            Globals: EmitGlobals(globals),
            NativeExt: EmitNativeExt(),
            GlobalUsings: EmitGlobalUsings());
    }

    // ------------------------------------------------------------------- types

    private static string EmitHandles(IReadOnlyList<TypeDecl> types)
    {
        var sb = new StringBuilder();
        Header(sb);
        sb.AppendLine("/// <summary>Root of the JASS handle hierarchy.</summary>");
        sb.AppendLine("public abstract class handle { protected handle() { } }");
        sb.AppendLine();

        foreach (var t in types.OrderBy(x => DependencyDepth(x.Name, types)))
        {
            sb.AppendLine($"public class {Escape(t.Name)} : {Escape(t.Super)} {{ protected {Escape(t.Name)}() {{ }} }}");
        }
        return sb.ToString();
    }

    private static int DependencyDepth(string name, IReadOnlyList<TypeDecl> types)
    {
        // Ensure base classes come before derived ones in source order.
        int depth = 0;
        string cur = name;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(cur))
        {
            var t = types.FirstOrDefault(x => x.Name == cur);
            if (t is null) break;
            depth++;
            cur = t.Super;
        }
        return depth;
    }

    // -------------------------------------------------------------- functions

    private string EmitNatives(IReadOnlyList<FuncDecl> funcs)
    {
        var sb = new StringBuilder();
        Header(sb);
        sb.AppendLine($"public static partial class {_hostClass}");
        sb.AppendLine("{");

        foreach (var f in funcs)
        {
            string ret = MapType(f.ReturnType, isReturn: true);
            string args = string.Join(", ", f.Params.Select(p => $"{MapParameterType(f, p)} {Escape(p.Name)}"));
            string kind = f.IsNative ? "native" : "function";
            sb.AppendLine($"    /// <summary>JASS {kind} <c>{f.Name}</c>.</summary>");
            sb.AppendLine($"    public static {ret} {Escape(f.Name)}({args}) => throw null!;");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- globals

    private string EmitGlobals(IReadOnlyList<GlobalDecl> globals)
    {
        var sb = new StringBuilder();
        Header(sb);
        sb.AppendLine($"public static partial class {_hostClass}");
        sb.AppendLine("{");

        foreach (var g in globals)
        {
            string type = MapType(g.Type) + (g.IsArray ? "[]" : string.Empty);
            if (!string.IsNullOrEmpty(g.RawValue))
            {
                sb.AppendLine($"    /// <summary>JASS: <c>{(g.IsConstant ? "constant " : string.Empty)}{g.Type} {g.Name} = {g.RawValue}</c></summary>");
            }
            sb.AppendLine($"    public static readonly {type} {Escape(g.Name)} = default!;");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    // ----------------------------------------------------------- global using

    private string EmitGlobalUsings()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by sf-jassgen </auto-generated>");
        sb.AppendLine($"global using static {_hostClass};");
        return sb.ToString();
    }

    // ----------------------------------------------------------- native ext

    private string EmitNativeExt()
    {
        var sb = new StringBuilder();
        Header(sb);
        sb.AppendLine($"public static partial class {_hostClass}");
        sb.AppendLine("{");
        sb.AppendLine("    public static int FourCC(string val) => throw null!;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ utils

    private static void Header(StringBuilder sb)
    {
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//   Generated by sf-jassgen from common.j / blizzard.j.");
        sb.AppendLine("//   Do not edit by hand; re-run the generator instead.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        // CS8981: type names are intentionally kept lowercase to mirror JASS source.
        // CS1591: doc comments are not exhaustive on every member.
        sb.AppendLine("#pragma warning disable CS8981, CS1591");
        sb.AppendLine();
    }

    private static string MapType(string jassType, bool isReturn = false)
    {
        return jassType switch
        {
            "nothing" when isReturn => "void",
            "integer" => "int",
            "real" => "float",
            "boolean" => "bool",
            "string" => "string",
            "code" => "global::System.Action",
            _ => Escape(jassType),
        };
    }

    private static string MapParameterType(FuncDecl function, JassParam parameter)
    {
        if (parameter.Type == "code" && function.Name is "Filter" or "Condition")
        {
            return "global::System.Func<bool>";
        }

        return MapType(parameter.Type);
    }

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    private static string Escape(string name) =>
        CSharpKeywords.Contains(name) ? "@" + name : name;
}
