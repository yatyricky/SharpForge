using System.Globalization;
using System.Text;
using SharpForge.Transpiler.IR;

namespace SharpForge.Transpiler.Emitter;

/// <summary>
/// Emits Lua source from the SharpForge IR.
/// All transpiled types live under a single configurable root table (default
/// <c>SF__</c>) so the generated code never collides with hand-written
/// <c>war3map.lua</c> globals.
/// </summary>
public sealed class LuaEmitter
{
    private readonly string _rootTable;
    private readonly StringBuilder _sb = new();
    private readonly HashSet<string> _emittedTablePaths = new(StringComparer.Ordinal);
    private int _indent;
    private int _foreachId;
    private bool _emitTypeHelpers;

    public LuaEmitter(string rootTable)
    {
        if (string.IsNullOrWhiteSpace(rootTable))
        {
            throw new ArgumentException("Root table name must be non-empty.", nameof(rootTable));
        }
        _rootTable = rootTable;
    }

    public string Emit(IRModule module)
    {
        _sb.Clear();
        _emittedTablePaths.Clear();
        _indent = 0;
        _foreachId = 0;
        _emitTypeHelpers = UsesTypeChecks(module);

        EnsureRootEmitted();

        for (int i = 0; i < module.Types.Count; i++)
        {
            EmitType(module.Types[i]);
        }

        // Trim trailing blank lines, keep exactly one terminating newline.
        var s = _sb.ToString().TrimEnd('\n', '\r');
        return s + "\n";
    }

    private void EnsureRootEmitted()
    {
        if (_emittedTablePaths.Add(_rootTable))
        {
            WriteLine($"{_rootTable} = {_rootTable} or {{}}");
            if (_emitTypeHelpers)
            {
                WriteRootTypeHelpers();
            }
        }
    }

