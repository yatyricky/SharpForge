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
    private readonly HashSet<string> _usedIdentifiers = new(StringComparer.Ordinal);
    private int _indent;
    private int _tempId;
    private bool _emitTypeHelpers;
    private bool _emitStringConcatHelper;
    private bool _emitDictionaryHelpers;
    private bool _emitListHelpers;
    private bool _emitCoroutineHelpers;

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
        _tempId = 0;
        _emitTypeHelpers = UsesTypeChecks(module);
        _emitStringConcatHelper = UsesStringConcat(module);
        _emitDictionaryHelpers = UsesDictionaryHelpers(module);
        _emitListHelpers = UsesListHelpers(module);
        _emitCoroutineHelpers = UsesCoroutineHelpers(module);
        _usedIdentifiers.Clear();
        CollectIdentifiers(module);

        EnsureRootEmitted();

        for (int i = 0; i < module.Types.Count; i++)
        {
            EmitType(module.Types[i]);
        }

        EmitEntryPointCall(module);

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
            if (_emitStringConcatHelper)
            {
                WriteStringConcatHelper();
            }
            if (_emitCoroutineHelpers)
            {
                WriteCoroutineHelpers();
            }
            if (_emitDictionaryHelpers)
            {
                WriteDictionaryHelpers();
            }
            if (_emitListHelpers)
            {
                WriteListHelpers();
            }
        }
    }

    private void WriteRootTypeHelpers()
    {
        WriteLine($"function {_rootTable}.TypeIs__(obj, target)");
        _indent++;
        WriteLine("if obj == nil then return false end");
        WriteLine("local type = obj.__sf_type");
        WriteLine("while type ~= nil do");
        _indent++;
        WriteLine("if type == target then return true end");
        WriteLine("if type.__sf_interfaces ~= nil and type.__sf_interfaces[target] then return true end");
        WriteLine("type = type.__sf_base");
        _indent--;
        WriteLine("end");
        WriteLine("return false");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.TypeAs__(obj, target)");
        _indent++;
        WriteLine($"if {_rootTable}.TypeIs__(obj, target) then return obj end");
        WriteLine("return nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteStringConcatHelper()
    {
        WriteLine($"function {_rootTable}.StrConcat__(...)");
        _indent++;
        WriteLine("local result = \"\"");
        WriteLine("for i = 1, select(\"#\", ...) do");
        _indent++;
        WriteLine("local part = select(i, ...)");
        WriteLine("if part ~= nil then");
        _indent++;
        WriteLine("result = result .. tostring(part)");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        WriteLine("return result");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteCoroutineHelpers()
    {
        WriteLine($"{_rootTable}.CorTimerPool__ = {_rootTable}.CorTimerPool__ or {{}}");
        WriteLine($"{_rootTable}.CorTimerPoolSize__ = {_rootTable}.CorTimerPoolSize__ or 0");
        WriteLine($"{_rootTable}.CorMaxTimerPoolSize__ = {_rootTable}.CorMaxTimerPoolSize__ or 256");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.CorAcquireTimer__()");
        _indent++;
        WriteLine($"local size = {_rootTable}.CorTimerPoolSize__");
        WriteLine("if size > 0 then");
        _indent++;
        WriteLine($"local timer = {_rootTable}.CorTimerPool__[size]");
        WriteLine($"{_rootTable}.CorTimerPool__[size] = nil");
        WriteLine($"{_rootTable}.CorTimerPoolSize__ = size - 1");
        WriteLine("return timer");
        _indent--;
        WriteLine("end");
        WriteLine("return CreateTimer()");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.CorReleaseTimer__(timer)");
        _indent++;
        WriteLine("PauseTimer(timer)");
        WriteLine($"local size = {_rootTable}.CorTimerPoolSize__");
        WriteLine($"if size < {_rootTable}.CorMaxTimerPoolSize__ then");
        _indent++;
        WriteLine("size = size + 1");
        WriteLine($"{_rootTable}.CorTimerPool__[size] = timer");
        WriteLine($"{_rootTable}.CorTimerPoolSize__ = size");
        _indent--;
        WriteLine("else");
        _indent++;
        WriteLine("DestroyTimer(timer)");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.CorRun__(fn)");
        _indent++;
        WriteLine("local thread = coroutine.create(fn)");
        WriteLine("local ok, err = coroutine.resume(thread)");
        WriteLine("if not ok then error(err) end");
        WriteLine("return thread");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.CorWait__(milliseconds)");
        _indent++;
        WriteLine("if milliseconds <= 0 then return end");
        WriteLine("local thread = coroutine.running()");
        WriteLine("if thread == nil then error(\"CorWait must be called from a coroutine\") end");
        WriteLine("if coroutine.isyieldable ~= nil and not coroutine.isyieldable() then error(\"CorWait cannot yield from this context\") end");
        WriteLine($"local timer = {_rootTable}.CorAcquireTimer__()");
        WriteLine("TimerStart(timer, milliseconds / 1000, false, function()");
        _indent++;
        WriteLine("local ok, err = coroutine.resume(thread)");
        WriteLine($"{_rootTable}.CorReleaseTimer__(timer)");
        WriteLine("if not ok then error(err) end");
        _indent--;
        WriteLine("end)");
        WriteLine("return coroutine.yield()");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryHelpers()
    {
        WriteLine($"{_rootTable}.DictNil__ = {_rootTable}.DictNil__ or {{}}");
        WriteLine($"function {_rootTable}.DictNew__()");
        _indent++;
        WriteLine("return { data = {}, keys = {} }");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictGet__(dict, key)");
        _indent++;
        WriteLine("local value = dict.data[key]");
        WriteLine($"if value == {_rootTable}.DictNil__ then return nil end");
        WriteLine("return value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictSet__(dict, key, value)");
        _indent++;
        WriteLine("if dict.data[key] == nil then");
        _indent++;
        WriteLine("table.insert(dict.keys, key)");
        _indent--;
        WriteLine("end");
        WriteLine($"dict.data[key] = value == nil and {_rootTable}.DictNil__ or value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictRemove__(dict, key)");
        _indent++;
        WriteLine("if dict.data[key] ~= nil then");
        _indent++;
        WriteLine("dict.data[key] = nil");
        WriteLine("for i, storedKey in ipairs(dict.keys) do");
        _indent++;
        WriteLine("if storedKey == key then");
        _indent++;
        WriteLine("table.remove(dict.keys, i)");
        WriteLine("break");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        WriteLine("return false");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictIterate__(dict)");
        _indent++;
        WriteLine("local i = 0");
        WriteLine("return function()");
        _indent++;
        WriteLine("i = i + 1");
        WriteLine("local key = dict.keys[i]");
        WriteLine("if key ~= nil then");
        _indent++;
        WriteLine("local value = dict.data[key]");
        WriteLine($"if value == {_rootTable}.DictNil__ then value = nil end");
        WriteLine("return key, value");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListHelpers()
    {
        WriteLine($"function {_rootTable}.ListSort__(list, less)");
        _indent++;
        WriteLine("local compare = less or function(a, b) return a < b end");
        WriteLine("for i = 2, #list do");
        _indent++;
        WriteLine("local value = list[i]");
        WriteLine("local j = i - 1");
        WriteLine("while j >= 1 and compare(value, list[j]) do");
        _indent++;
        WriteLine("list[j + 1] = list[j]");
        WriteLine("j = j - 1");
        _indent--;
        WriteLine("end");
        WriteLine("list[j + 1] = value");
        _indent--;
        WriteLine("end");
        WriteLine("return list");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void EnsureModuleEmitted(string moduleName)
    {
        var path = _rootTable + "." + moduleName;
        if (_emittedTablePaths.Add(path))
        {
            WriteLine($"{path} = {path} or {{}}");
        }
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

        if (m.IsCoroutine)
        {
            WriteLine($"return {_rootTable}.CorRun__(function()");
            _indent++;
            EmitBlock(m.Body);
            _indent--;
            WriteLine("end)");
        }
        else
        {
            EmitBlock(m.Body);
        }

        _indent--;
        WriteLine("end");
    }

    private void EmitEntryPointCall(IRModule module)
    {
        var entry = module.Types
            .SelectMany(type => type.Methods.Select(method => new { Type = type, Method = method }))
            .FirstOrDefault(candidate => candidate.Method.IsEntryPoint);

        if (entry is null)
        {
            return;
        }

        _sb.Append('\n');
        WriteLine($"{FormatTypePath(entry.Type)}.{entry.Method.LuaName}()");
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
            case IRDictionaryForEach f:
                EmitDictionaryForEach(f);
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
        var collectionName = NewTemp("collection");
        var indexName = NewTemp("i");

        WriteLine("do");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(collectionName).Append(" = ");
        EmitExpr(f.Collection);
        _sb.Append('\n');
        WriteLine($"for {indexName}, {f.ItemName} in ipairs({collectionName}) do");
        _indent++;
        EmitBlock(f.Body);
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
    }

    private void EmitDictionaryForEach(IRDictionaryForEach f)
    {
        var dictionaryName = NewTemp("dict");

        WriteLine("do");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(dictionaryName).Append(" = ");
        EmitExpr(f.Dictionary);
        _sb.Append('\n');
        WriteLine($"for {f.KeyName}, {f.ValueName} in {_rootTable}.DictIterate__({dictionaryName}) do");
        _indent++;
        if (f.ItemName is not null)
        {
            WriteLine($"local {f.ItemName} = {{k = {f.KeyName}, v = {f.ValueName}}}");
        }
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
            case IRArrayNew:
                _sb.Append("{}");
                break;
            case IRStringConcat concat:
                _sb.Append(_rootTable).Append(".StrConcat__(");
                for (int i = 0; i < concat.Parts.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(concat.Parts[i]);
                }
                _sb.Append(')');
                break;
            case IRDictionaryNew:
                _sb.Append(_rootTable).Append(".DictNew__()");
                break;
            case IRDictionaryGet dictionaryGet:
                _sb.Append(_rootTable).Append(".DictGet__(");
                EmitExpr(dictionaryGet.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryGet.Key);
                _sb.Append(')');
                break;
            case IRDictionarySet dictionarySet:
                _sb.Append(_rootTable).Append(".DictSet__(");
                EmitExpr(dictionarySet.Table);
                _sb.Append(", ");
                EmitExpr(dictionarySet.Key);
                _sb.Append(", ");
                EmitExpr(dictionarySet.Value);
                _sb.Append(')');
                break;
            case IRDictionaryRemove dictionaryRemove:
                _sb.Append(_rootTable).Append(".DictRemove__(");
                EmitExpr(dictionaryRemove.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryRemove.Key);
                _sb.Append(')');
                break;
            case IRListSort listSort:
                _sb.Append(_rootTable).Append(".ListSort__(");
                EmitExpr(listSort.List);
                if (listSort.Comparer is not null)
                {
                    _sb.Append(", ");
                    EmitExpr(listSort.Comparer);
                }
                _sb.Append(')');
                break;
            case IRRuntimeInvocation runtimeInvocation:
                _sb.Append(_rootTable).Append('.').Append(runtimeInvocation.Name).Append('(');
                for (int i = 0; i < runtimeInvocation.Arguments.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(runtimeInvocation.Arguments[i]);
                }
                _sb.Append(')');
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
                _sb.Append(_rootTable).Append(".TypeIs__(");
                EmitExpr(isExpr.Value);
                _sb.Append(", ").Append(FormatTypeReference(isExpr.Type)).Append(')');
                break;
            case IRAs asExpr:
                _sb.Append(_rootTable).Append(".TypeAs__(");
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

    private string FormatTypePath(IRType type)
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

    private static bool UsesStringConcat(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesStringConcat(m.Body));

    private static bool UsesDictionaryHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesDictionaryHelpers(m.Body));

    private static bool UsesListHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesListHelpers(m.Body));

    private static bool UsesCoroutineHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => m.IsCoroutine || BlockUsesCoroutineHelpers(m.Body));

    private static bool BlockUsesTypeChecks(IRBlock block)
        => block.Statements.Any(StmtUsesTypeChecks);

    private static bool BlockUsesStringConcat(IRBlock block)
        => block.Statements.Any(StmtUsesStringConcat);

    private static bool BlockUsesDictionaryHelpers(IRBlock block)
        => block.Statements.Any(StmtUsesDictionaryHelpers);

    private static bool BlockUsesListHelpers(IRBlock block)
        => block.Statements.Any(StmtUsesListHelpers);

    private static bool BlockUsesCoroutineHelpers(IRBlock block)
        => block.Statements.Any(StmtUsesCoroutineHelpers);

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
            IRDictionaryForEach fe => ExprUsesTypeChecks(fe.Dictionary) || BlockUsesTypeChecks(fe.Body),
            IRTry tr => BlockUsesTypeChecks(tr.Try)
                || (tr.Catch is not null && BlockUsesTypeChecks(tr.Catch))
                || (tr.Finally is not null && BlockUsesTypeChecks(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesTypeChecks(th.Value),
            _ => false,
        };

    private static bool StmtUsesDictionaryHelpers(IRStmt stmt)
        => stmt switch
        {
            IRBlock block => BlockUsesDictionaryHelpers(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesDictionaryHelpers(local.Initializer),
            IRAssign assign => ExprUsesDictionaryHelpers(assign.Target) || ExprUsesDictionaryHelpers(assign.Value),
            IRExprStmt exprStmt => ExprUsesDictionaryHelpers(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesDictionaryHelpers),
            IRReturn ret => ret.Value is not null && ExprUsesDictionaryHelpers(ret.Value),
            IRIf iff => ExprUsesDictionaryHelpers(iff.Condition) || BlockUsesDictionaryHelpers(iff.Then) || (iff.Else is not null && BlockUsesDictionaryHelpers(iff.Else)),
            IRWhile wh => ExprUsesDictionaryHelpers(wh.Condition) || BlockUsesDictionaryHelpers(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesDictionaryHelpers(fr.Initializer))
                || (fr.Condition is not null && ExprUsesDictionaryHelpers(fr.Condition))
                || fr.Incrementors.Any(StmtUsesDictionaryHelpers)
                || BlockUsesDictionaryHelpers(fr.Body),
            IRForEach fe => ExprUsesDictionaryHelpers(fe.Collection) || BlockUsesDictionaryHelpers(fe.Body),
            IRDictionaryForEach => true,
            IRTry tr => BlockUsesDictionaryHelpers(tr.Try)
                || (tr.Catch is not null && BlockUsesDictionaryHelpers(tr.Catch))
                || (tr.Finally is not null && BlockUsesDictionaryHelpers(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesDictionaryHelpers(th.Value),
            _ => false,
        };

    private static bool StmtUsesListHelpers(IRStmt stmt)
        => stmt switch
        {
            IRBlock block => BlockUsesListHelpers(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesListHelpers(local.Initializer),
            IRAssign assign => ExprUsesListHelpers(assign.Target) || ExprUsesListHelpers(assign.Value),
            IRExprStmt exprStmt => ExprUsesListHelpers(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesListHelpers),
            IRReturn ret => ret.Value is not null && ExprUsesListHelpers(ret.Value),
            IRIf iff => ExprUsesListHelpers(iff.Condition) || BlockUsesListHelpers(iff.Then) || (iff.Else is not null && BlockUsesListHelpers(iff.Else)),
            IRWhile wh => ExprUsesListHelpers(wh.Condition) || BlockUsesListHelpers(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesListHelpers(fr.Initializer))
                || (fr.Condition is not null && ExprUsesListHelpers(fr.Condition))
                || fr.Incrementors.Any(StmtUsesListHelpers)
                || BlockUsesListHelpers(fr.Body),
            IRForEach fe => ExprUsesListHelpers(fe.Collection) || BlockUsesListHelpers(fe.Body),
            IRDictionaryForEach fe => ExprUsesListHelpers(fe.Dictionary) || BlockUsesListHelpers(fe.Body),
            IRTry tr => BlockUsesListHelpers(tr.Try)
                || (tr.Catch is not null && BlockUsesListHelpers(tr.Catch))
                || (tr.Finally is not null && BlockUsesListHelpers(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesListHelpers(th.Value),
            _ => false,
        };

    private static bool StmtUsesCoroutineHelpers(IRStmt stmt)
        => stmt switch
        {
            IRBlock block => BlockUsesCoroutineHelpers(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesCoroutineHelpers(local.Initializer),
            IRAssign assign => ExprUsesCoroutineHelpers(assign.Target) || ExprUsesCoroutineHelpers(assign.Value),
            IRExprStmt exprStmt => ExprUsesCoroutineHelpers(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesCoroutineHelpers),
            IRReturn ret => ret.Value is not null && ExprUsesCoroutineHelpers(ret.Value),
            IRIf iff => ExprUsesCoroutineHelpers(iff.Condition) || BlockUsesCoroutineHelpers(iff.Then) || (iff.Else is not null && BlockUsesCoroutineHelpers(iff.Else)),
            IRWhile wh => ExprUsesCoroutineHelpers(wh.Condition) || BlockUsesCoroutineHelpers(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesCoroutineHelpers(fr.Initializer))
                || (fr.Condition is not null && ExprUsesCoroutineHelpers(fr.Condition))
                || fr.Incrementors.Any(StmtUsesCoroutineHelpers)
                || BlockUsesCoroutineHelpers(fr.Body),
            IRForEach fe => ExprUsesCoroutineHelpers(fe.Collection) || BlockUsesCoroutineHelpers(fe.Body),
            IRDictionaryForEach fe => ExprUsesCoroutineHelpers(fe.Dictionary) || BlockUsesCoroutineHelpers(fe.Body),
            IRTry tr => BlockUsesCoroutineHelpers(tr.Try)
                || (tr.Catch is not null && BlockUsesCoroutineHelpers(tr.Catch))
                || (tr.Finally is not null && BlockUsesCoroutineHelpers(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesCoroutineHelpers(th.Value),
            _ => false,
        };

    private static bool StmtUsesStringConcat(IRStmt stmt)
        => stmt switch
        {
            IRBlock block => BlockUsesStringConcat(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesStringConcat(local.Initializer),
            IRAssign assign => ExprUsesStringConcat(assign.Target) || ExprUsesStringConcat(assign.Value),
            IRExprStmt exprStmt => ExprUsesStringConcat(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesStringConcat),
            IRReturn ret => ret.Value is not null && ExprUsesStringConcat(ret.Value),
            IRIf iff => ExprUsesStringConcat(iff.Condition) || BlockUsesStringConcat(iff.Then) || (iff.Else is not null && BlockUsesStringConcat(iff.Else)),
            IRWhile wh => ExprUsesStringConcat(wh.Condition) || BlockUsesStringConcat(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesStringConcat(fr.Initializer))
                || (fr.Condition is not null && ExprUsesStringConcat(fr.Condition))
                || fr.Incrementors.Any(StmtUsesStringConcat)
                || BlockUsesStringConcat(fr.Body),
            IRForEach fe => ExprUsesStringConcat(fe.Collection) || BlockUsesStringConcat(fe.Body),
            IRDictionaryForEach fe => ExprUsesStringConcat(fe.Dictionary) || BlockUsesStringConcat(fe.Body),
            IRTry tr => BlockUsesStringConcat(tr.Try)
                || (tr.Catch is not null && BlockUsesStringConcat(tr.Catch))
                || (tr.Finally is not null && BlockUsesStringConcat(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesStringConcat(th.Value),
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
            IRArrayNew arrayNew => ExprUsesTypeChecks(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesTypeChecks),
            IRDictionaryNew => false,
            IRDictionaryGet dictionaryGet => ExprUsesTypeChecks(dictionaryGet.Table) || ExprUsesTypeChecks(dictionaryGet.Key),
            IRDictionarySet dictionarySet => ExprUsesTypeChecks(dictionarySet.Table) || ExprUsesTypeChecks(dictionarySet.Key) || ExprUsesTypeChecks(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesTypeChecks(dictionaryRemove.Table) || ExprUsesTypeChecks(dictionaryRemove.Key),
            IRListSort listSort => ExprUsesTypeChecks(listSort.List) || (listSort.Comparer is not null && ExprUsesTypeChecks(listSort.Comparer)),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesTypeChecks),
            IRBinary binary => ExprUsesTypeChecks(binary.Left) || ExprUsesTypeChecks(binary.Right),
            IRUnary unary => ExprUsesTypeChecks(unary.Operand),
            _ => false,
        };

    private static bool ExprUsesStringConcat(IRExpr expr)
        => expr switch
        {
            IRStringConcat => true,
            IRDictionaryNew => false,
            IRDictionaryGet dictionaryGet => ExprUsesStringConcat(dictionaryGet.Table) || ExprUsesStringConcat(dictionaryGet.Key),
            IRDictionarySet dictionarySet => ExprUsesStringConcat(dictionarySet.Table) || ExprUsesStringConcat(dictionarySet.Key) || ExprUsesStringConcat(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesStringConcat(dictionaryRemove.Table) || ExprUsesStringConcat(dictionaryRemove.Key),
            IRListSort listSort => ExprUsesStringConcat(listSort.List) || (listSort.Comparer is not null && ExprUsesStringConcat(listSort.Comparer)),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesStringConcat),
            IRMemberAccess member => ExprUsesStringConcat(member.Target),
            IRElementAccess element => ExprUsesStringConcat(element.Target) || ExprUsesStringConcat(element.Index),
            IRLength length => ExprUsesStringConcat(length.Target),
            IRInvocation invocation => ExprUsesStringConcat(invocation.Callee) || invocation.Arguments.Any(ExprUsesStringConcat),
            IRArrayLiteral array => array.Items.Any(ExprUsesStringConcat),
            IRArrayNew arrayNew => ExprUsesStringConcat(arrayNew.Size),
            IRBinary binary => ExprUsesStringConcat(binary.Left) || ExprUsesStringConcat(binary.Right),
            IRUnary unary => ExprUsesStringConcat(unary.Operand),
            IRIs isExpr => ExprUsesStringConcat(isExpr.Value),
            IRAs asExpr => ExprUsesStringConcat(asExpr.Value),
            _ => false,
        };

    private static bool ExprUsesDictionaryHelpers(IRExpr expr)
        => expr switch
        {
            IRDictionaryGet or IRDictionarySet or IRDictionaryRemove => true,
            IRDictionaryNew => true,
            IRListSort listSort => ExprUsesDictionaryHelpers(listSort.List) || (listSort.Comparer is not null && ExprUsesDictionaryHelpers(listSort.Comparer)),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesDictionaryHelpers),
            IRMemberAccess member => ExprUsesDictionaryHelpers(member.Target),
            IRElementAccess element => ExprUsesDictionaryHelpers(element.Target) || ExprUsesDictionaryHelpers(element.Index),
            IRLength length => ExprUsesDictionaryHelpers(length.Target),
            IRInvocation invocation => ExprUsesDictionaryHelpers(invocation.Callee) || invocation.Arguments.Any(ExprUsesDictionaryHelpers),
            IRArrayLiteral array => array.Items.Any(ExprUsesDictionaryHelpers),
            IRArrayNew arrayNew => ExprUsesDictionaryHelpers(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesDictionaryHelpers),
            IRBinary binary => ExprUsesDictionaryHelpers(binary.Left) || ExprUsesDictionaryHelpers(binary.Right),
            IRUnary unary => ExprUsesDictionaryHelpers(unary.Operand),
            IRIs isExpr => ExprUsesDictionaryHelpers(isExpr.Value),
            IRAs asExpr => ExprUsesDictionaryHelpers(asExpr.Value),
            _ => false,
        };

    private static bool ExprUsesListHelpers(IRExpr expr)
        => expr switch
        {
            IRListSort => true,
            IRMemberAccess member => ExprUsesListHelpers(member.Target),
            IRElementAccess element => ExprUsesListHelpers(element.Target) || ExprUsesListHelpers(element.Index),
            IRLength length => ExprUsesListHelpers(length.Target),
            IRInvocation invocation => ExprUsesListHelpers(invocation.Callee) || invocation.Arguments.Any(ExprUsesListHelpers),
            IRArrayLiteral array => array.Items.Any(ExprUsesListHelpers),
            IRArrayNew arrayNew => ExprUsesListHelpers(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesListHelpers),
            IRDictionaryGet dictionaryGet => ExprUsesListHelpers(dictionaryGet.Table) || ExprUsesListHelpers(dictionaryGet.Key),
            IRDictionarySet dictionarySet => ExprUsesListHelpers(dictionarySet.Table) || ExprUsesListHelpers(dictionarySet.Key) || ExprUsesListHelpers(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesListHelpers(dictionaryRemove.Table) || ExprUsesListHelpers(dictionaryRemove.Key),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesListHelpers),
            IRBinary binary => ExprUsesListHelpers(binary.Left) || ExprUsesListHelpers(binary.Right),
            IRUnary unary => ExprUsesListHelpers(unary.Operand),
            IRIs isExpr => ExprUsesListHelpers(isExpr.Value),
            IRAs asExpr => ExprUsesListHelpers(asExpr.Value),
            _ => false,
        };

    private static bool ExprUsesCoroutineHelpers(IRExpr expr)
        => expr switch
        {
            IRRuntimeInvocation { Name: "CorWait__" } => true,
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesCoroutineHelpers),
            IRMemberAccess member => ExprUsesCoroutineHelpers(member.Target),
            IRElementAccess element => ExprUsesCoroutineHelpers(element.Target) || ExprUsesCoroutineHelpers(element.Index),
            IRLength length => ExprUsesCoroutineHelpers(length.Target),
            IRInvocation invocation => ExprUsesCoroutineHelpers(invocation.Callee) || invocation.Arguments.Any(ExprUsesCoroutineHelpers),
            IRArrayLiteral array => array.Items.Any(ExprUsesCoroutineHelpers),
            IRArrayNew arrayNew => ExprUsesCoroutineHelpers(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesCoroutineHelpers),
            IRDictionaryGet dictionaryGet => ExprUsesCoroutineHelpers(dictionaryGet.Table) || ExprUsesCoroutineHelpers(dictionaryGet.Key),
            IRDictionarySet dictionarySet => ExprUsesCoroutineHelpers(dictionarySet.Table) || ExprUsesCoroutineHelpers(dictionarySet.Key) || ExprUsesCoroutineHelpers(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesCoroutineHelpers(dictionaryRemove.Table) || ExprUsesCoroutineHelpers(dictionaryRemove.Key),
            IRListSort listSort => ExprUsesCoroutineHelpers(listSort.List) || (listSort.Comparer is not null && ExprUsesCoroutineHelpers(listSort.Comparer)),
            IRBinary binary => ExprUsesCoroutineHelpers(binary.Left) || ExprUsesCoroutineHelpers(binary.Right),
            IRUnary unary => ExprUsesCoroutineHelpers(unary.Operand),
            IRIs isExpr => ExprUsesCoroutineHelpers(isExpr.Value),
            IRAs asExpr => ExprUsesCoroutineHelpers(asExpr.Value),
            _ => false,
        };

    private void CollectIdentifiers(IRModule module)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            foreach (var parameter in method.Parameters)
            {
                _usedIdentifiers.Add(parameter);
            }
            CollectIdentifiers(method.Body);
        }
    }

    private void CollectIdentifiers(IRBlock block)
    {
        foreach (var stmt in block.Statements)
        {
            CollectIdentifiers(stmt);
        }
    }

    private void CollectIdentifiers(IRStmt stmt)
    {
        switch (stmt)
        {
            case IRBlock block:
                CollectIdentifiers(block);
                break;
            case IRLocalDecl local:
                _usedIdentifiers.Add(local.Name);
                if (local.Initializer is not null) CollectIdentifiers(local.Initializer);
                break;
            case IRAssign assign:
                CollectIdentifiers(assign.Target);
                CollectIdentifiers(assign.Value);
                break;
            case IRExprStmt exprStmt:
                CollectIdentifiers(exprStmt.Expression);
                break;
            case IRBaseConstructorCall baseCall:
                foreach (var argument in baseCall.Arguments) CollectIdentifiers(argument);
                break;
            case IRReturn ret when ret.Value is not null:
                CollectIdentifiers(ret.Value);
                break;
            case IRIf iff:
                CollectIdentifiers(iff.Condition);
                CollectIdentifiers(iff.Then);
                if (iff.Else is not null) CollectIdentifiers(iff.Else);
                break;
            case IRWhile wh:
                CollectIdentifiers(wh.Condition);
                CollectIdentifiers(wh.Body);
                break;
            case IRFor fr:
                if (fr.Initializer is not null) CollectIdentifiers(fr.Initializer);
                if (fr.Condition is not null) CollectIdentifiers(fr.Condition);
                foreach (var incrementor in fr.Incrementors) CollectIdentifiers(incrementor);
                CollectIdentifiers(fr.Body);
                break;
            case IRForEach fe:
                _usedIdentifiers.Add(fe.ItemName);
                CollectIdentifiers(fe.Collection);
                CollectIdentifiers(fe.Body);
                break;
            case IRDictionaryForEach fe:
                if (fe.ItemName is not null) _usedIdentifiers.Add(fe.ItemName);
                _usedIdentifiers.Add(fe.KeyName);
                _usedIdentifiers.Add(fe.ValueName);
                CollectIdentifiers(fe.Dictionary);
                CollectIdentifiers(fe.Body);
                break;
            case IRTry tr:
                CollectIdentifiers(tr.Try);
                if (!string.IsNullOrEmpty(tr.CatchVariable)) _usedIdentifiers.Add(tr.CatchVariable);
                if (tr.Catch is not null) CollectIdentifiers(tr.Catch);
                if (tr.Finally is not null) CollectIdentifiers(tr.Finally);
                break;
            case IRThrow th when th.Value is not null:
                CollectIdentifiers(th.Value);
                break;
        }
    }

    private void CollectIdentifiers(IRExpr expr)
    {
        switch (expr)
        {
            case IRIdentifier id:
                _usedIdentifiers.Add(id.Name);
                break;
            case IRMemberAccess member:
                CollectIdentifiers(member.Target);
                break;
            case IRElementAccess element:
                CollectIdentifiers(element.Target);
                CollectIdentifiers(element.Index);
                break;
            case IRLength length:
                CollectIdentifiers(length.Target);
                break;
            case IRInvocation invocation:
                CollectIdentifiers(invocation.Callee);
                foreach (var argument in invocation.Arguments) CollectIdentifiers(argument);
                break;
            case IRArrayLiteral array:
                foreach (var item in array.Items) CollectIdentifiers(item);
                break;
            case IRArrayNew arrayNew:
                CollectIdentifiers(arrayNew.Size);
                break;
            case IRStringConcat concat:
                foreach (var part in concat.Parts) CollectIdentifiers(part);
                break;
            case IRDictionaryGet dictionaryGet:
                CollectIdentifiers(dictionaryGet.Table);
                CollectIdentifiers(dictionaryGet.Key);
                break;
            case IRDictionarySet dictionarySet:
                CollectIdentifiers(dictionarySet.Table);
                CollectIdentifiers(dictionarySet.Key);
                CollectIdentifiers(dictionarySet.Value);
                break;
            case IRDictionaryRemove dictionaryRemove:
                CollectIdentifiers(dictionaryRemove.Table);
                CollectIdentifiers(dictionaryRemove.Key);
                break;
            case IRListSort listSort:
                CollectIdentifiers(listSort.List);
                if (listSort.Comparer is not null) CollectIdentifiers(listSort.Comparer);
                break;
            case IRRuntimeInvocation runtimeInvocation:
                foreach (var argument in runtimeInvocation.Arguments) CollectIdentifiers(argument);
                break;
            case IRBinary binary:
                CollectIdentifiers(binary.Left);
                CollectIdentifiers(binary.Right);
                break;
            case IRUnary unary:
                CollectIdentifiers(unary.Operand);
                break;
            case IRIs isExpr:
                CollectIdentifiers(isExpr.Value);
                break;
            case IRAs asExpr:
                CollectIdentifiers(asExpr.Value);
                break;
        }
    }

    private string NewTemp(string reasonableName)
    {
        var baseName = reasonableName;
        if (_usedIdentifiers.Add(baseName))
        {
            return baseName;
        }

        string candidate;
        do
        {
            candidate = baseName + (++_tempId).ToString(CultureInfo.InvariantCulture);
        }
        while (!_usedIdentifiers.Add(candidate));
        return candidate;
    }

    private void WriteIndent() => _sb.Append(' ', _indent * 4);
    private void WriteLine(string s) { WriteIndent(); _sb.Append(s).Append('\n'); }
}
