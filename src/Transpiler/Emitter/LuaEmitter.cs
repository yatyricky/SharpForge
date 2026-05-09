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
    private bool _emitDictionaryHelpers;
    private bool _emitListHelpers;
    private bool _emitCoroutineHelpers;
    private bool _emitTernaryHelper;

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
        _emitTernaryHelper = UsesTernaryHelper(module);
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
            if (_emitTernaryHelper)
            {
                WriteTernaryHelper();
            }
        }
    }

    private void WriteTernaryHelper()
    {
        WriteLine($"function {_rootTable}.Ternary__(cond, a, b)");
        _indent++;
        WriteLine("if cond then return a else return b end");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
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
        WriteLine("return { data = {}, keys = {}, version = 0 }");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictCount__(dict)");
        _indent++;
        WriteLine("return #dict.keys");
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
        WriteLine("dict.version = dict.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictAdd__(dict, key, value)");
        _indent++;
        WriteLine("if dict.data[key] ~= nil then error(\"duplicate key\") end");
        WriteLine("table.insert(dict.keys, key)");
        WriteLine($"dict.data[key] = value == nil and {_rootTable}.DictNil__ or value");
        WriteLine("dict.version = dict.version + 1");
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
        WriteLine("dict.version = dict.version + 1");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        WriteLine("return false");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictContainsKey__(dict, key)");
        _indent++;
        WriteLine("return dict.data[key] ~= nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictClear__(dict)");
        _indent++;
        WriteLine("dict.data = {}");
        WriteLine("dict.keys = {}");
        WriteLine("dict.version = dict.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictKeys__(dict)");
        _indent++;
        WriteLine("local items = {}");
        WriteLine("for i, key in ipairs(dict.keys) do items[i] = key end");
        WriteLine($"return {_rootTable}.ListNew__(items)");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictValues__(dict)");
        _indent++;
        WriteLine($"local list = {_rootTable}.ListNew__({{}})");
        WriteLine("for i, key in ipairs(dict.keys) do");
        _indent++;
        WriteLine("local value = dict.data[key]");
        WriteLine($"if value == {_rootTable}.DictNil__ then value = nil end");
        WriteLine($"list.items[i] = {_rootTable}.ListWrap__(value)");
        _indent--;
        WriteLine("end");
        WriteLine("return list");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.DictIterate__(dict)");
        _indent++;
        WriteLine("local version = dict.version");
        WriteLine("local i = 0");
        WriteLine("return function()");
        _indent++;
        WriteLine("if dict.version ~= version then error(\"collection was modified during iteration\") end");
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
        WriteLine($"{_rootTable}.ListNil__ = {_rootTable}.ListNil__ or {{}}");

        WriteLine($"function {_rootTable}.ListWrap__(value)");
        _indent++;
        WriteLine($"return value == nil and {_rootTable}.ListNil__ or value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListUnwrap__(value)");
        _indent++;
        WriteLine($"if value == {_rootTable}.ListNil__ then return nil end");
        WriteLine("return value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListNew__(items)");
        _indent++;
        WriteLine("local list = { items = {}, version = 0 }");
        WriteLine("if items ~= nil then");
        _indent++;
        WriteLine("for i = 1, #items do");
        _indent++;
        WriteLine($"list.items[i] = {_rootTable}.ListWrap__(items[i])");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        WriteLine("return list");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListCount__(list)");
        _indent++;
        WriteLine("return #list.items");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListGet__(list, index)");
        _indent++;
        WriteLine($"return {_rootTable}.ListUnwrap__(list.items[index + 1])");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListSet__(list, index, value)");
        _indent++;
        WriteLine($"list.items[index + 1] = {_rootTable}.ListWrap__(value)");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListAdd__(list, value)");
        _indent++;
        WriteLine($"table.insert(list.items, {_rootTable}.ListWrap__(value))");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListAddRange__(list, values)");
        _indent++;
        WriteLine("local source = values.items or values");
        WriteLine("local sourceIsList = values.items ~= nil");
        WriteLine("for i = 1, #source do");
        _indent++;
        WriteLine($"table.insert(list.items, sourceIsList and source[i] or {_rootTable}.ListWrap__(source[i]))");
        _indent--;
        WriteLine("end");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListClear__(list)");
        _indent++;
        WriteLine("list.items = {}");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListIndexOf__(list, value)");
        _indent++;
        WriteLine($"local stored = {_rootTable}.ListWrap__(value)");
        WriteLine("for i, item in ipairs(list.items) do");
        _indent++;
        WriteLine("if item == stored then return i - 1 end");
        _indent--;
        WriteLine("end");
        WriteLine("return -1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListContains__(list, value)");
        _indent++;
        WriteLine($"return {_rootTable}.ListIndexOf__(list, value) >= 0");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListInsert__(list, index, value)");
        _indent++;
        WriteLine($"table.insert(list.items, index + 1, {_rootTable}.ListWrap__(value))");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListRemoveAt__(list, index)");
        _indent++;
        WriteLine("table.remove(list.items, index + 1)");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListRemove__(list, value)");
        _indent++;
        WriteLine($"local index = {_rootTable}.ListIndexOf__(list, value)");
        WriteLine("if index >= 0 then");
        _indent++;
        WriteLine($"{_rootTable}.ListRemoveAt__(list, index)");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        WriteLine("return false");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListReverse__(list)");
        _indent++;
        WriteLine("local items = list.items");
        WriteLine("local left = 1");
        WriteLine("local right = #items");
        WriteLine("while left < right do");
        _indent++;
        WriteLine("items[left], items[right] = items[right], items[left]");
        WriteLine("left = left + 1");
        WriteLine("right = right - 1");
        _indent--;
        WriteLine("end");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListIterate__(list)");
        _indent++;
        WriteLine("local version = list.version");
        WriteLine("local i = 0");
        WriteLine("return function()");
        _indent++;
        WriteLine("if list.version ~= version then error(\"collection was modified during iteration\") end");
        WriteLine("i = i + 1");
        WriteLine("local value = list.items[i]");
        WriteLine($"if value ~= nil then return i, {_rootTable}.ListUnwrap__(value) end");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListSort__(list, less)");
        _indent++;
        WriteLine("local compare = less or function(a, b) return a < b end");
        WriteLine("local items = list.items");
        WriteLine("for i = 2, #items do");
        _indent++;
        WriteLine("local value = items[i]");
        WriteLine("local j = i - 1");
        WriteLine($"while j >= 1 and compare({_rootTable}.ListUnwrap__(value), {_rootTable}.ListUnwrap__(items[j])) do");
        _indent++;
        WriteLine("items[j + 1] = items[j]");
        WriteLine("j = j - 1");
        _indent--;
        WriteLine("end");
        WriteLine("items[j + 1] = value");
        _indent--;
        WriteLine("end");
        WriteLine("list.version = list.version + 1");
        WriteLine("return list");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');

        WriteLine($"function {_rootTable}.ListToArray__(list)");
        _indent++;
        WriteLine("local result = {}");
        WriteLine("for i, item in ipairs(list.items) do");
        _indent++;
        WriteLine($"result[i] = {_rootTable}.ListUnwrap__(item)");
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

    private void EmitType(IRType type)
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
        foreach (var moduleName in type.LuaRequires)
        {
            WriteLine($"require(\"{EscapeLuaString(moduleName)}\")");
        }
        if (type.LuaClass is { } luaClass)
        {
            EmitLuaClassDeclaration(typePath, luaClass);
        }
        else
        {
            WriteLine($"{typePath} = {typePath} or {{}}");
        }
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

        // Static field initializers.
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

        for (int i = 0; i < methods.Length; i++)
        {
            EmitMethod(type, typePath, methods[i], type.Fields.Where(f => !f.IsStatic));
            if (i < methods.Length - 1)
            {
                _sb.Append('\n');
            }
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
        if (m.BaseConstructorCall is not null)
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
        var indexName = NewTemp("i");

        WriteLine("do");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(collectionName).Append(" = ");
        EmitExpr(f.Collection);
        _sb.Append('\n');
        var iterator = f.UseListIterator ? $"{_rootTable}.ListIterate__({collectionName})" : $"ipairs({collectionName})";
        WriteLine($"for {indexName}, {f.ItemName} in {iterator} do");
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
            case IRDictionaryNew:
                _sb.Append(_rootTable).Append(".DictNew__()");
                break;
            case IRDictionaryCount dictionaryCount:
                _sb.Append(_rootTable).Append(".DictCount__(");
                EmitExpr(dictionaryCount.Table);
                _sb.Append(')');
                break;
            case IRDictionaryGet dictionaryGet:
                _sb.Append(_rootTable).Append(".DictGet__(");
                EmitExpr(dictionaryGet.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryGet.Key);
                _sb.Append(')');
                break;
            case IRDictionaryAdd dictionaryAdd:
                _sb.Append(_rootTable).Append(".DictAdd__(");
                EmitExpr(dictionaryAdd.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryAdd.Key);
                _sb.Append(", ");
                EmitExpr(dictionaryAdd.Value);
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
            case IRDictionaryContainsKey dictionaryContainsKey:
                _sb.Append(_rootTable).Append(".DictContainsKey__(");
                EmitExpr(dictionaryContainsKey.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryContainsKey.Key);
                _sb.Append(')');
                break;
            case IRDictionaryClear dictionaryClear:
                _sb.Append(_rootTable).Append(".DictClear__(");
                EmitExpr(dictionaryClear.Table);
                _sb.Append(')');
                break;
            case IRDictionaryKeys dictionaryKeys:
                _sb.Append(_rootTable).Append(".DictKeys__(");
                EmitExpr(dictionaryKeys.Table);
                _sb.Append(')');
                break;
            case IRDictionaryValues dictionaryValues:
                _sb.Append(_rootTable).Append(".DictValues__(");
                EmitExpr(dictionaryValues.Table);
                _sb.Append(')');
                break;
            case IRListNew listNew:
                _sb.Append(_rootTable).Append(".ListNew__({");
                for (int i = 0; i < listNew.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    EmitExpr(listNew.Items[i]);
                }
                _sb.Append("})");
                break;
            case IRListCount listCount:
                _sb.Append(_rootTable).Append(".ListCount__(");
                EmitExpr(listCount.List);
                _sb.Append(')');
                break;
            case IRListGet listGet:
                _sb.Append(_rootTable).Append(".ListGet__(");
                EmitExpr(listGet.List);
                _sb.Append(", ");
                EmitExpr(listGet.Index);
                _sb.Append(')');
                break;
            case IRListSet listSet:
                _sb.Append(_rootTable).Append(".ListSet__(");
                EmitExpr(listSet.List);
                _sb.Append(", ");
                EmitExpr(listSet.Index);
                _sb.Append(", ");
                EmitExpr(listSet.Value);
                _sb.Append(')');
                break;
            case IRListAdd listAdd:
                _sb.Append(_rootTable).Append(".ListAdd__(");
                EmitExpr(listAdd.List);
                _sb.Append(", ");
                EmitExpr(listAdd.Value);
                _sb.Append(')');
                break;
            case IRListAddRange listAddRange:
                _sb.Append(_rootTable).Append(".ListAddRange__(");
                EmitExpr(listAddRange.List);
                _sb.Append(", ");
                EmitExpr(listAddRange.Items);
                _sb.Append(')');
                break;
            case IRListClear listClear:
                _sb.Append(_rootTable).Append(".ListClear__(");
                EmitExpr(listClear.List);
                _sb.Append(')');
                break;
            case IRListContains listContains:
                _sb.Append(_rootTable).Append(".ListContains__(");
                EmitExpr(listContains.List);
                _sb.Append(", ");
                EmitExpr(listContains.Value);
                _sb.Append(')');
                break;
            case IRListIndexOf listIndexOf:
                _sb.Append(_rootTable).Append(".ListIndexOf__(");
                EmitExpr(listIndexOf.List);
                _sb.Append(", ");
                EmitExpr(listIndexOf.Value);
                _sb.Append(')');
                break;
            case IRListInsert listInsert:
                _sb.Append(_rootTable).Append(".ListInsert__(");
                EmitExpr(listInsert.List);
                _sb.Append(", ");
                EmitExpr(listInsert.Index);
                _sb.Append(", ");
                EmitExpr(listInsert.Value);
                _sb.Append(')');
                break;
            case IRListRemove listRemove:
                _sb.Append(_rootTable).Append(".ListRemove__(");
                EmitExpr(listRemove.List);
                _sb.Append(", ");
                EmitExpr(listRemove.Value);
                _sb.Append(')');
                break;
            case IRListRemoveAt listRemoveAt:
                _sb.Append(_rootTable).Append(".ListRemoveAt__(");
                EmitExpr(listRemoveAt.List);
                _sb.Append(", ");
                EmitExpr(listRemoveAt.Index);
                _sb.Append(')');
                break;
            case IRListReverse listReverse:
                _sb.Append(_rootTable).Append(".ListReverse__(");
                EmitExpr(listReverse.List);
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
            case IRListToArray listToArray:
                _sb.Append(_rootTable).Append(".ListToArray__(");
                EmitExpr(listToArray.List);
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
                _sb.Append(_rootTable).Append(".Ternary__(");
                EmitExpr(ternary.Condition);
                _sb.Append(", ");
                EmitExpr(ternary.WhenTrue);
                _sb.Append(", ");
                EmitExpr(ternary.WhenFalse);
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

    private static bool UsesDictionaryHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesDictionaryHelpers(m.Body));

    private static bool UsesListHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesListHelpers(m.Body));

    private static bool UsesCoroutineHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => m.IsCoroutine || BlockUsesCoroutineHelpers(m.Body));

    private static bool UsesTernaryHelper(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesTernaryHelper(m.Body));

    private static bool BlockUsesTernaryHelper(IRBlock block)
        => block.Statements.Any(StmtUsesTernaryHelper);

    private static bool StmtUsesTernaryHelper(IRStmt stmt)
        => stmt switch
        {
            IRBlock block => BlockUsesTernaryHelper(block),
            IRLocalDecl local => local.Initializer is not null && ExprUsesTernaryHelper(local.Initializer),
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesTernaryHelper),
            IRAssign assign => ExprUsesTernaryHelper(assign.Target) || ExprUsesTernaryHelper(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesTernaryHelper) || assign.Values.Any(ExprUsesTernaryHelper),
            IRExprStmt exprStmt => ExprUsesTernaryHelper(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesTernaryHelper),
            IRReturn ret => ret.Value is not null && ExprUsesTernaryHelper(ret.Value),
            IRMultiReturn ret => ret.Values.Any(ExprUsesTernaryHelper),
            IRIf iff => ExprUsesTernaryHelper(iff.Condition) || BlockUsesTernaryHelper(iff.Then) || (iff.Else is not null && BlockUsesTernaryHelper(iff.Else)),
            IRSwitch sw => ExprUsesTernaryHelper(sw.Value) || sw.Sections.Any(section => section.Labels.Any(ExprUsesTernaryHelper) || BlockUsesTernaryHelper(section.Body)),
            IRWhile wh => ExprUsesTernaryHelper(wh.Condition) || BlockUsesTernaryHelper(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesTernaryHelper(fr.Initializer))
                || (fr.Condition is not null && ExprUsesTernaryHelper(fr.Condition))
                || fr.Incrementors.Any(StmtUsesTernaryHelper)
                || BlockUsesTernaryHelper(fr.Body),
            IRForEach fe => ExprUsesTernaryHelper(fe.Collection) || BlockUsesTernaryHelper(fe.Body),
            IRDictionaryForEach fe => ExprUsesTernaryHelper(fe.Dictionary) || BlockUsesTernaryHelper(fe.Body),
            IRTry tr => BlockUsesTernaryHelper(tr.Try)
                || (tr.Catch is not null && BlockUsesTernaryHelper(tr.Catch))
                || (tr.Finally is not null && BlockUsesTernaryHelper(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesTernaryHelper(th.Value),
            _ => false,
        };

    private static bool ExprUsesTernaryHelper(IRExpr expr)
        => expr switch
        {
            IRTernary => true,
            IRMemberAccess member => ExprUsesTernaryHelper(member.Target),
            IRElementAccess element => ExprUsesTernaryHelper(element.Target) || ExprUsesTernaryHelper(element.Index),
            IRLength length => ExprUsesTernaryHelper(length.Target),
            IRInvocation invocation => ExprUsesTernaryHelper(invocation.Callee) || invocation.Arguments.Any(ExprUsesTernaryHelper),
            IRFunctionExpression functionExpression => BlockUsesTernaryHelper(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesTernaryHelper),
            IRArrayNew arrayNew => ExprUsesTernaryHelper(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesTernaryHelper),
            IRDictionaryCount dictionaryCount => ExprUsesTernaryHelper(dictionaryCount.Table),
            IRDictionaryGet dictionaryGet => ExprUsesTernaryHelper(dictionaryGet.Table) || ExprUsesTernaryHelper(dictionaryGet.Key),
            IRDictionaryAdd dictionaryAdd => ExprUsesTernaryHelper(dictionaryAdd.Table) || ExprUsesTernaryHelper(dictionaryAdd.Key) || ExprUsesTernaryHelper(dictionaryAdd.Value),
            IRDictionarySet dictionarySet => ExprUsesTernaryHelper(dictionarySet.Table) || ExprUsesTernaryHelper(dictionarySet.Key) || ExprUsesTernaryHelper(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesTernaryHelper(dictionaryRemove.Table) || ExprUsesTernaryHelper(dictionaryRemove.Key),
            IRDictionaryContainsKey dictionaryContainsKey => ExprUsesTernaryHelper(dictionaryContainsKey.Table) || ExprUsesTernaryHelper(dictionaryContainsKey.Key),
            IRDictionaryClear dictionaryClear => ExprUsesTernaryHelper(dictionaryClear.Table),
            IRDictionaryKeys dictionaryKeys => ExprUsesTernaryHelper(dictionaryKeys.Table),
            IRDictionaryValues dictionaryValues => ExprUsesTernaryHelper(dictionaryValues.Table),
            IRListNew listNew => listNew.Items.Any(ExprUsesTernaryHelper),
            IRListCount listCount => ExprUsesTernaryHelper(listCount.List),
            IRListGet listGet => ExprUsesTernaryHelper(listGet.List) || ExprUsesTernaryHelper(listGet.Index),
            IRListSet listSet => ExprUsesTernaryHelper(listSet.List) || ExprUsesTernaryHelper(listSet.Index) || ExprUsesTernaryHelper(listSet.Value),
            IRListAdd listAdd => ExprUsesTernaryHelper(listAdd.List) || ExprUsesTernaryHelper(listAdd.Value),
            IRListAddRange listAddRange => ExprUsesTernaryHelper(listAddRange.List) || ExprUsesTernaryHelper(listAddRange.Items),
            IRListClear listClear => ExprUsesTernaryHelper(listClear.List),
            IRListContains listContains => ExprUsesTernaryHelper(listContains.List) || ExprUsesTernaryHelper(listContains.Value),
            IRListIndexOf listIndexOf => ExprUsesTernaryHelper(listIndexOf.List) || ExprUsesTernaryHelper(listIndexOf.Value),
            IRListInsert listInsert => ExprUsesTernaryHelper(listInsert.List) || ExprUsesTernaryHelper(listInsert.Index) || ExprUsesTernaryHelper(listInsert.Value),
            IRListRemove listRemove => ExprUsesTernaryHelper(listRemove.List) || ExprUsesTernaryHelper(listRemove.Value),
            IRListRemoveAt listRemoveAt => ExprUsesTernaryHelper(listRemoveAt.List) || ExprUsesTernaryHelper(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesTernaryHelper(listReverse.List),
            IRListSort listSort => ExprUsesTernaryHelper(listSort.List) || (listSort.Comparer is not null && ExprUsesTernaryHelper(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesTernaryHelper(listToArray.List),
            IRLuaRequire luaRequire => ExprUsesTernaryHelper(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesTernaryHelper(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesTernaryHelper(luaAccess.Target) || ExprUsesTernaryHelper(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesTernaryHelper(luaMethodInvocation.Target) || ExprUsesTernaryHelper(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesTernaryHelper),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesTernaryHelper),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesTernaryHelper(f.Value)),
            IRBinary binary => ExprUsesTernaryHelper(binary.Left) || ExprUsesTernaryHelper(binary.Right),
            IRUnary unary => ExprUsesTernaryHelper(unary.Operand),
            IRIs isExpr => ExprUsesTernaryHelper(isExpr.Value),
            IRAs asExpr => ExprUsesTernaryHelper(asExpr.Value),
            _ => false,
        };

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
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesTypeChecks),
            IRAssign assign => ExprUsesTypeChecks(assign.Target) || ExprUsesTypeChecks(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesTypeChecks) || assign.Values.Any(ExprUsesTypeChecks),
            IRExprStmt exprStmt => ExprUsesTypeChecks(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesTypeChecks),
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
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesDictionaryHelpers),
            IRAssign assign => ExprUsesDictionaryHelpers(assign.Target) || ExprUsesDictionaryHelpers(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesDictionaryHelpers) || assign.Values.Any(ExprUsesDictionaryHelpers),
            IRExprStmt exprStmt => ExprUsesDictionaryHelpers(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesDictionaryHelpers),
            IRReturn ret => ret.Value is not null && ExprUsesDictionaryHelpers(ret.Value),
            IRMultiReturn ret => ret.Values.Any(ExprUsesDictionaryHelpers),
            IRIf iff => ExprUsesDictionaryHelpers(iff.Condition) || BlockUsesDictionaryHelpers(iff.Then) || (iff.Else is not null && BlockUsesDictionaryHelpers(iff.Else)),
            IRSwitch sw => ExprUsesDictionaryHelpers(sw.Value) || sw.Sections.Any(section => section.Labels.Any(ExprUsesDictionaryHelpers) || BlockUsesDictionaryHelpers(section.Body)),
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
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesListHelpers),
            IRAssign assign => ExprUsesListHelpers(assign.Target) || ExprUsesListHelpers(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesListHelpers) || assign.Values.Any(ExprUsesListHelpers),
            IRExprStmt exprStmt => ExprUsesListHelpers(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesListHelpers),
            IRReturn ret => ret.Value is not null && ExprUsesListHelpers(ret.Value),
            IRMultiReturn ret => ret.Values.Any(ExprUsesListHelpers),
            IRIf iff => ExprUsesListHelpers(iff.Condition) || BlockUsesListHelpers(iff.Then) || (iff.Else is not null && BlockUsesListHelpers(iff.Else)),
            IRSwitch sw => ExprUsesListHelpers(sw.Value) || sw.Sections.Any(section => section.Labels.Any(ExprUsesListHelpers) || BlockUsesListHelpers(section.Body)),
            IRWhile wh => ExprUsesListHelpers(wh.Condition) || BlockUsesListHelpers(wh.Body),
            IRFor fr => (fr.Initializer is not null && StmtUsesListHelpers(fr.Initializer))
                || (fr.Condition is not null && ExprUsesListHelpers(fr.Condition))
                || fr.Incrementors.Any(StmtUsesListHelpers)
                || BlockUsesListHelpers(fr.Body),
            IRForEach fe => fe.UseListIterator || ExprUsesListHelpers(fe.Collection) || BlockUsesListHelpers(fe.Body),
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
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesCoroutineHelpers),
            IRAssign assign => ExprUsesCoroutineHelpers(assign.Target) || ExprUsesCoroutineHelpers(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesCoroutineHelpers) || assign.Values.Any(ExprUsesCoroutineHelpers),
            IRExprStmt exprStmt => ExprUsesCoroutineHelpers(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesCoroutineHelpers),
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
            IRMultiLocalDecl local => local.Initializers.Any(ExprUsesStringConcat),
            IRAssign assign => ExprUsesStringConcat(assign.Target) || ExprUsesStringConcat(assign.Value),
            IRMultiAssign assign => assign.Targets.Any(ExprUsesStringConcat) || assign.Values.Any(ExprUsesStringConcat),
            IRExprStmt exprStmt => ExprUsesStringConcat(exprStmt.Expression),
            IRBaseConstructorCall baseCall => baseCall.Arguments.Any(ExprUsesStringConcat),
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
            IRFunctionExpression functionExpression => BlockUsesTypeChecks(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesTypeChecks),
            IRArrayNew arrayNew => ExprUsesTypeChecks(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesTypeChecks),
            IRDictionaryNew => false,
            IRDictionaryCount dictionaryCount => ExprUsesTypeChecks(dictionaryCount.Table),
            IRDictionaryGet dictionaryGet => ExprUsesTypeChecks(dictionaryGet.Table) || ExprUsesTypeChecks(dictionaryGet.Key),
            IRDictionaryAdd dictionaryAdd => ExprUsesTypeChecks(dictionaryAdd.Table) || ExprUsesTypeChecks(dictionaryAdd.Key) || ExprUsesTypeChecks(dictionaryAdd.Value),
            IRDictionarySet dictionarySet => ExprUsesTypeChecks(dictionarySet.Table) || ExprUsesTypeChecks(dictionarySet.Key) || ExprUsesTypeChecks(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesTypeChecks(dictionaryRemove.Table) || ExprUsesTypeChecks(dictionaryRemove.Key),
            IRDictionaryContainsKey dictionaryContainsKey => ExprUsesTypeChecks(dictionaryContainsKey.Table) || ExprUsesTypeChecks(dictionaryContainsKey.Key),
            IRDictionaryClear dictionaryClear => ExprUsesTypeChecks(dictionaryClear.Table),
            IRDictionaryKeys dictionaryKeys => ExprUsesTypeChecks(dictionaryKeys.Table),
            IRDictionaryValues dictionaryValues => ExprUsesTypeChecks(dictionaryValues.Table),
            IRListNew listNew => listNew.Items.Any(ExprUsesTypeChecks),
            IRListCount listCount => ExprUsesTypeChecks(listCount.List),
            IRListGet listGet => ExprUsesTypeChecks(listGet.List) || ExprUsesTypeChecks(listGet.Index),
            IRListSet listSet => ExprUsesTypeChecks(listSet.List) || ExprUsesTypeChecks(listSet.Index) || ExprUsesTypeChecks(listSet.Value),
            IRListAdd listAdd => ExprUsesTypeChecks(listAdd.List) || ExprUsesTypeChecks(listAdd.Value),
            IRListAddRange listAddRange => ExprUsesTypeChecks(listAddRange.List) || ExprUsesTypeChecks(listAddRange.Items),
            IRListClear listClear => ExprUsesTypeChecks(listClear.List),
            IRListContains listContains => ExprUsesTypeChecks(listContains.List) || ExprUsesTypeChecks(listContains.Value),
            IRListIndexOf listIndexOf => ExprUsesTypeChecks(listIndexOf.List) || ExprUsesTypeChecks(listIndexOf.Value),
            IRListInsert listInsert => ExprUsesTypeChecks(listInsert.List) || ExprUsesTypeChecks(listInsert.Index) || ExprUsesTypeChecks(listInsert.Value),
            IRListRemove listRemove => ExprUsesTypeChecks(listRemove.List) || ExprUsesTypeChecks(listRemove.Value),
            IRListRemoveAt listRemoveAt => ExprUsesTypeChecks(listRemoveAt.List) || ExprUsesTypeChecks(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesTypeChecks(listReverse.List),
            IRListSort listSort => ExprUsesTypeChecks(listSort.List) || (listSort.Comparer is not null && ExprUsesTypeChecks(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesTypeChecks(listToArray.List),
            IRLuaRequire luaRequire => ExprUsesTypeChecks(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesTypeChecks(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesTypeChecks(luaAccess.Target) || ExprUsesTypeChecks(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesTypeChecks(luaMethodInvocation.Target) || ExprUsesTypeChecks(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesTypeChecks),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesTypeChecks),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesTypeChecks(f.Value)),
            IRBinary binary => ExprUsesTypeChecks(binary.Left) || ExprUsesTypeChecks(binary.Right),
            IRUnary unary => ExprUsesTypeChecks(unary.Operand),
            _ => false,
        };

    private static bool ExprUsesStringConcat(IRExpr expr)
        => expr switch
        {
            IRStringConcat => true,
            IRDictionaryNew => false,
            IRDictionaryCount dictionaryCount => ExprUsesStringConcat(dictionaryCount.Table),
            IRDictionaryGet dictionaryGet => ExprUsesStringConcat(dictionaryGet.Table) || ExprUsesStringConcat(dictionaryGet.Key),
            IRDictionaryAdd dictionaryAdd => ExprUsesStringConcat(dictionaryAdd.Table) || ExprUsesStringConcat(dictionaryAdd.Key) || ExprUsesStringConcat(dictionaryAdd.Value),
            IRDictionarySet dictionarySet => ExprUsesStringConcat(dictionarySet.Table) || ExprUsesStringConcat(dictionarySet.Key) || ExprUsesStringConcat(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesStringConcat(dictionaryRemove.Table) || ExprUsesStringConcat(dictionaryRemove.Key),
            IRDictionaryContainsKey dictionaryContainsKey => ExprUsesStringConcat(dictionaryContainsKey.Table) || ExprUsesStringConcat(dictionaryContainsKey.Key),
            IRDictionaryClear dictionaryClear => ExprUsesStringConcat(dictionaryClear.Table),
            IRDictionaryKeys dictionaryKeys => ExprUsesStringConcat(dictionaryKeys.Table),
            IRDictionaryValues dictionaryValues => ExprUsesStringConcat(dictionaryValues.Table),
            IRListNew listNew => listNew.Items.Any(ExprUsesStringConcat),
            IRListCount listCount => ExprUsesStringConcat(listCount.List),
            IRListGet listGet => ExprUsesStringConcat(listGet.List) || ExprUsesStringConcat(listGet.Index),
            IRListSet listSet => ExprUsesStringConcat(listSet.List) || ExprUsesStringConcat(listSet.Index) || ExprUsesStringConcat(listSet.Value),
            IRListAdd listAdd => ExprUsesStringConcat(listAdd.List) || ExprUsesStringConcat(listAdd.Value),
            IRListAddRange listAddRange => ExprUsesStringConcat(listAddRange.List) || ExprUsesStringConcat(listAddRange.Items),
            IRListClear listClear => ExprUsesStringConcat(listClear.List),
            IRListContains listContains => ExprUsesStringConcat(listContains.List) || ExprUsesStringConcat(listContains.Value),
            IRListIndexOf listIndexOf => ExprUsesStringConcat(listIndexOf.List) || ExprUsesStringConcat(listIndexOf.Value),
            IRListInsert listInsert => ExprUsesStringConcat(listInsert.List) || ExprUsesStringConcat(listInsert.Index) || ExprUsesStringConcat(listInsert.Value),
            IRListRemove listRemove => ExprUsesStringConcat(listRemove.List) || ExprUsesStringConcat(listRemove.Value),
            IRListRemoveAt listRemoveAt => ExprUsesStringConcat(listRemoveAt.List) || ExprUsesStringConcat(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesStringConcat(listReverse.List),
            IRListSort listSort => ExprUsesStringConcat(listSort.List) || (listSort.Comparer is not null && ExprUsesStringConcat(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesStringConcat(listToArray.List),
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
            IRIs isExpr => ExprUsesStringConcat(isExpr.Value),
            IRAs asExpr => ExprUsesStringConcat(asExpr.Value),
            _ => false,
        };

    private static bool ExprUsesDictionaryHelpers(IRExpr expr)
        => expr switch
        {
            IRDictionaryCount or IRDictionaryGet or IRDictionaryAdd or IRDictionarySet or IRDictionaryRemove or IRDictionaryContainsKey or IRDictionaryClear or IRDictionaryKeys or IRDictionaryValues => true,
            IRDictionaryNew => true,
            IRListNew listNew => listNew.Items.Any(ExprUsesDictionaryHelpers),
            IRListCount listCount => ExprUsesDictionaryHelpers(listCount.List),
            IRListGet listGet => ExprUsesDictionaryHelpers(listGet.List) || ExprUsesDictionaryHelpers(listGet.Index),
            IRListSet listSet => ExprUsesDictionaryHelpers(listSet.List) || ExprUsesDictionaryHelpers(listSet.Index) || ExprUsesDictionaryHelpers(listSet.Value),
            IRListAdd listAdd => ExprUsesDictionaryHelpers(listAdd.List) || ExprUsesDictionaryHelpers(listAdd.Value),
            IRListAddRange listAddRange => ExprUsesDictionaryHelpers(listAddRange.List) || ExprUsesDictionaryHelpers(listAddRange.Items),
            IRListClear listClear => ExprUsesDictionaryHelpers(listClear.List),
            IRListContains listContains => ExprUsesDictionaryHelpers(listContains.List) || ExprUsesDictionaryHelpers(listContains.Value),
            IRListIndexOf listIndexOf => ExprUsesDictionaryHelpers(listIndexOf.List) || ExprUsesDictionaryHelpers(listIndexOf.Value),
            IRListInsert listInsert => ExprUsesDictionaryHelpers(listInsert.List) || ExprUsesDictionaryHelpers(listInsert.Index) || ExprUsesDictionaryHelpers(listInsert.Value),
            IRListRemove listRemove => ExprUsesDictionaryHelpers(listRemove.List) || ExprUsesDictionaryHelpers(listRemove.Value),
            IRListRemoveAt listRemoveAt => ExprUsesDictionaryHelpers(listRemoveAt.List) || ExprUsesDictionaryHelpers(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesDictionaryHelpers(listReverse.List),
            IRListSort listSort => ExprUsesDictionaryHelpers(listSort.List) || (listSort.Comparer is not null && ExprUsesDictionaryHelpers(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesDictionaryHelpers(listToArray.List),
            IRLuaRequire luaRequire => ExprUsesDictionaryHelpers(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesDictionaryHelpers(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesDictionaryHelpers(luaAccess.Target) || ExprUsesDictionaryHelpers(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesDictionaryHelpers(luaMethodInvocation.Target) || ExprUsesDictionaryHelpers(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesDictionaryHelpers),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesDictionaryHelpers),
            IRMemberAccess member => ExprUsesDictionaryHelpers(member.Target),
            IRElementAccess element => ExprUsesDictionaryHelpers(element.Target) || ExprUsesDictionaryHelpers(element.Index),
            IRLength length => ExprUsesDictionaryHelpers(length.Target),
            IRInvocation invocation => ExprUsesDictionaryHelpers(invocation.Callee) || invocation.Arguments.Any(ExprUsesDictionaryHelpers),
            IRFunctionExpression functionExpression => BlockUsesDictionaryHelpers(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesDictionaryHelpers),
            IRArrayNew arrayNew => ExprUsesDictionaryHelpers(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesDictionaryHelpers),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesDictionaryHelpers(f.Value)),
            IRBinary binary => ExprUsesDictionaryHelpers(binary.Left) || ExprUsesDictionaryHelpers(binary.Right),
            IRUnary unary => ExprUsesDictionaryHelpers(unary.Operand),
            IRIs isExpr => ExprUsesDictionaryHelpers(isExpr.Value),
            IRAs asExpr => ExprUsesDictionaryHelpers(asExpr.Value),
            _ => false,
        };

    private static bool ExprUsesListHelpers(IRExpr expr)
        => expr switch
        {
            IRListNew or IRListCount or IRListGet or IRListSet or IRListAdd or IRListAddRange or IRListClear or IRListContains or IRListIndexOf or IRListInsert or IRListRemove or IRListRemoveAt or IRListReverse or IRListSort or IRListToArray => true,
            IRMemberAccess member => ExprUsesListHelpers(member.Target),
            IRElementAccess element => ExprUsesListHelpers(element.Target) || ExprUsesListHelpers(element.Index),
            IRLength length => ExprUsesListHelpers(length.Target),
            IRInvocation invocation => ExprUsesListHelpers(invocation.Callee) || invocation.Arguments.Any(ExprUsesListHelpers),
            IRFunctionExpression functionExpression => BlockUsesListHelpers(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesListHelpers),
            IRArrayNew arrayNew => ExprUsesListHelpers(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesListHelpers),
            IRDictionaryCount dictionaryCount => ExprUsesListHelpers(dictionaryCount.Table),
            IRDictionaryGet dictionaryGet => ExprUsesListHelpers(dictionaryGet.Table) || ExprUsesListHelpers(dictionaryGet.Key),
            IRDictionaryAdd dictionaryAdd => ExprUsesListHelpers(dictionaryAdd.Table) || ExprUsesListHelpers(dictionaryAdd.Key) || ExprUsesListHelpers(dictionaryAdd.Value),
            IRDictionarySet dictionarySet => ExprUsesListHelpers(dictionarySet.Table) || ExprUsesListHelpers(dictionarySet.Key) || ExprUsesListHelpers(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesListHelpers(dictionaryRemove.Table) || ExprUsesListHelpers(dictionaryRemove.Key),
            IRDictionaryContainsKey dictionaryContainsKey => ExprUsesListHelpers(dictionaryContainsKey.Table) || ExprUsesListHelpers(dictionaryContainsKey.Key),
            IRDictionaryClear dictionaryClear => ExprUsesListHelpers(dictionaryClear.Table),
            IRDictionaryKeys => true,
            IRDictionaryValues => true,
            IRLuaRequire luaRequire => ExprUsesListHelpers(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesListHelpers(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesListHelpers(luaAccess.Target) || ExprUsesListHelpers(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesListHelpers(luaMethodInvocation.Target) || ExprUsesListHelpers(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesListHelpers),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesListHelpers),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesListHelpers(f.Value)),
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
            IRFunctionExpression functionExpression => BlockUsesCoroutineHelpers(functionExpression.Body),
            IRArrayLiteral array => array.Items.Any(ExprUsesCoroutineHelpers),
            IRArrayNew arrayNew => ExprUsesCoroutineHelpers(arrayNew.Size),
            IRStringConcat concat => concat.Parts.Any(ExprUsesCoroutineHelpers),
            IRDictionaryCount dictionaryCount => ExprUsesCoroutineHelpers(dictionaryCount.Table),
            IRDictionaryGet dictionaryGet => ExprUsesCoroutineHelpers(dictionaryGet.Table) || ExprUsesCoroutineHelpers(dictionaryGet.Key),
            IRDictionaryAdd dictionaryAdd => ExprUsesCoroutineHelpers(dictionaryAdd.Table) || ExprUsesCoroutineHelpers(dictionaryAdd.Key) || ExprUsesCoroutineHelpers(dictionaryAdd.Value),
            IRDictionarySet dictionarySet => ExprUsesCoroutineHelpers(dictionarySet.Table) || ExprUsesCoroutineHelpers(dictionarySet.Key) || ExprUsesCoroutineHelpers(dictionarySet.Value),
            IRDictionaryRemove dictionaryRemove => ExprUsesCoroutineHelpers(dictionaryRemove.Table) || ExprUsesCoroutineHelpers(dictionaryRemove.Key),
            IRDictionaryContainsKey dictionaryContainsKey => ExprUsesCoroutineHelpers(dictionaryContainsKey.Table) || ExprUsesCoroutineHelpers(dictionaryContainsKey.Key),
            IRDictionaryClear dictionaryClear => ExprUsesCoroutineHelpers(dictionaryClear.Table),
            IRDictionaryKeys dictionaryKeys => ExprUsesCoroutineHelpers(dictionaryKeys.Table),
            IRDictionaryValues dictionaryValues => ExprUsesCoroutineHelpers(dictionaryValues.Table),
            IRListNew listNew => listNew.Items.Any(ExprUsesCoroutineHelpers),
            IRListCount listCount => ExprUsesCoroutineHelpers(listCount.List),
            IRListGet listGet => ExprUsesCoroutineHelpers(listGet.List) || ExprUsesCoroutineHelpers(listGet.Index),
            IRListSet listSet => ExprUsesCoroutineHelpers(listSet.List) || ExprUsesCoroutineHelpers(listSet.Index) || ExprUsesCoroutineHelpers(listSet.Value),
            IRListAdd listAdd => ExprUsesCoroutineHelpers(listAdd.List) || ExprUsesCoroutineHelpers(listAdd.Value),
            IRListAddRange listAddRange => ExprUsesCoroutineHelpers(listAddRange.List) || ExprUsesCoroutineHelpers(listAddRange.Items),
            IRListClear listClear => ExprUsesCoroutineHelpers(listClear.List),
            IRListContains listContains => ExprUsesCoroutineHelpers(listContains.List) || ExprUsesCoroutineHelpers(listContains.Value),
            IRListIndexOf listIndexOf => ExprUsesCoroutineHelpers(listIndexOf.List) || ExprUsesCoroutineHelpers(listIndexOf.Value),
            IRListInsert listInsert => ExprUsesCoroutineHelpers(listInsert.List) || ExprUsesCoroutineHelpers(listInsert.Index) || ExprUsesCoroutineHelpers(listInsert.Value),
            IRListRemove listRemove => ExprUsesCoroutineHelpers(listRemove.List) || ExprUsesCoroutineHelpers(listRemove.Value),
            IRListRemoveAt listRemoveAt => ExprUsesCoroutineHelpers(listRemoveAt.List) || ExprUsesCoroutineHelpers(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesCoroutineHelpers(listReverse.List),
            IRListSort listSort => ExprUsesCoroutineHelpers(listSort.List) || (listSort.Comparer is not null && ExprUsesCoroutineHelpers(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesCoroutineHelpers(listToArray.List),
            IRLuaRequire luaRequire => ExprUsesCoroutineHelpers(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesCoroutineHelpers(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesCoroutineHelpers(luaAccess.Target) || ExprUsesCoroutineHelpers(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesCoroutineHelpers(luaMethodInvocation.Target) || ExprUsesCoroutineHelpers(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesCoroutineHelpers),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesCoroutineHelpers(f.Value)),
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
            case IRDictionaryCount dictionaryCount:
                CollectIdentifiers(dictionaryCount.Table);
                break;
            case IRDictionaryGet dictionaryGet:
                CollectIdentifiers(dictionaryGet.Table);
                CollectIdentifiers(dictionaryGet.Key);
                break;
            case IRDictionaryAdd dictionaryAdd:
                CollectIdentifiers(dictionaryAdd.Table);
                CollectIdentifiers(dictionaryAdd.Key);
                CollectIdentifiers(dictionaryAdd.Value);
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
            case IRDictionaryContainsKey dictionaryContainsKey:
                CollectIdentifiers(dictionaryContainsKey.Table);
                CollectIdentifiers(dictionaryContainsKey.Key);
                break;
            case IRDictionaryClear dictionaryClear:
                CollectIdentifiers(dictionaryClear.Table);
                break;
            case IRDictionaryKeys dictionaryKeys:
                CollectIdentifiers(dictionaryKeys.Table);
                break;
            case IRDictionaryValues dictionaryValues:
                CollectIdentifiers(dictionaryValues.Table);
                break;
            case IRListNew listNew:
                foreach (var item in listNew.Items) CollectIdentifiers(item);
                break;
            case IRListCount listCount:
                CollectIdentifiers(listCount.List);
                break;
            case IRListGet listGet:
                CollectIdentifiers(listGet.List);
                CollectIdentifiers(listGet.Index);
                break;
            case IRListSet listSet:
                CollectIdentifiers(listSet.List);
                CollectIdentifiers(listSet.Index);
                CollectIdentifiers(listSet.Value);
                break;
            case IRListAdd listAdd:
                CollectIdentifiers(listAdd.List);
                CollectIdentifiers(listAdd.Value);
                break;
            case IRListAddRange listAddRange:
                CollectIdentifiers(listAddRange.List);
                CollectIdentifiers(listAddRange.Items);
                break;
            case IRListClear listClear:
                CollectIdentifiers(listClear.List);
                break;
            case IRListContains listContains:
                CollectIdentifiers(listContains.List);
                CollectIdentifiers(listContains.Value);
                break;
            case IRListIndexOf listIndexOf:
                CollectIdentifiers(listIndexOf.List);
                CollectIdentifiers(listIndexOf.Value);
                break;
            case IRListInsert listInsert:
                CollectIdentifiers(listInsert.List);
                CollectIdentifiers(listInsert.Index);
                CollectIdentifiers(listInsert.Value);
                break;
            case IRListRemove listRemove:
                CollectIdentifiers(listRemove.List);
                CollectIdentifiers(listRemove.Value);
                break;
            case IRListRemoveAt listRemoveAt:
                CollectIdentifiers(listRemoveAt.List);
                CollectIdentifiers(listRemoveAt.Index);
                break;
            case IRListReverse listReverse:
                CollectIdentifiers(listReverse.List);
                break;
            case IRListSort listSort:
                CollectIdentifiers(listSort.List);
                if (listSort.Comparer is not null) CollectIdentifiers(listSort.Comparer);
                break;
            case IRListToArray listToArray:
                CollectIdentifiers(listToArray.List);
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