    private void WriteRootTypeHelpers()
    {
        WriteLine($"function {_rootTable}.__is(obj, target)");
        _indent++;
        WriteLine("if obj == nil then return false end");
        WriteLine("local t = obj.__sf_type");
        WriteLine("while t ~= nil do");
        _indent++;
        WriteLine("if t == target then return true end");
        WriteLine("if t.__sf_interfaces ~= nil and t.__sf_interfaces[target] then return true end");
        WriteLine("t = t.__sf_base");
        _indent--;
        WriteLine("end");
        WriteLine("return false");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.__as(obj, target)");
        _indent++;
        WriteLine($"if {_rootTable}.__is(obj, target) then return obj end");
        WriteLine("return nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void EmitType(IRType type)
    {
        // Walk namespace segments and emit each level once.
        var path = _rootTable;
        foreach (var seg in type.NamespaceSegments)
        {
            path = path + "." + seg;
            if (_emittedTablePaths.Add(path))
            {
                WriteLine($"{path} = {path} or {{}}");
            }
        }

        // Type table.
        var typePath = path + "." + type.Name;
        WriteLine($"-- {type.FullName}");
        WriteLine($"{typePath} = {typePath} or {{}}");
        if (type.BaseType is { } baseType)
        {
            WriteLine($"setmetatable({typePath}, {{ __index = {FormatTypeReference(baseType)} }})");
            WriteLine($"{typePath}.__sf_base = {FormatTypeReference(baseType)}");
        }
        if (type.Interfaces.Count > 0)
        {
            WriteIndent();
            _sb.Append(typePath).Append(".__sf_interfaces = {");
            for (int i = 0; i < type.Interfaces.Count; i++)
            {
                if (i > 0)
                {
                    _sb.Append(", ");
                }
                _sb.Append('[').Append(FormatTypeReference(type.Interfaces[i])).Append("] = true");
            }
            _sb.Append("}\n");
        }

        // Static field initializers.
        foreach (var f in type.Fields.Where(f => f.IsStatic))
        {
            WriteIndent();
            _sb.Append(typePath).Append('.').Append(f.Name).Append(" = ");
            if (f.Initializer is null)
            {
                _sb.Append("nil");
            }
            else
            {
                EmitExpr(f.Initializer);
            }
            _sb.Append('\n');
        }

        foreach (var staticConstructor in type.Methods.Where(m => m.IsStaticConstructor))
        {
            EmitBlock(staticConstructor.Body);
        }

        var methods = type.Methods.Where(m => !m.IsStaticConstructor).ToArray();
        for (int i = 0; i < methods.Length; i++)
        {
            EmitMethod(typePath, methods[i], type.Fields.Where(f => !f.IsStatic));
            if (i < methods.Length - 1)
            {
                _sb.Append('\n');
            }
        }
    }

    private void EmitMethod(string typePath, IRFunction m, IEnumerable<IRField> instanceFields)
    {
        if (m.IsConstructor)
        {
            EmitConstructor(typePath, m, instanceFields);
            return;
        }

        var sep = m.IsInstance ? ":" : ".";
        var paramList = string.Join(", ", m.Parameters);
        WriteLine($"function {typePath}{sep}{m.LuaName}({paramList})");
        _indent++;

        EmitBlock(m.Body);

        _indent--;
        WriteLine("end");
    }

    private void EmitConstructor(string typePath, IRFunction m, IEnumerable<IRField> instanceFields)
    {
        var paramList = string.Join(", ", m.Parameters);
        var initName = m.InitLuaName ?? "__Init";

        WriteLine($"function {typePath}.{initName}(self{(paramList.Length == 0 ? string.Empty : ", " + paramList)})");
        _indent++;
        if (m.BaseConstructorCall is not null)
        {
            EmitStmt(m.BaseConstructorCall);
        }
        WriteLine($"self.__sf_type = {typePath}");
        foreach (var field in instanceFields)
        {
            WriteIndent();
            _sb.Append("self.").Append(field.Name).Append(" = ");
            if (field.Initializer is null)
            {
                _sb.Append("nil");
            }
            else
            {
                EmitExpr(field.Initializer);
            }
            _sb.Append('\n');
        }
        EmitBlock(m.Body);
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {typePath}.{m.LuaName}({paramList})");
        _indent++;
        WriteLine($"local self = setmetatable({{}}, {{ __index = {typePath} }})");
        WriteIndent();
        _sb.Append(typePath).Append('.').Append(initName).Append("(self");
        foreach (var parameter in m.Parameters)
        {
            _sb.Append(", ").Append(parameter);
        }
        _sb.Append(")\n");
        WriteLine("return self");
        _indent--;
        WriteLine("end");
    }

    private void EmitBlock(IRBlock block)
    {
        foreach (var s in block.Statements)
        {
            EmitStmt(s);
        }
    }

    private void EmitStmt(IRStmt stmt)
    {
        switch (stmt)
        {
            case IRBlock b:
                WriteLine("do");
                _indent++;
                EmitBlock(b);
                _indent--;
                WriteLine("end");
                break;
            case IRLocalDecl ld:
                WriteIndent();
                _sb.Append("local ").Append(ld.Name);
                if (ld.Initializer is not null)
                {
                    _sb.Append(" = ");
                    EmitExpr(ld.Initializer);
                }
                _sb.Append('\n');
                break;
            case IRAssign a:
                WriteIndent();
                EmitExpr(a.Target);
                _sb.Append(" = ");
                EmitExpr(a.Value);
                _sb.Append('\n');
                break;
            case IRExprStmt es:
                WriteIndent();
                EmitExpr(es.Expression);
                _sb.Append('\n');
                break;
            case IRBaseConstructorCall baseCall:
                WriteIndent();
                _sb.Append(FormatTypeReference(baseCall.BaseType)).Append('.').Append(baseCall.InitLuaName).Append("(self");
                foreach (var argument in baseCall.Arguments)
                {
                    _sb.Append(", ");
                    EmitExpr(argument);
                }
                _sb.Append(")\n");
                break;
            case IRReturn r:
                WriteIndent();
                if (r.Value is null)
                {
                    _sb.Append("return\n");
                }
                else
                {
                    _sb.Append("return ");
                    EmitExpr(r.Value);
                    _sb.Append('\n');
                }
                break;
            case IRIf i:
                EmitIf(i);
                break;
            case IRWhile w:
                WriteIndent();
                _sb.Append("while ");
                EmitExpr(w.Condition);
                _sb.Append(" do\n");
                _indent++;
                EmitBlock(w.Body);
                WriteLine("::continue::");
                _indent--;
                WriteLine("end");
                break;
            case IRFor f:
                EmitFor(f);
                break;
            case IRForEach f:
                EmitForEach(f);
                break;
            case IRTry t:
                EmitTry(t);
                break;
            case IRThrow t:
                WriteIndent();
                _sb.Append("error(");
                if (t.Value is null)
                {
                    _sb.Append("nil");
                }
                else
                {
                    EmitExpr(t.Value);
                }
                _sb.Append(")\n");
                break;
            case IRBreak:
                WriteLine("break");
                break;
            case IRContinue:
                WriteLine("goto continue"); // Lua 5.3 has no `continue`
                break;
            case IRRawComment c:
                WriteLine($"-- {c.Text}");
                break;
        }
    }

    private void EmitIf(IRIf i)
    {
        WriteIndent();
        _sb.Append("if ");
        EmitExpr(i.Condition);
        _sb.Append(" then\n");
        _indent++;
        EmitBlock(i.Then);
        _indent--;
        if (i.Else is { } el)
        {
            if (el.Statements.Count == 1 && el.Statements[0] is IRIf nested)
            {
                WriteIndent();
                _sb.Append("elseif ");
                EmitExpr(nested.Condition);
                _sb.Append(" then\n");
                _indent++;
                EmitBlock(nested.Then);
                _indent--;
                if (nested.Else is { } nestedElse)
                {
                    WriteLine("else");
                    _indent++;
                    EmitBlock(nestedElse);
                    _indent--;
                }
            }
            else
            {
                WriteLine("else");
                _indent++;
                EmitBlock(el);
                _indent--;
            }
        }
        WriteLine("end");
    }

    private void EmitFor(IRFor f)
    {
        WriteLine("do");
        _indent++;
        if (f.Initializer is not null)
        {
            EmitStmt(f.Initializer);
        }

        WriteIndent();
        _sb.Append("while ");
        if (f.Condition is null)
        {
            _sb.Append("true");
        }
        else
        {
            EmitExpr(f.Condition);
        }
        _sb.Append(" do\n");

        _indent++;
        EmitBlock(f.Body);
        WriteLine("::continue::");
        foreach (var incrementor in f.Incrementors)
        {
            EmitStmt(incrementor);
        }
        _indent--;
        WriteLine("end");

        _indent--;
        WriteLine("end");
    }

    private void EmitForEach(IRForEach f)
    {
        var id = _foreachId++;
        var collectionName = $"__sf_collection{id}";
        var indexName = $"__sf_i{id}";

        WriteLine("do");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(collectionName).Append(" = ");
        EmitExpr(f.Collection);
        _sb.Append('\n');
        WriteLine($"for {indexName} = 1, #{collectionName} do");
        _indent++;
        WriteLine($"local {f.ItemName} = {collectionName}[{indexName}]");
        EmitBlock(f.Body);
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
    }

    private void EmitTry(IRTry t)
    {
        WriteLine("do");
        _indent++;
        WriteLine("local __sf_ok, __sf_err = pcall(function()");
        _indent++;
        EmitBlock(t.Try);
        _indent--;
        WriteLine("end)");

        if (t.Catch is not null)
        {
            WriteLine("if not __sf_ok then");
            _indent++;
            if (!string.IsNullOrEmpty(t.CatchVariable))
            {
                WriteLine($"local {t.CatchVariable} = __sf_err");
            }
            EmitBlock(t.Catch);
            _indent--;
            WriteLine("end");
        }
        else
        {
            WriteLine("if not __sf_ok then error(__sf_err) end");
        }

        if (t.Finally is not null)
        {
            EmitBlock(t.Finally);
        }

        _indent--;
        WriteLine("end");
    }

    private void EmitExpr(IRExpr expr)
    {
        switch (expr)
        {
            case IRLiteral l:
                _sb.Append(FormatLiteral(l));
                break;
            case IRIdentifier id:
                _sb.Append(id.Name);
                break;
            case IRTypeReference tr:
                _sb.Append(FormatTypeReference(tr));
                break;
            case IRMemberAccess ma:
                EmitExpr(ma.Target);
                _sb.Append('.').Append(ma.Member);
                break;
            case IRElementAccess elementAccess:
                EmitExpr(elementAccess.Target);
                _sb.Append('[');
                EmitExpr(new IRBinary("+", elementAccess.Index, new IRLiteral(1, IRLiteralKind.Integer)));
                _sb.Append(']');
                break;
            case IRLength length:
                _sb.Append('#');
                EmitExpr(length.Target);
                break;
            case IRInvocation inv:
                if (inv.UseColon && inv.Callee is IRMemberAccess memberAccess)
                {
                    EmitExpr(memberAccess.Target);
                    _sb.Append(':').Append(memberAccess.Member);
                }
                else
                {
                    EmitExpr(inv.Callee);
                }
                _sb.Append('(');
                for (int i = 0; i < inv.Arguments.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(inv.Arguments[i]);
                }
                _sb.Append(')');
                break;
            case IRArrayLiteral array:
                _sb.Append('{');
                for (int i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(array.Items[i]);
                }
                _sb.Append('}');
                break;
            case IRBinary bin:
                // Always parenthesize: precedence between Lua and C# differs (e.g. `..`,
                // `and`/`or`, bitwise ops), so unconditional parens are the safe choice.
                _sb.Append('(');
                EmitExpr(bin.Left);
                _sb.Append(' ').Append(bin.Op).Append(' ');
                EmitExpr(bin.Right);
                _sb.Append(')');
                break;
            case IRUnary un:
                _sb.Append('(').Append(un.Op switch { "!" => "not ", _ => un.Op });
                EmitExpr(un.Operand);
                _sb.Append(')');
                break;
            case IRIs isExpr:
                _sb.Append(_rootTable).Append(".__is(");
                EmitExpr(isExpr.Value);
                _sb.Append(", ").Append(FormatTypeReference(isExpr.Type)).Append(')');
                break;
            case IRAs asExpr:
                _sb.Append(_rootTable).Append(".__as(");
                EmitExpr(asExpr.Value);
                _sb.Append(", ").Append(FormatTypeReference(asExpr.Type)).Append(')');
                break;
        }
    }

    private static string FormatLiteral(IRLiteral l) => l.Kind switch
    {
        IRLiteralKind.Nil => "nil",
        IRLiteralKind.Boolean => (bool)l.Value! ? "true" : "false",
        IRLiteralKind.Integer => Convert.ToInt64(l.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        IRLiteralKind.Real => ((double)l.Value!).ToString("R", CultureInfo.InvariantCulture),
        IRLiteralKind.String => "\"" + EscapeLuaString((string)l.Value!) + "\"",
        _ => "nil",
    };

    private static string EscapeLuaString(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private string FormatTypeReference(IRTypeReference type)
    {
        var sb = new StringBuilder(_rootTable);
        foreach (var segment in type.NamespaceSegments)
        {
            sb.Append('.').Append(segment);
        }
        sb.Append('.').Append(type.Name);
        return sb.ToString();
    }

    private static bool UsesTypeChecks(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesTypeChecks(m.Body));

    private static bool BlockUsesTypeChecks(IRBlock block)
        => block.Statements.Any(StmtUsesTypeChecks);

    private static bool StmtUsesTypeChecks(IRStmt stmt)
        => stmt switch
        {
            IRBlock block => BlockUsesTypeChecks(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesTypeChecks(local.Initializer),
            IRAssign assign => ExprUsesTypeChecks(assign.Target) || ExprUsesTypeChecks(assign.Value),
            IRExprStmt exprStmt => ExprUsesTypeChecks(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesTypeChecks),
            IRReturn ret => ret.Value is not null && ExprUsesTypeChecks(ret.Value),
            IRIf iff => ExprUsesTypeChecks(iff.Condition) || BlockUsesTypeChecks(iff.Then) || (iff.Else is not null && BlockUsesTypeChecks(iff.Else)),
            IRWhile wh => ExprUsesTypeChecks(wh.Condition) || BlockUsesTypeChecks(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesTypeChecks(fr.Initializer))
                || (fr.Condition is not null && ExprUsesTypeChecks(fr.Condition))
                || fr.Incrementors.Any(StmtUsesTypeChecks)
                || BlockUsesTypeChecks(fr.Body),
            IRForEach fe => ExprUsesTypeChecks(fe.Collection) || BlockUsesTypeChecks(fe.Body),
            IRTry tr => BlockUsesTypeChecks(tr.Try)
                || (tr.Catch is not null && BlockUsesTypeChecks(tr.Catch))
                || (tr.Finally is not null && BlockUsesTypeChecks(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesTypeChecks(th.Value),
            _ => false,
        };

    private static bool ExprUsesTypeChecks(IRExpr expr)
        => expr switch
        {
            IRIs or IRAs => true,
            IRMemberAccess member => ExprUsesTypeChecks(member.Target),
            IRElementAccess element => ExprUsesTypeChecks(element.Target) || ExprUsesTypeChecks(element.Index),
            IRLength length => ExprUsesTypeChecks(length.Target),
            IRInvocation invocation => ExprUsesTypeChecks(invocation.Callee) || invocation.Arguments.Any(ExprUsesTypeChecks),
            IRArrayLiteral array => array.Items.Any(ExprUsesTypeChecks),
            IRBinary binary => ExprUsesTypeChecks(binary.Left) || ExprUsesTypeChecks(binary.Right),
            IRUnary unary => ExprUsesTypeChecks(unary.Operand),
            _ => false,
        };

    private void WriteIndent() => _sb.Append(' ', _indent * 4);
    private void WriteLine(string s) { WriteIndent(); _sb.Append(s).Append('\n'); }
}
