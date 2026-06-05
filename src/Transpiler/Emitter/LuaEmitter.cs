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
    private static readonly HashSet<string> LuaReservedIdentifiers = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while", "table",
    };

    private readonly string _rootTable;
    private readonly StringBuilder _sb = new();
    private readonly HashSet<string> _emittedTablePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedIdentifiers = new(StringComparer.Ordinal);
    private int _indent;
    private int _tempId;
    private bool _emitTypeHelpers;
    private bool _emitStringConcatHelper;
    private bool _emitCoroutineHelpers;
    private bool _emitStrSplitHelper;

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
        _emitCoroutineHelpers = UsesCoroutineHelpers(module);
        _emitStrSplitHelper = UsesStrSplit(module);
        _usedIdentifiers.Clear();
        CollectIdentifiers(module);

        EnsureRootEmitted();

        // Emit all LuaRequires at the top — before any type uses them (e.g., class() calls).
        var emittedRequires = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in module.Types)
        {
            foreach (var moduleName in type.LuaRequires)
            {
                if (emittedRequires.Add(moduleName))
                {
                    WriteLine($"require(\"{EscapeLuaString(moduleName)}\")");
                }
            }
        }

        // Pre-compute which type paths will use class() — skip {} emission for these.
        var luaClassPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in module.Types)
        {
            if (t.LuaClass is not null)
            {
                var tp = _rootTable;
                foreach (var seg in t.NamespaceSegments) tp += "." + seg;
                tp += "." + t.Name;
                luaClassPaths.Add(tp);
            }
        }

        foreach (var enumType in module.Enums)
        {
            EmitEnum(enumType);
        }

        for (int i = 0; i < module.Types.Count; i++)
        {
            EmitType(module.Types[i], luaClassPaths);
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
            if (_emitStrSplitHelper)
            {
                WriteStrSplitHelper();
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
        WriteLine("if not ok then print(tostring(err)) end");
        _indent--;
        WriteLine("end)");
        WriteLine("return coroutine.yield()");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteStrSplitHelper()
    {
        WriteLine($"function {_rootTable}.StrSplit__(str, sep)");
        _indent++;
        WriteLine("local result = {}");
        WriteLine("if str == nil or str == \"\" then return result end");
        WriteLine("if sep == nil or sep == \"\" then");
        _indent++;
        WriteLine("for i = 1, #str do");
        _indent++;
        WriteLine("result[i] = str:sub(i, i)");
        _indent--;
        WriteLine("end");
        WriteLine("return result");
        _indent--;
        WriteLine("end");
        // Use string.find with plain=true for literal separator matching.
        // Handles multi-char separators, special chars, consecutive separators, and trailing separators.
        WriteLine("local pos = 1");
        WriteLine("while true do");
        _indent++;
        WriteLine("local start, finish = string.find(str, sep, pos, true)");
        WriteLine("if start == nil then");
        _indent++;
        WriteLine("table.insert(result, string.sub(str, pos))");
        WriteLine("break");
        _indent--;
        WriteLine("end");
        WriteLine("table.insert(result, string.sub(str, pos, start - 1))");
        WriteLine("pos = finish + 1");
        _indent--;
        WriteLine("end");
        WriteLine("return result");
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

    private void EmitEnum(IREnum enumType)
    {
        EmitComments(enumType.Comments);

        var path = _rootTable;
        foreach (var seg in enumType.NamespaceSegments)
        {
            path = path + "." + seg;
            if (_emittedTablePaths.Add(path))
            {
                WriteLine($"{path} = {path} or {{}}");
            }
        }

        var enumPath = path + "." + enumType.Name;
        WriteLine($"-- {enumType.FullName}");
        WriteLine($"{enumPath} = {enumPath} or {{}}");
        foreach (var member in enumType.Members)
        {
            EmitComments(member.Comments);
            WriteLine($"{enumPath}.{member.Name} = {FormatLiteral(member.Value)}");
        }
        _sb.Append('\n');
    }

    private void EmitType(IRType type, HashSet<string> luaClassPaths)
    {
        if (type.IsTableLiteral)
        {
            return;
        }

        var methods = type.Methods.Where(m => !m.IsStaticConstructor && (!type.IsStruct || !m.IsConstructor)).ToArray();
        if (type.IsStruct
            && type.LuaRequires.Count == 0
            && type.Fields.All(f => !f.IsStatic)
            && type.Methods.All(m => !m.IsStaticConstructor)
            && methods.Length == 0)
        {
            return;
        }

        EmitComments(type.Comments);

        // Walk namespace segments and emit each level once.
        // Skip paths that correspond to types with LuaClass — they'll be declared with class() later.
        var path = _rootTable;
        foreach (var seg in type.NamespaceSegments)
        {
            path = path + "." + seg;
            if (_emittedTablePaths.Add(path) && !luaClassPaths.Contains(path))
            {
                WriteLine($"{path} = {path} or {{}}");
            }
        }

        // Type table.
        var typePath = path + "." + type.Name;
        WriteLine($"-- {type.FullName}");
        if (type.LuaClass is { } luaClass)
        {
            EmitLuaClassDeclaration(typePath, luaClass);
        }
        else
        {
            WriteLine($"{typePath} = {typePath} or {{}}");
        }
        WriteLine($"{typePath}.Name = {FormatLiteral(new IRLiteral(type.Name, IRLiteralKind.String))}");
        WriteLine($"{typePath}.FullName = {FormatLiteral(new IRLiteral(type.FullName, IRLiteralKind.String))}");
        if (type.BaseType is { } baseType && type.LuaClass is null)
        {
            WriteLine($"setmetatable({typePath}, {{ __index = {FormatTypeReference(baseType)} }})");
            WriteLine($"{typePath}.__sf_base = {FormatTypeReference(baseType)}");
        }
        else if (type.LuaClass?.BaseType is { } luaClassBaseType)
        {
            WriteIndent();
            _sb.Append(typePath).Append(".__sf_base = ");
            EmitExpr(luaClassBaseType);
            _sb.Append('\n');
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

        for (int i = 0; i < methods.Length; i++)
        {
            EmitMethod(type, typePath, methods[i], type.Fields.Where(f => !f.IsStatic));
            if (i < methods.Length - 1)
            {
                _sb.Append('\n');
            }
        }

        if (methods.Length > 0 && (type.Fields.Any(f => f.IsStatic) || type.Methods.Any(m => m.IsStaticConstructor)))
        {
            _sb.Append('\n');
        }

        // Static initialization runs after methods are registered so type-local
        // initializers and static constructors can safely call helpers like .New().
        foreach (var f in type.Fields.Where(f => f.IsStatic))
        {
            EmitComments(f.Comments);
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
    }

    private void EmitLuaClassDeclaration(string typePath, IRLuaClass luaClass)
    {
        foreach (var binding in luaClass.ModuleBindings)
        {
            WriteLine($"local {binding.LocalName} = require(\"{EscapeLuaString(binding.ModuleName)}\")");
        }

        WriteIndent();
        _sb.Append(typePath).Append(" = ").Append(typePath).Append(" or class(")
            .Append(FormatLiteral(new IRLiteral(luaClass.ClassName, IRLiteralKind.String)));
        if (luaClass.BaseType is not null)
        {
            _sb.Append(", ");
            EmitExpr(luaClass.BaseType);
        }
        _sb.Append(")\n");
    }

    private void EmitMethod(IRType type, string typePath, IRFunction m, IEnumerable<IRField> instanceFields)
    {
        EmitComments(m.Comments);

        if (m.IsConstructor)
        {
            EmitConstructor(type, typePath, m, instanceFields);
            return;
        }

        var sep = m.IsInstance ? ":" : ".";
        var paramList = string.Join(", ", m.Parameters);
        WriteLine($"function {typePath}{sep}{m.LuaName}({paramList})");
        _indent++;
        EmitParameterDefaults(m);

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

    private void EmitConstructor(IRType type, string typePath, IRFunction m, IEnumerable<IRField> instanceFields)
    {
        var paramList = string.Join(", ", m.Parameters);
        var initName = m.InitLuaName ?? "__Init";

        WriteLine($"function {typePath}.{initName}(self{(paramList.Length == 0 ? string.Empty : ", " + paramList)})");
        _indent++;
        EmitParameterDefaults(m);
        if (m.ThisConstructorCall is not null)
        {
            EmitStmt(m.ThisConstructorCall);
        }
        else
        {
            if (m.BaseConstructorCall is { SkipEmit: false })
            {
                EmitStmt(m.BaseConstructorCall);
            }
            WriteLine($"self.__sf_type = {typePath}");
            foreach (var field in instanceFields)
            {
                EmitComments(field.Comments);
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
        }
        EmitBlock(m.Body);
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {typePath}.{m.LuaName}({paramList})");
        _indent++;
        if (type.LuaClass is null)
        {
            WriteLine($"local self = setmetatable({{}}, {{ __index = {typePath} }})");
        }
        else if (m.BaseConstructorCall is { Arguments.Count: > 0 } baseCall)
        {
            WriteIndent();
            _sb.Append("local self = ").Append(typePath).Append(".new(");
            for (var i = 0; i < baseCall.Arguments.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                EmitExpr(baseCall.Arguments[i]);
            }

            _sb.Append(")\n");
        }
        else
        {
            WriteLine($"local self = {typePath}.new()");
        }
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

    private void EmitParameterDefaults(IRFunction function)
    {
        foreach (var parameterDefault in function.ParameterDefaults)
        {
            WriteIndent();
            _sb.Append("if ").Append(parameterDefault.ParameterName).Append(" == nil then ")
                .Append(parameterDefault.ParameterName).Append(" = ");
            EmitExpr(parameterDefault.Value);
            _sb.Append(" end\n");
        }
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
            case IRStatementList list:
                foreach (var child in list.Statements)
                {
                    EmitStmt(child);
                }
                break;
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
            case IRMultiLocalDecl ld:
                WriteIndent();
                _sb.Append("local ").Append(string.Join(", ", ld.Names));
                if (ld.Initializers.Count > 0)
                {
                    _sb.Append(" = ");
                    for (int i = 0; i < ld.Initializers.Count; i++)
                    {
                        if (i > 0)
                        {
                            _sb.Append(", ");
                        }
                        EmitExpr(ld.Initializers[i]);
                    }
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
            case IRMultiAssign a:
                WriteIndent();
                for (int i = 0; i < a.Targets.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(a.Targets[i]);
                }
                _sb.Append(" = ");
                for (int i = 0; i < a.Values.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(a.Values[i]);
                }
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
            case IRThisConstructorCall thisCall:
                WriteIndent();
                _sb.Append(FormatTypeReference(thisCall.Type)).Append('.').Append(thisCall.InitLuaName).Append("(self");
                foreach (var argument in thisCall.Arguments)
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
            case IRMultiReturn r:
                WriteIndent();
                _sb.Append("return ");
                for (int i = 0; i < r.Values.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(r.Values[i]);
                }
                _sb.Append('\n');
                break;
            case IRIf i:
                EmitIf(i);
                break;
            case IRSwitch s:
                EmitSwitch(s);
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
                EmitComment(c.Text);
                break;
        }
    }

    private void EmitComments(IEnumerable<string> comments)
    {
        foreach (var comment in comments)
        {
            EmitComment(comment);
        }
    }

    private void EmitComment(string comment)
    {
        foreach (var line in comment.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            WriteIndent();
            _sb.Append("--");
            if (line.Length > 0)
            {
                _sb.Append(' ').Append(line);
            }
            _sb.Append('\n');
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

    private void EmitSwitch(IRSwitch switchStmt)
    {
        var valueName = NewTemp("switchValue");
        var defaultSection = switchStmt.Sections.FirstOrDefault(section => section.IsDefault);
        var caseSections = switchStmt.Sections.Where(section => !section.IsDefault && section.Labels.Count > 0).ToArray();

        WriteLine("repeat");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(valueName).Append(" = ");
        EmitExpr(switchStmt.Value);
        _sb.Append('\n');

        if (caseSections.Length > 0 || defaultSection is not null)
        {
            var emittedBranch = false;
            foreach (var section in caseSections)
            {
                WriteIndent();
                _sb.Append(emittedBranch ? "elseif " : "if ");
                EmitSwitchCondition(valueName, section.Labels);
                _sb.Append(" then\n");
                _indent++;
                EmitBlock(section.Body);
                _indent--;
                emittedBranch = true;
            }

            if (defaultSection is not null)
            {
                WriteLine(emittedBranch ? "else" : "if true then");
                _indent++;
                EmitBlock(defaultSection.Body);
                _indent--;
                emittedBranch = true;
            }

            if (emittedBranch)
            {
                WriteLine("end");
            }
        }

        _indent--;
        WriteLine("until true");
    }

    private void EmitSwitchCondition(string valueName, IReadOnlyList<IRExpr> labels)
    {
        for (int i = 0; i < labels.Count; i++)
        {
            if (i > 0)
            {
                _sb.Append(" or ");
            }
            _sb.Append('(').Append(valueName).Append(" == ");
            EmitExpr(labels[i]);
            _sb.Append(')');
        }
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

        WriteLine("do");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(collectionName).Append(" = ");
        EmitExpr(f.Collection);
        _sb.Append('\n');

        string indexVar;
        string iterator;
        if (f.Iterator is not null)
        {
            indexVar = "_";
            var start = _sb.Length;
            EmitExpr(f.Iterator);
            var expr = _sb.ToString(start, _sb.Length - start);
            _sb.Length = start;
            iterator = $"({expr})({collectionName})";
        }
        else
        {
            indexVar = NewTemp("i");
            iterator = $"ipairs({collectionName})";
        }

        WriteLine($"for {indexVar}, {f.ItemName} in {iterator} do");
        _indent++;
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
        WriteLine("local __sf_handled = false");

        if (t.Catches.Count > 0)
        {
            WriteLine("if not __sf_ok then");
            _indent++;
            WriteLine("local __sf_err_msg = tostring(__sf_err)");
            for (var i = 0; i < t.Catches.Count; i++)
            {
                var catchClause = t.Catches[i];
                WriteLine($"{(i == 0 ? "if" : "elseif")} {FormatCatchCondition(catchClause)} then");
                _indent++;
                WriteLine("__sf_handled = true");
                if (!string.IsNullOrEmpty(catchClause.Variable))
                {
                    WriteLine($"local {catchClause.Variable} = __sf_err_msg");
                }
                EmitBlock(catchClause.Body);
                _indent--;
            }
            WriteLine("end");
            _indent--;
            WriteLine("end");
        }

        if (t.Finally is not null)
        {
            EmitBlock(t.Finally);
        }

        WriteLine("if not __sf_ok and not __sf_handled then error(__sf_err) end");

        _indent--;
        WriteLine("end");
    }

    private static string FormatCatchCondition(IRCatch catchClause)
    {
        if (catchClause.CatchesAny)
        {
            return "true";
        }

        if (catchClause.CatchesAnySharpForgeException)
        {
            return "string.sub(__sf_err_msg, 1, 4) == \"SF__\"";
        }

        return string.Join(" or ", catchClause.ExceptionHeaders.Select(header =>
            $"string.sub(__sf_err_msg, 1, 13) == {FormatLiteral(new IRLiteral(header, IRLiteralKind.String))}"));
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
                    if (inv.Callee is IRFunctionExpression)
                    {
                        _sb.Append('(');
                        EmitExpr(inv.Callee);
                        _sb.Append(')');
                    }
                    else
                    {
                        EmitExpr(inv.Callee);
                    }
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
            case IRFunctionExpression functionExpression:
                _sb.Append("function(").Append(string.Join(", ", functionExpression.Parameters)).Append(")\n");
                _indent++;
                EmitBlock(functionExpression.Body);
                _indent--;
                WriteIndent();
                _sb.Append("end");
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
            case IRTableLiteralNew tableLiteralNew:
                _sb.Append('{');
                for (int i = 0; i < tableLiteralNew.Fields.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    var (key, value) = tableLiteralNew.Fields[i];
                    _sb.Append(key).Append(" = ");
                    EmitExpr(value);
                }
                _sb.Append('}');
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
            case IRLuaRequire luaRequire:
                _sb.Append("require(");
                EmitExpr(luaRequire.ModuleName);
                _sb.Append(')');
                break;
            case IRLuaTable:
                _sb.Append("{}");
                break;
            case IRLuaGlobal luaGlobal:
                EmitLuaGlobal(luaGlobal.Name);
                break;
            case IRLuaAccess luaAccess:
                EmitLuaAccess(luaAccess.Target, luaAccess.Name);
                break;
            case IRLuaMethodInvocation luaMethodInvocation:
                EmitLuaMethodInvocation(luaMethodInvocation);
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
            case IRTernary ternary:
                _sb.Append("(function() if ");
                EmitExpr(ternary.Condition);
                _sb.Append(" then return ");
                EmitExpr(ternary.WhenTrue);
                _sb.Append(" else return ");
                EmitExpr(ternary.WhenFalse);
                _sb.Append(" end end)()");
                break;
            case IRCoalesceAssignment coalesceAssignment:
                _sb.Append("(function()\n");
                _indent++;
                WriteIndent();
                _sb.Append("if ");
                EmitExpr(coalesceAssignment.Target);
                _sb.Append(" ~= nil then\n");
                _indent++;
                WriteIndent();
                _sb.Append("return ");
                EmitExpr(coalesceAssignment.Target);
                _sb.Append('\n');
                _indent--;
                WriteLine("end");
                WriteIndent();
                EmitExpr(coalesceAssignment.Target);
                _sb.Append(" = ");
                EmitExpr(coalesceAssignment.Value);
                _sb.Append('\n');
                WriteIndent();
                _sb.Append("return ");
                EmitExpr(coalesceAssignment.Target);
                _sb.Append('\n');
                _indent--;
                WriteIndent();
                _sb.Append("end)()");
                break;
            case IRIs isExpr:
                _sb.Append(_rootTable).Append(".TypeIs__(");
                EmitExpr(isExpr.Value);
                _sb.Append(", ");
                EmitExpr(isExpr.Type);
                _sb.Append(')');
                break;
            case IRAs asExpr:
                _sb.Append(_rootTable).Append(".TypeAs__(");
                EmitExpr(asExpr.Value);
                _sb.Append(", ");
                EmitExpr(asExpr.Type);
                _sb.Append(')');
                break;
        }
    }

    private static string FormatLiteral(IRLiteral l) => l.Kind switch
    {
        IRLiteralKind.Nil => "nil",
        IRLiteralKind.Boolean => (bool)l.Value! ? "true" : "false",
        IRLiteralKind.Integer => Convert.ToInt64(l.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        IRLiteralKind.Real => l.Value is float f
            ? f.ToString("R", CultureInfo.InvariantCulture)
            : ((double)l.Value!).ToString("R", CultureInfo.InvariantCulture),
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

    private void EmitLuaGlobal(IRExpr name)
    {
        if (TryGetLuaIdentifierName(name, out var identifier))
        {
            _sb.Append(identifier);
            return;
        }

        _sb.Append("_G[");
        EmitExpr(name);
        _sb.Append(']');
    }

    private void EmitLuaAccess(IRExpr target, IRExpr name)
    {
        EmitExpr(target);
        if (TryGetLuaIdentifierName(name, out var identifier))
        {
            _sb.Append('.').Append(identifier);
            return;
        }

        _sb.Append('[');
        EmitExpr(name);
        _sb.Append(']');
    }

    private void EmitLuaMethodInvocation(IRLuaMethodInvocation invocation)
    {
        if (TryGetLuaIdentifierName(invocation.Name, out var identifier))
        {
            EmitExpr(invocation.Target);
            _sb.Append(':').Append(identifier).Append('(');
            EmitArguments(invocation.Arguments);
            _sb.Append(')');
            return;
        }

        EmitLuaAccess(invocation.Target, invocation.Name);
        _sb.Append('(');
        EmitExpr(invocation.Target);
        if (invocation.Arguments.Count > 0)
        {
            _sb.Append(", ");
            EmitArguments(invocation.Arguments);
        }
        _sb.Append(')');
    }

    private void EmitArguments(IReadOnlyList<IRExpr> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                _sb.Append(", ");
            }
            EmitExpr(arguments[i]);
        }
    }

    private static bool TryGetLuaIdentifierName(IRExpr expr, out string identifier)
    {
        if (expr is IRLiteral { Kind: IRLiteralKind.String, Value: string value } && IsLuaIdentifier(value))
        {
            identifier = value;
            return true;
        }

        identifier = string.Empty;
        return false;
    }

    private static bool IsLuaIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (int i = 1; i < value.Length; i++)
        {
            if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_'))
            {
                return false;
            }
        }

        return !LuaReservedIdentifiers.Contains(value);
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

    private static bool UsesCoroutineHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => m.IsCoroutine || BlockUsesCoroutineHelpers(m.Body));

    private static bool UsesStrSplit(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesStrSplit(m.Body));

    private static bool BlockUsesStrSplit(IRBlock block)
        => block.Statements.Any(StmtUsesStrSplit);

    private static bool StmtUsesStrSplit(IRStmt stmt)
        => stmt switch
        {
            IRStatementList list => list.Statements.Any(StmtUsesStrSplit),
            IRBlock block => BlockUsesStrSplit(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesStrSplit(local.Initializer),
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesStrSplit),
            IRAssign assign => ExprUsesStrSplit(assign.Target) || ExprUsesStrSplit(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesStrSplit) || assign.Values.Any(ExprUsesStrSplit),
            IRExprStmt exprStmt => ExprUsesStrSplit(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesStrSplit),
            IRThisConstructorCall thisCall => thisCall.Arguments.Any(ExprUsesStrSplit),
            IRReturn ret => ret.Value is not null && ExprUsesStrSplit(ret.Value),
            IRMultiReturn ret => ret.Values.Any(ExprUsesStrSplit),
            IRIf iff => ExprUsesStrSplit(iff.Condition) || BlockUsesStrSplit(iff.Then) || (iff.Else is not null && BlockUsesStrSplit(iff.Else)),
            IRSwitch sw => ExprUsesStrSplit(sw.Value) || sw.Sections.Any(section => section.Labels.Any(ExprUsesStrSplit) || BlockUsesStrSplit(section.Body)),
            IRWhile wh => ExprUsesStrSplit(wh.Condition) || BlockUsesStrSplit(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesStrSplit(fr.Initializer))
                || (fr.Condition is not null && ExprUsesStrSplit(fr.Condition))
                || fr.Incrementors.Any(StmtUsesStrSplit)
                || BlockUsesStrSplit(fr.Body),
            IRForEach fe => ExprUsesStrSplit(fe.Collection) || BlockUsesStrSplit(fe.Body),
            IRTry tr => BlockUsesStrSplit(tr.Try)
                || tr.Catches.Any(catchClause => BlockUsesStrSplit(catchClause.Body))
                || (tr.Finally is not null && BlockUsesStrSplit(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesStrSplit(th.Value),
            _ => false,
        };

    private static bool ExprUsesStrSplit(IRExpr expr)
        => expr switch
        {
            IRRuntimeInvocation inv => inv.Name == "StrSplit__" || inv.Arguments.Any(ExprUsesStrSplit),
            IRMemberAccess member => ExprUsesStrSplit(member.Target),
            IRElementAccess element => ExprUsesStrSplit(element.Target) || ExprUsesStrSplit(element.Index),
            IRLength length => ExprUsesStrSplit(length.Target),
            IRInvocation invocation => ExprUsesStrSplit(invocation.Callee) || invocation.Arguments.Any(ExprUsesStrSplit),
            IRFunctionExpression functionExpression => BlockUsesStrSplit(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesStrSplit),
            IRArrayNew arrayNew => ExprUsesStrSplit(arrayNew.Size),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesStrSplit(f.Value)),
            IRStringConcat concat => concat.Parts.Any(ExprUsesStrSplit),
            IRLuaRequire luaRequire => ExprUsesStrSplit(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesStrSplit(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesStrSplit(luaAccess.Target) || ExprUsesStrSplit(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesStrSplit(luaMethodInvocation.Target) || ExprUsesStrSplit(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesStrSplit),
            IRBinary binary => ExprUsesStrSplit(binary.Left) || ExprUsesStrSplit(binary.Right),
            IRUnary unary => ExprUsesStrSplit(unary.Operand),
            IRTernary ternary => ExprUsesStrSplit(ternary.Condition) || ExprUsesStrSplit(ternary.WhenTrue) || ExprUsesStrSplit(ternary.WhenFalse),
            IRCoalesceAssignment coalesceAssignment => ExprUsesStrSplit(coalesceAssignment.Target) || ExprUsesStrSplit(coalesceAssignment.Value),
            IRIs isExpr => ExprUsesStrSplit(isExpr.Value) || ExprUsesStrSplit(isExpr.Type),
            IRAs asExpr => ExprUsesStrSplit(asExpr.Value) || ExprUsesStrSplit(asExpr.Type),
            _ => false,
        };

    private static bool BlockUsesTypeChecks(IRBlock block)
        => block.Statements.Any(StmtUsesTypeChecks);

    private static bool BlockUsesStringConcat(IRBlock block)
        => block.Statements.Any(StmtUsesStringConcat);

    private static bool BlockUsesCoroutineHelpers(IRBlock block)
        => block.Statements.Any(StmtUsesCoroutineHelpers);

    private static bool StmtUsesTypeChecks(IRStmt stmt)
        => stmt switch
        {
            IRStatementList list => list.Statements.Any(StmtUsesTypeChecks),
            IRBlock block => BlockUsesTypeChecks(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesTypeChecks(local.Initializer),
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesTypeChecks),
            IRAssign assign => ExprUsesTypeChecks(assign.Target) || ExprUsesTypeChecks(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesTypeChecks) || assign.Values.Any(ExprUsesTypeChecks),
            IRExprStmt exprStmt => ExprUsesTypeChecks(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesTypeChecks),
            IRThisConstructorCall thisCall => thisCall.Arguments.Any(ExprUsesTypeChecks),
            IRReturn ret => ret.Value is not null && ExprUsesTypeChecks(ret.Value),
            IRMultiReturn ret => ret.Values.Any(ExprUsesTypeChecks),
            IRIf iff => ExprUsesTypeChecks(iff.Condition) || BlockUsesTypeChecks(iff.Then) || (iff.Else is not null && BlockUsesTypeChecks(iff.Else)),
            IRSwitch sw => ExprUsesTypeChecks(sw.Value) || sw.Sections.Any(section => section.Labels.Any(ExprUsesTypeChecks) || BlockUsesTypeChecks(section.Body)),
            IRWhile wh => ExprUsesTypeChecks(wh.Condition) || BlockUsesTypeChecks(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesTypeChecks(fr.Initializer))
                || (fr.Condition is not null && ExprUsesTypeChecks(fr.Condition))
                || fr.Incrementors.Any(StmtUsesTypeChecks)
                || BlockUsesTypeChecks(fr.Body),
            IRForEach fe => ExprUsesTypeChecks(fe.Collection) || BlockUsesTypeChecks(fe.Body),
            IRTry tr => BlockUsesTypeChecks(tr.Try)
                || tr.Catches.Any(catchClause => BlockUsesTypeChecks(catchClause.Body))
                || (tr.Finally is not null && BlockUsesTypeChecks(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesTypeChecks(th.Value),
            _ => false,
        };

    private static bool StmtUsesCoroutineHelpers(IRStmt stmt)
        => stmt switch
        {
            IRStatementList list => list.Statements.Any(StmtUsesCoroutineHelpers),
            IRBlock block => BlockUsesCoroutineHelpers(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesCoroutineHelpers(local.Initializer),
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesCoroutineHelpers),
            IRAssign assign => ExprUsesCoroutineHelpers(assign.Target) || ExprUsesCoroutineHelpers(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesCoroutineHelpers) || assign.Values.Any(ExprUsesCoroutineHelpers),
            IRExprStmt exprStmt => ExprUsesCoroutineHelpers(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesCoroutineHelpers),
            IRThisConstructorCall thisCall => thisCall.Arguments.Any(ExprUsesCoroutineHelpers),
            IRReturn ret => ret.Value is not null && ExprUsesCoroutineHelpers(ret.Value),
            IRMultiReturn ret => ret.Values.Any(ExprUsesCoroutineHelpers),
            IRIf iff => ExprUsesCoroutineHelpers(iff.Condition) || BlockUsesCoroutineHelpers(iff.Then) || (iff.Else is not null && BlockUsesCoroutineHelpers(iff.Else)),
            IRSwitch sw => ExprUsesCoroutineHelpers(sw.Value) || sw.Sections.Any(section => section.Labels.Any(ExprUsesCoroutineHelpers) || BlockUsesCoroutineHelpers(section.Body)),
            IRWhile wh => ExprUsesCoroutineHelpers(wh.Condition) || BlockUsesCoroutineHelpers(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesCoroutineHelpers(fr.Initializer))
                || (fr.Condition is not null && ExprUsesCoroutineHelpers(fr.Condition))
                || fr.Incrementors.Any(StmtUsesCoroutineHelpers)
                || BlockUsesCoroutineHelpers(fr.Body),
            IRForEach fe => ExprUsesCoroutineHelpers(fe.Collection) || BlockUsesCoroutineHelpers(fe.Body),
            IRTry tr => BlockUsesCoroutineHelpers(tr.Try)
                || tr.Catches.Any(catchClause => BlockUsesCoroutineHelpers(catchClause.Body))
                || (tr.Finally is not null && BlockUsesCoroutineHelpers(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesCoroutineHelpers(th.Value),
            _ => false,
        };

    private static bool StmtUsesStringConcat(IRStmt stmt)
        => stmt switch
        {
            IRStatementList list => list.Statements.Any(StmtUsesStringConcat),
            IRBlock block => BlockUsesStringConcat(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesStringConcat(local.Initializer),
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesStringConcat),
            IRAssign assign => ExprUsesStringConcat(assign.Target) || ExprUsesStringConcat(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesStringConcat) || assign.Values.Any(ExprUsesStringConcat),
            IRExprStmt exprStmt => ExprUsesStringConcat(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesStringConcat),
            IRThisConstructorCall thisCall => thisCall.Arguments.Any(ExprUsesStringConcat),
            IRReturn ret => ret.Value is not null && ExprUsesStringConcat(ret.Value),
            IRMultiReturn ret => ret.Values.Any(ExprUsesStringConcat),
            IRIf iff => ExprUsesStringConcat(iff.Condition) || BlockUsesStringConcat(iff.Then) || (iff.Else is not null && BlockUsesStringConcat(iff.Else)),
            IRSwitch sw => ExprUsesStringConcat(sw.Value) || sw.Sections.Any(section => section.Labels.Any(ExprUsesStringConcat) || BlockUsesStringConcat(section.Body)),
            IRWhile wh => ExprUsesStringConcat(wh.Condition) || BlockUsesStringConcat(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesStringConcat(fr.Initializer))
                || (fr.Condition is not null && ExprUsesStringConcat(fr.Condition))
                || fr.Incrementors.Any(StmtUsesStringConcat)
                || BlockUsesStringConcat(fr.Body),
            IRForEach fe => ExprUsesStringConcat(fe.Collection) || BlockUsesStringConcat(fe.Body),
            IRTry tr => BlockUsesStringConcat(tr.Try)
                || tr.Catches.Any(catchClause => BlockUsesStringConcat(catchClause.Body))
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
            IRFunctionExpression functionExpression => BlockUsesTypeChecks(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesTypeChecks),
            IRArrayNew arrayNew => ExprUsesTypeChecks(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesTypeChecks),
            IRLuaRequire luaRequire => ExprUsesTypeChecks(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesTypeChecks(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesTypeChecks(luaAccess.Target) || ExprUsesTypeChecks(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesTypeChecks(luaMethodInvocation.Target) || ExprUsesTypeChecks(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesTypeChecks),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesTypeChecks),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesTypeChecks(f.Value)),
            IRBinary binary => ExprUsesTypeChecks(binary.Left) || ExprUsesTypeChecks(binary.Right),
            IRUnary unary => ExprUsesTypeChecks(unary.Operand),
            IRCoalesceAssignment coalesceAssignment => ExprUsesTypeChecks(coalesceAssignment.Target) || ExprUsesTypeChecks(coalesceAssignment.Value),
            _ => false,
        };

    private static bool ExprUsesStringConcat(IRExpr expr)
        => expr switch
        {
            IRStringConcat => true,
            IRLuaRequire luaRequire => ExprUsesStringConcat(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesStringConcat(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesStringConcat(luaAccess.Target) || ExprUsesStringConcat(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesStringConcat(luaMethodInvocation.Target) || ExprUsesStringConcat(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesStringConcat),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesStringConcat),
            IRMemberAccess member => ExprUsesStringConcat(member.Target),
            IRElementAccess element => ExprUsesStringConcat(element.Target) || ExprUsesStringConcat(element.Index),
            IRLength length => ExprUsesStringConcat(length.Target),
            IRInvocation invocation => ExprUsesStringConcat(invocation.Callee) || invocation.Arguments.Any(ExprUsesStringConcat),
            IRFunctionExpression functionExpression => BlockUsesStringConcat(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesStringConcat),
            IRArrayNew arrayNew => ExprUsesStringConcat(arrayNew.Size),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesStringConcat(f.Value)),
            IRBinary binary => ExprUsesStringConcat(binary.Left) || ExprUsesStringConcat(binary.Right),
            IRUnary unary => ExprUsesStringConcat(unary.Operand),
            IRCoalesceAssignment coalesceAssignment => ExprUsesStringConcat(coalesceAssignment.Target) || ExprUsesStringConcat(coalesceAssignment.Value),
            IRIs isExpr => ExprUsesStringConcat(isExpr.Value) || ExprUsesStringConcat(isExpr.Type),
            IRAs asExpr => ExprUsesStringConcat(asExpr.Value) || ExprUsesStringConcat(asExpr.Type),
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
            IRFunctionExpression functionExpression => BlockUsesCoroutineHelpers(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesCoroutineHelpers),
            IRArrayNew arrayNew => ExprUsesCoroutineHelpers(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesCoroutineHelpers),
            IRLuaRequire luaRequire => ExprUsesCoroutineHelpers(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesCoroutineHelpers(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesCoroutineHelpers(luaAccess.Target) || ExprUsesCoroutineHelpers(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesCoroutineHelpers(luaMethodInvocation.Target) || ExprUsesCoroutineHelpers(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesCoroutineHelpers),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesCoroutineHelpers(f.Value)),
            IRBinary binary => ExprUsesCoroutineHelpers(binary.Left) || ExprUsesCoroutineHelpers(binary.Right),
            IRUnary unary => ExprUsesCoroutineHelpers(unary.Operand),
            IRCoalesceAssignment coalesceAssignment => ExprUsesCoroutineHelpers(coalesceAssignment.Target) || ExprUsesCoroutineHelpers(coalesceAssignment.Value),
            IRIs isExpr => ExprUsesCoroutineHelpers(isExpr.Value) || ExprUsesCoroutineHelpers(isExpr.Type),
            IRAs asExpr => ExprUsesCoroutineHelpers(asExpr.Value) || ExprUsesCoroutineHelpers(asExpr.Type),
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
            case IRStatementList list:
                foreach (var child in list.Statements) CollectIdentifiers(child);
                break;
            case IRBlock block:
                CollectIdentifiers(block);
                break;
            case IRLocalDecl local:
                _usedIdentifiers.Add(local.Name);
                if (local.Initializer is not null) CollectIdentifiers(local.Initializer);
                break;
            case IRMultiLocalDecl local:
                foreach (var name in local.Names) _usedIdentifiers.Add(name);
                foreach (var initializer in local.Initializers) CollectIdentifiers(initializer);
                break;
            case IRAssign assign:
                CollectIdentifiers(assign.Target);
                CollectIdentifiers(assign.Value);
                break;
            case IRMultiAssign assign:
                foreach (var target in assign.Targets) CollectIdentifiers(target);
                foreach (var value in assign.Values) CollectIdentifiers(value);
                break;
            case IRExprStmt exprStmt:
                CollectIdentifiers(exprStmt.Expression);
                break;
            case IRBaseConstructorCall baseCall:
                foreach (var argument in baseCall.Arguments) CollectIdentifiers(argument);
                break;
            case IRThisConstructorCall thisCall:
                foreach (var argument in thisCall.Arguments) CollectIdentifiers(argument);
                break;
            case IRReturn ret when ret.Value is not null:
                CollectIdentifiers(ret.Value);
                break;
            case IRMultiReturn ret:
                foreach (var value in ret.Values) CollectIdentifiers(value);
                break;
            case IRIf iff:
                CollectIdentifiers(iff.Condition);
                CollectIdentifiers(iff.Then);
                if (iff.Else is not null) CollectIdentifiers(iff.Else);
                break;
            case IRSwitch sw:
                CollectIdentifiers(sw.Value);
                foreach (var section in sw.Sections)
                {
                    foreach (var label in section.Labels) CollectIdentifiers(label);
                    CollectIdentifiers(section.Body);
                }
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
            case IRTry tr:
                CollectIdentifiers(tr.Try);
                foreach (var catchClause in tr.Catches)
                {
                    if (!string.IsNullOrEmpty(catchClause.Variable)) _usedIdentifiers.Add(catchClause.Variable);
                    CollectIdentifiers(catchClause.Body);
                }
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
            case IRFunctionExpression functionExpression:
                foreach (var parameter in functionExpression.Parameters) _usedIdentifiers.Add(parameter);
                CollectIdentifiers(functionExpression.Body);
                break;
            case IRArrayLiteral array:
                foreach (var item in array.Items) CollectIdentifiers(item);
                break;
            case IRArrayNew arrayNew:
                CollectIdentifiers(arrayNew.Size);
                break;
            case IRTableLiteralNew tableLiteralNew:
                foreach (var (_, value) in tableLiteralNew.Fields) CollectIdentifiers(value);
                break;
            case IRStringConcat concat:
                foreach (var part in concat.Parts) CollectIdentifiers(part);
                break;
            case IRLuaRequire luaRequire:
                CollectIdentifiers(luaRequire.ModuleName);
                break;
            case IRLuaGlobal luaGlobal:
                CollectIdentifiers(luaGlobal.Name);
                break;
            case IRLuaAccess luaAccess:
                CollectIdentifiers(luaAccess.Target);
                CollectIdentifiers(luaAccess.Name);
                break;
            case IRLuaMethodInvocation luaMethodInvocation:
                CollectIdentifiers(luaMethodInvocation.Target);
                CollectIdentifiers(luaMethodInvocation.Name);
                foreach (var argument in luaMethodInvocation.Arguments) CollectIdentifiers(argument);
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
            case IRCoalesceAssignment coalesceAssignment:
                CollectIdentifiers(coalesceAssignment.Target);
                CollectIdentifiers(coalesceAssignment.Value);
                break;
            case IRIs isExpr:
                CollectIdentifiers(isExpr.Value);
                CollectIdentifiers(isExpr.Type);
                break;
            case IRAs asExpr:
                CollectIdentifiers(asExpr.Value);
                CollectIdentifiers(asExpr.Type);
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
