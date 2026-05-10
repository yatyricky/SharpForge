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
    private readonly HashSet<DictionaryHelper> _dictionaryHelpers = [];
    private readonly HashSet<ListHelper> _listHelpers = [];
    private readonly HashSet<HashSetHelper> _hashSetHelpers = [];
    private int _indent;
    private int _tempId;
    private bool _emitTypeHelpers;
    private bool _emitStringConcatHelper;
    private bool _emitCoroutineHelpers;
    private bool _emitTernaryHelper;

    private enum DictionaryHelper
    {
        Nil,
        New,
        Count,
        Get,
        Set,
        Add,
        Remove,
        ContainsKey,
        Clear,
        Keys,
        Values,
        Iterate,
        LinearNew,
        LinearFind,
        LinearCount,
        LinearGet,
        LinearSet,
        LinearAdd,
        LinearRemove,
        LinearContainsKey,
        LinearClear,
        LinearKeys,
        LinearValues,
        LinearIterate,
    }

    private enum ListHelper
    {
        Nil,
        Wrap,
        Unwrap,
        New,
        Count,
        Get,
        Set,
        Add,
        AddRange,
        Clear,
        IndexOf,
        Contains,
        Insert,
        RemoveAt,
        Remove,
        Reverse,
        Iterate,
        Sort,
        ToArray,
        QueueDequeue,
        QueuePeek,
        StackPop,
        StackPeek,
        StackIterate,
        StackToArray,
    }

    private enum HashSetHelper
    {
        New,
        Count,
        Add,
        Remove,
        Contains,
        Clear,
        ToArray,
        Iterate,
        LinearNew,
        LinearFind,
        LinearCount,
        LinearAdd,
        LinearRemove,
        LinearContains,
        LinearClear,
        LinearToArray,
        LinearIterate,
    }

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
        _emitTernaryHelper = UsesTernaryHelper(module);
        _dictionaryHelpers.Clear();
        _listHelpers.Clear();
        _hashSetHelpers.Clear();
        CollectCollectionHelpers(module);
        AddCollectionHelperDependencies();
        _usedIdentifiers.Clear();
        CollectIdentifiers(module);

        EnsureRootEmitted();

        foreach (var enumType in module.Enums)
        {
            EmitEnum(enumType);
        }

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
            if (_dictionaryHelpers.Count > 0)
            {
                WriteDictionaryHelpers();
            }
            if (_listHelpers.Count > 0)
            {
                WriteListHelpers();
            }
            if (_hashSetHelpers.Count > 0)
            {
                WriteHashSetHelpers();
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
        if (_dictionaryHelpers.Contains(DictionaryHelper.Nil)) WriteLine($"{_rootTable}.DictNil__ = {_rootTable}.DictNil__ or {{}}");
        if (_dictionaryHelpers.Contains(DictionaryHelper.New)) WriteDictionaryNewHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Count)) WriteDictionaryCountHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Get)) WriteDictionaryGetHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Set)) WriteDictionarySetHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Add)) WriteDictionaryAddHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Remove)) WriteDictionaryRemoveHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.ContainsKey)) WriteDictionaryContainsKeyHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Clear)) WriteDictionaryClearHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Keys)) WriteDictionaryKeysHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Values)) WriteDictionaryValuesHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.Iterate)) WriteDictionaryIterateHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearNew)) WriteDictionaryLinearNewHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearFind)) WriteDictionaryLinearFindHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearCount)) WriteDictionaryLinearCountHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearGet)) WriteDictionaryLinearGetHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearSet)) WriteDictionaryLinearSetHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearAdd)) WriteDictionaryLinearAddHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearRemove)) WriteDictionaryLinearRemoveHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearContainsKey)) WriteDictionaryLinearContainsKeyHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearClear)) WriteDictionaryLinearClearHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearKeys)) WriteDictionaryLinearKeysHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearValues)) WriteDictionaryLinearValuesHelper();
        if (_dictionaryHelpers.Contains(DictionaryHelper.LinearIterate)) WriteDictionaryLinearIterateHelper();
    }

    private void WriteDictionaryNewHelper()
    {
        WriteLine($"function {_rootTable}.DictNew__()");
        _indent++;
        WriteLine("return { data = {}, keys = {}, version = 0 }");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryCountHelper()
    {
        WriteLine($"function {_rootTable}.DictCount__(dict)");
        _indent++;
        WriteLine("return #dict.keys");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryGetHelper()
    {
        WriteLine($"function {_rootTable}.DictGet__(dict, key)");
        _indent++;
        WriteLine("local value = dict.data[key]");
        WriteLine($"if value == {_rootTable}.DictNil__ then return nil end");
        WriteLine("return value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionarySetHelper()
    {
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
    }

    private void WriteDictionaryAddHelper()
    {
        WriteLine($"function {_rootTable}.DictAdd__(dict, key, value)");
        _indent++;
        WriteLine("if dict.data[key] ~= nil then error(\"duplicate key\") end");
        WriteLine("table.insert(dict.keys, key)");
        WriteLine($"dict.data[key] = value == nil and {_rootTable}.DictNil__ or value");
        WriteLine("dict.version = dict.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryRemoveHelper()
    {
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
    }

    private void WriteDictionaryContainsKeyHelper()
    {
        WriteLine($"function {_rootTable}.DictContainsKey__(dict, key)");
        _indent++;
        WriteLine("return dict.data[key] ~= nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryClearHelper()
    {
        WriteLine($"function {_rootTable}.DictClear__(dict)");
        _indent++;
        WriteLine("dict.data = {}");
        WriteLine("dict.keys = {}");
        WriteLine("dict.version = dict.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryKeysHelper()
    {
        WriteLine($"function {_rootTable}.DictKeys__(dict)");
        _indent++;
        WriteLine("local items = {}");
        WriteLine("for i, key in ipairs(dict.keys) do items[i] = key end");
        WriteLine($"return {_rootTable}.ListNew__(items)");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryValuesHelper()
    {
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
    }

    private void WriteDictionaryIterateHelper()
    {
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

    private void WriteDictionaryLinearNewHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearNew__(keyEquals)");
        _indent++;
        WriteLine("return { keys = {}, values = {}, version = 0, keyEquals = keyEquals }");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearFindHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearFind__(dict, key)");
        _indent++;
        WriteLine("for i, storedKey in ipairs(dict.keys) do");
        _indent++;
        WriteLine("if dict.keyEquals(storedKey, key) then return i end");
        _indent--;
        WriteLine("end");
        WriteLine("return nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearCountHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearCount__(dict)");
        _indent++;
        WriteLine("return #dict.keys");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearGetHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearGet__(dict, key)");
        _indent++;
        WriteLine($"local index = {_rootTable}.DictLinearFind__(dict, key)");
        WriteLine("if index == nil then return nil end");
        WriteLine("local value = dict.values[index]");
        WriteLine($"if value == {_rootTable}.DictNil__ then return nil end");
        WriteLine("return value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearSetHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearSet__(dict, key, value)");
        _indent++;
        WriteLine($"local index = {_rootTable}.DictLinearFind__(dict, key)");
        WriteLine("if index == nil then");
        _indent++;
        WriteLine("table.insert(dict.keys, key)");
        WriteLine($"table.insert(dict.values, value == nil and {_rootTable}.DictNil__ or value)");
        _indent--;
        WriteLine("else");
        _indent++;
        WriteLine($"dict.values[index] = value == nil and {_rootTable}.DictNil__ or value");
        _indent--;
        WriteLine("end");
        WriteLine("dict.version = dict.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearAddHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearAdd__(dict, key, value)");
        _indent++;
        WriteLine($"if {_rootTable}.DictLinearFind__(dict, key) ~= nil then error(\"duplicate key\") end");
        WriteLine("table.insert(dict.keys, key)");
        WriteLine($"table.insert(dict.values, value == nil and {_rootTable}.DictNil__ or value)");
        WriteLine("dict.version = dict.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearRemoveHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearRemove__(dict, key)");
        _indent++;
        WriteLine($"local index = {_rootTable}.DictLinearFind__(dict, key)");
        WriteLine("if index ~= nil then");
        _indent++;
        WriteLine("table.remove(dict.keys, index)");
        WriteLine("table.remove(dict.values, index)");
        WriteLine("dict.version = dict.version + 1");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        WriteLine("return false");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearContainsKeyHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearContainsKey__(dict, key)");
        _indent++;
        WriteLine($"return {_rootTable}.DictLinearFind__(dict, key) ~= nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearClearHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearClear__(dict)");
        _indent++;
        WriteLine("dict.keys = {}");
        WriteLine("dict.values = {}");
        WriteLine("dict.version = dict.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearKeysHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearKeys__(dict)");
        _indent++;
        WriteLine($"return {_rootTable}.ListNew__(dict.keys)");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearValuesHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearValues__(dict)");
        _indent++;
        WriteLine($"local list = {_rootTable}.ListNew__({{}})");
        WriteLine("for i, value in ipairs(dict.values) do");
        _indent++;
        WriteLine($"if value == {_rootTable}.DictNil__ then value = nil end");
        WriteLine($"list.items[i] = {_rootTable}.ListWrap__(value)");
        _indent--;
        WriteLine("end");
        WriteLine("return list");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteDictionaryLinearIterateHelper()
    {
        WriteLine($"function {_rootTable}.DictLinearIterate__(dict)");
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
        WriteLine("local value = dict.values[i]");
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
        if (_listHelpers.Contains(ListHelper.Nil)) WriteLine($"{_rootTable}.ListNil__ = {_rootTable}.ListNil__ or {{}}");
        if (_listHelpers.Contains(ListHelper.Wrap)) WriteListWrapHelper();
        if (_listHelpers.Contains(ListHelper.Unwrap)) WriteListUnwrapHelper();
        if (_listHelpers.Contains(ListHelper.New)) WriteListNewHelper();
        if (_listHelpers.Contains(ListHelper.Count)) WriteListCountHelper();
        if (_listHelpers.Contains(ListHelper.Get)) WriteListGetHelper();
        if (_listHelpers.Contains(ListHelper.Set)) WriteListSetHelper();
        if (_listHelpers.Contains(ListHelper.Add)) WriteListAddHelper();
        if (_listHelpers.Contains(ListHelper.AddRange)) WriteListAddRangeHelper();
        if (_listHelpers.Contains(ListHelper.Clear)) WriteListClearHelper();
        if (_listHelpers.Contains(ListHelper.IndexOf)) WriteListIndexOfHelper();
        if (_listHelpers.Contains(ListHelper.Contains)) WriteListContainsHelper();
        if (_listHelpers.Contains(ListHelper.Insert)) WriteListInsertHelper();
        if (_listHelpers.Contains(ListHelper.RemoveAt)) WriteListRemoveAtHelper();
        if (_listHelpers.Contains(ListHelper.Remove)) WriteListRemoveHelper();
        if (_listHelpers.Contains(ListHelper.Reverse)) WriteListReverseHelper();
        if (_listHelpers.Contains(ListHelper.Iterate)) WriteListIterateHelper();
        if (_listHelpers.Contains(ListHelper.Sort)) WriteListSortHelper();
        if (_listHelpers.Contains(ListHelper.ToArray)) WriteListToArrayHelper();
        if (_listHelpers.Contains(ListHelper.QueueDequeue)) WriteQueueDequeueHelper();
        if (_listHelpers.Contains(ListHelper.QueuePeek)) WriteQueuePeekHelper();
        if (_listHelpers.Contains(ListHelper.StackPop)) WriteStackPopHelper();
        if (_listHelpers.Contains(ListHelper.StackPeek)) WriteStackPeekHelper();
        if (_listHelpers.Contains(ListHelper.StackIterate)) WriteStackIterateHelper();
        if (_listHelpers.Contains(ListHelper.StackToArray)) WriteStackToArrayHelper();
    }

    private void WriteListWrapHelper()
    {
        WriteLine($"function {_rootTable}.ListWrap__(value)");
        _indent++;
        WriteLine($"return value == nil and {_rootTable}.ListNil__ or value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListUnwrapHelper()
    {
        WriteLine($"function {_rootTable}.ListUnwrap__(value)");
        _indent++;
        WriteLine($"if value == {_rootTable}.ListNil__ then return nil end");
        WriteLine("return value");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListNewHelper()
    {
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
    }

    private void WriteListCountHelper()
    {
        WriteLine($"function {_rootTable}.ListCount__(list)");
        _indent++;
        WriteLine("return #list.items");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListGetHelper()
    {
        WriteLine($"function {_rootTable}.ListGet__(list, index)");
        _indent++;
        WriteLine($"return {_rootTable}.ListUnwrap__(list.items[index + 1])");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListSetHelper()
    {
        WriteLine($"function {_rootTable}.ListSet__(list, index, value)");
        _indent++;
        WriteLine($"list.items[index + 1] = {_rootTable}.ListWrap__(value)");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListAddHelper()
    {
        WriteLine($"function {_rootTable}.ListAdd__(list, value)");
        _indent++;
        WriteLine($"table.insert(list.items, {_rootTable}.ListWrap__(value))");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListAddRangeHelper()
    {
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
    }

    private void WriteListClearHelper()
    {
        WriteLine($"function {_rootTable}.ListClear__(list)");
        _indent++;
        WriteLine("list.items = {}");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListIndexOfHelper()
    {
        WriteLine($"function {_rootTable}.ListIndexOf__(list, value, equals)");
        _indent++;
        WriteLine($"local stored = {_rootTable}.ListWrap__(value)");
        WriteLine("for i, item in ipairs(list.items) do");
        _indent++;
        WriteLine("if equals ~= nil then");
        _indent++;
        WriteLine($"if equals({_rootTable}.ListUnwrap__(item), value) then return i - 1 end");
        _indent--;
        WriteLine("else");
        _indent++;
        WriteLine("if item == stored then return i - 1 end");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        WriteLine("return -1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListContainsHelper()
    {
        WriteLine($"function {_rootTable}.ListContains__(list, value, equals)");
        _indent++;
        WriteLine($"return {_rootTable}.ListIndexOf__(list, value, equals) >= 0");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListInsertHelper()
    {
        WriteLine($"function {_rootTable}.ListInsert__(list, index, value)");
        _indent++;
        WriteLine($"table.insert(list.items, index + 1, {_rootTable}.ListWrap__(value))");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListRemoveAtHelper()
    {
        WriteLine($"function {_rootTable}.ListRemoveAt__(list, index)");
        _indent++;
        WriteLine("table.remove(list.items, index + 1)");
        WriteLine("list.version = list.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteListRemoveHelper()
    {
        WriteLine($"function {_rootTable}.ListRemove__(list, value, equals)");
        _indent++;
        WriteLine($"local index = {_rootTable}.ListIndexOf__(list, value, equals)");
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
    }

    private void WriteListReverseHelper()
    {
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
    }

    private void WriteListIterateHelper()
    {
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
    }

    private void WriteListSortHelper()
    {
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
    }

    private void WriteListToArrayHelper()
    {
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

    private void WriteQueueDequeueHelper()
    {
        WriteLine($"function {_rootTable}.QueueDequeue__(queue)");
        _indent++;
        WriteLine("if #queue.items == 0 then error(\"queue is empty\") end");
        WriteLine("local value = queue.items[1]");
        WriteLine("table.remove(queue.items, 1)");
        WriteLine("queue.version = queue.version + 1");
        WriteLine($"return {_rootTable}.ListUnwrap__(value)");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteQueuePeekHelper()
    {
        WriteLine($"function {_rootTable}.QueuePeek__(queue)");
        _indent++;
        WriteLine("if #queue.items == 0 then error(\"queue is empty\") end");
        WriteLine($"return {_rootTable}.ListUnwrap__(queue.items[1])");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteStackPopHelper()
    {
        WriteLine($"function {_rootTable}.StackPop__(stack)");
        _indent++;
        WriteLine("local index = #stack.items");
        WriteLine("if index == 0 then error(\"stack is empty\") end");
        WriteLine("local value = stack.items[index]");
        WriteLine("stack.items[index] = nil");
        WriteLine("stack.version = stack.version + 1");
        WriteLine($"return {_rootTable}.ListUnwrap__(value)");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteStackPeekHelper()
    {
        WriteLine($"function {_rootTable}.StackPeek__(stack)");
        _indent++;
        WriteLine("local index = #stack.items");
        WriteLine("if index == 0 then error(\"stack is empty\") end");
        WriteLine($"return {_rootTable}.ListUnwrap__(stack.items[index])");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteStackIterateHelper()
    {
        WriteLine($"function {_rootTable}.StackIterate__(stack)");
        _indent++;
        WriteLine("local version = stack.version");
        WriteLine("local i = #stack.items + 1");
        WriteLine("return function()");
        _indent++;
        WriteLine("if stack.version ~= version then error(\"collection was modified during iteration\") end");
        WriteLine("i = i - 1");
        WriteLine("local value = stack.items[i]");
        WriteLine($"if value ~= nil then return i, {_rootTable}.ListUnwrap__(value) end");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteStackToArrayHelper()
    {
        WriteLine($"function {_rootTable}.StackToArray__(stack)");
        _indent++;
        WriteLine("local result = {}");
        WriteLine("local output = 1");
        WriteLine("for i = #stack.items, 1, -1 do");
        _indent++;
        WriteLine($"result[output] = {_rootTable}.ListUnwrap__(stack.items[i])");
        WriteLine("output = output + 1");
        _indent--;
        WriteLine("end");
        WriteLine("return result");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetHelpers()
    {
        if (_hashSetHelpers.Contains(HashSetHelper.New)) WriteHashSetNewHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.Count)) WriteHashSetCountHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.Add)) WriteHashSetAddHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.Remove)) WriteHashSetRemoveHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.Contains)) WriteHashSetContainsHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.Clear)) WriteHashSetClearHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.ToArray)) WriteHashSetToArrayHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.Iterate)) WriteHashSetIterateHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearNew)) WriteHashSetLinearNewHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearFind)) WriteHashSetLinearFindHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearCount)) WriteHashSetLinearCountHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearAdd)) WriteHashSetLinearAddHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearRemove)) WriteHashSetLinearRemoveHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearContains)) WriteHashSetLinearContainsHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearClear)) WriteHashSetLinearClearHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearToArray)) WriteHashSetLinearToArrayHelper();
        if (_hashSetHelpers.Contains(HashSetHelper.LinearIterate)) WriteHashSetLinearIterateHelper();
    }

    private void WriteHashSetNewHelper()
    {
        WriteLine($"function {_rootTable}.HashSetNew__()");
        _indent++;
        WriteLine("return { data = {}, keys = {}, version = 0 }");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetCountHelper()
    {
        WriteLine($"function {_rootTable}.HashSetCount__(set)");
        _indent++;
        WriteLine("return #set.keys");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetAddHelper()
    {
        WriteLine($"function {_rootTable}.HashSetAdd__(set, value)");
        _indent++;
        WriteLine("if set.data[value] ~= nil then return false end");
        WriteLine("set.data[value] = true");
        WriteLine("table.insert(set.keys, value)");
        WriteLine("set.version = set.version + 1");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetRemoveHelper()
    {
        WriteLine($"function {_rootTable}.HashSetRemove__(set, value)");
        _indent++;
        WriteLine("if set.data[value] == nil then return false end");
        WriteLine("set.data[value] = nil");
        WriteLine("for i, storedValue in ipairs(set.keys) do");
        _indent++;
        WriteLine("if storedValue == value then");
        _indent++;
        WriteLine("table.remove(set.keys, i)");
        WriteLine("break");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        WriteLine("set.version = set.version + 1");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetContainsHelper()
    {
        WriteLine($"function {_rootTable}.HashSetContains__(set, value)");
        _indent++;
        WriteLine("return set.data[value] ~= nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetClearHelper()
    {
        WriteLine($"function {_rootTable}.HashSetClear__(set)");
        _indent++;
        WriteLine("set.data = {}");
        WriteLine("set.keys = {}");
        WriteLine("set.version = set.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetToArrayHelper()
    {
        WriteLine($"function {_rootTable}.HashSetToArray__(set)");
        _indent++;
        WriteLine("local result = {}");
        WriteLine("for i, value in ipairs(set.keys) do result[i] = value end");
        WriteLine("return result");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetIterateHelper()
    {
        WriteLine($"function {_rootTable}.HashSetIterate__(set)");
        _indent++;
        WriteLine("local version = set.version");
        WriteLine("local i = 0");
        WriteLine("return function()");
        _indent++;
        WriteLine("if set.version ~= version then error(\"collection was modified during iteration\") end");
        WriteLine("i = i + 1");
        WriteLine("local value = set.keys[i]");
        WriteLine("if value ~= nil then return i, value end");
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearNewHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearNew__(equals)");
        _indent++;
        WriteLine("return { keys = {}, version = 0, equals = equals }");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearFindHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearFind__(set, value)");
        _indent++;
        WriteLine("for i, storedValue in ipairs(set.keys) do");
        _indent++;
        WriteLine("if set.equals(storedValue, value) then return i end");
        _indent--;
        WriteLine("end");
        WriteLine("return nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearCountHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearCount__(set)");
        _indent++;
        WriteLine("return #set.keys");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearAddHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearAdd__(set, value)");
        _indent++;
        WriteLine($"if {_rootTable}.HashSetLinearFind__(set, value) ~= nil then return false end");
        WriteLine("table.insert(set.keys, value)");
        WriteLine("set.version = set.version + 1");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearRemoveHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearRemove__(set, value)");
        _indent++;
        WriteLine($"local index = {_rootTable}.HashSetLinearFind__(set, value)");
        WriteLine("if index == nil then return false end");
        WriteLine("table.remove(set.keys, index)");
        WriteLine("set.version = set.version + 1");
        WriteLine("return true");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearContainsHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearContains__(set, value)");
        _indent++;
        WriteLine($"return {_rootTable}.HashSetLinearFind__(set, value) ~= nil");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearClearHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearClear__(set)");
        _indent++;
        WriteLine("set.keys = {}");
        WriteLine("set.version = set.version + 1");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearToArrayHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearToArray__(set)");
        _indent++;
        WriteLine("local result = {}");
        WriteLine("for i, value in ipairs(set.keys) do result[i] = value end");
        WriteLine("return result");
        _indent--;
        WriteLine("end");
        _sb.Append('\n');
    }

    private void WriteHashSetLinearIterateHelper()
    {
        WriteLine($"function {_rootTable}.HashSetLinearIterate__(set)");
        _indent++;
        WriteLine("local version = set.version");
        WriteLine("local i = 0");
        WriteLine("return function()");
        _indent++;
        WriteLine("if set.version ~= version then error(\"collection was modified during iteration\") end");
        WriteLine("i = i + 1");
        WriteLine("local value = set.keys[i]");
        WriteLine("if value ~= nil then return i, value end");
        _indent--;
        WriteLine("end");
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
            case IRStackForEach f:
                EmitStackForEach(f);
                break;
            case IRHashSetForEach f:
                EmitHashSetForEach(f);
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
        var iterator = f.UseLinearKeys ? $"{_rootTable}.DictLinearIterate__({dictionaryName})" : $"{_rootTable}.DictIterate__({dictionaryName})";
        WriteLine($"for {f.KeyName}, {f.ValueName} in {iterator} do");
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

    private void EmitStackForEach(IRStackForEach f)
    {
        var stackName = NewTemp("stack");
        var indexName = NewTemp("i");

        WriteLine("do");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(stackName).Append(" = ");
        EmitExpr(f.Stack);
        _sb.Append('\n');
        WriteLine($"for {indexName}, {f.ItemName} in {_rootTable}.StackIterate__({stackName}) do");
        _indent++;
        EmitBlock(f.Body);
        _indent--;
        WriteLine("end");
        _indent--;
        WriteLine("end");
    }

    private void EmitHashSetForEach(IRHashSetForEach f)
    {
        var setName = NewTemp("set");
        var indexName = NewTemp("i");

        WriteLine("do");
        _indent++;
        WriteIndent();
        _sb.Append("local ").Append(setName).Append(" = ");
        EmitExpr(f.Set);
        _sb.Append('\n');
        var iterator = f.UseLinearKeys ? $"{_rootTable}.HashSetLinearIterate__({setName})" : $"{_rootTable}.HashSetIterate__({setName})";
        WriteLine($"for {indexName}, {f.ItemName} in {iterator} do");
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
            case IRStructValueTable structValueTable:
                _sb.Append("(function(");
                for (int i = 0; i < structValueTable.Fields.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    _sb.Append("__sf_v").Append(i + 1);
                }
                _sb.Append(") return {");
                for (int i = 0; i < structValueTable.Fields.Count; i++)
                {
                    if (i > 0)
                    {
                        _sb.Append(", ");
                    }
                    _sb.Append(structValueTable.Fields[i]).Append(" = __sf_v").Append(i + 1);
                }
                _sb.Append("} end)(");
                EmitExpr(structValueTable.Value);
                _sb.Append(')');
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
            case IRDictionaryNew dictionaryNew:
                _sb.Append(_rootTable).Append(dictionaryNew.UseLinearKeys ? ".DictLinearNew__(" : ".DictNew__(");
                if (dictionaryNew.UseLinearKeys && dictionaryNew.KeyComparer is not null)
                {
                    EmitExpr(dictionaryNew.KeyComparer);
                }
                _sb.Append(')');
                break;
            case IRDictionaryCount dictionaryCount:
                _sb.Append(_rootTable).Append(dictionaryCount.UseLinearKeys ? ".DictLinearCount__(" : ".DictCount__(");
                EmitExpr(dictionaryCount.Table);
                _sb.Append(')');
                break;
            case IRDictionaryGet dictionaryGet:
                _sb.Append(_rootTable).Append(dictionaryGet.UseLinearKeys ? ".DictLinearGet__(" : ".DictGet__(");
                EmitExpr(dictionaryGet.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryGet.Key);
                _sb.Append(')');
                break;
            case IRDictionaryAdd dictionaryAdd:
                _sb.Append(_rootTable).Append(dictionaryAdd.UseLinearKeys ? ".DictLinearAdd__(" : ".DictAdd__(");
                EmitExpr(dictionaryAdd.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryAdd.Key);
                _sb.Append(", ");
                EmitExpr(dictionaryAdd.Value);
                _sb.Append(')');
                break;
            case IRDictionarySet dictionarySet:
                _sb.Append(_rootTable).Append(dictionarySet.UseLinearKeys ? ".DictLinearSet__(" : ".DictSet__(");
                EmitExpr(dictionarySet.Table);
                _sb.Append(", ");
                EmitExpr(dictionarySet.Key);
                _sb.Append(", ");
                EmitExpr(dictionarySet.Value);
                _sb.Append(')');
                break;
            case IRDictionaryRemove dictionaryRemove:
                _sb.Append(_rootTable).Append(dictionaryRemove.UseLinearKeys ? ".DictLinearRemove__(" : ".DictRemove__(");
                EmitExpr(dictionaryRemove.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryRemove.Key);
                _sb.Append(')');
                break;
            case IRDictionaryContainsKey dictionaryContainsKey:
                _sb.Append(_rootTable).Append(dictionaryContainsKey.UseLinearKeys ? ".DictLinearContainsKey__(" : ".DictContainsKey__(");
                EmitExpr(dictionaryContainsKey.Table);
                _sb.Append(", ");
                EmitExpr(dictionaryContainsKey.Key);
                _sb.Append(')');
                break;
            case IRDictionaryClear dictionaryClear:
                _sb.Append(_rootTable).Append(dictionaryClear.UseLinearKeys ? ".DictLinearClear__(" : ".DictClear__(");
                EmitExpr(dictionaryClear.Table);
                _sb.Append(')');
                break;
            case IRDictionaryKeys dictionaryKeys:
                _sb.Append(_rootTable).Append(dictionaryKeys.UseLinearKeys ? ".DictLinearKeys__(" : ".DictKeys__(");
                EmitExpr(dictionaryKeys.Table);
                _sb.Append(')');
                break;
            case IRDictionaryValues dictionaryValues:
                _sb.Append(_rootTable).Append(dictionaryValues.UseLinearKeys ? ".DictLinearValues__(" : ".DictValues__(");
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
                if (listContains.EqualityComparer is not null)
                {
                    _sb.Append(", ");
                    EmitExpr(listContains.EqualityComparer);
                }
                _sb.Append(')');
                break;
            case IRListIndexOf listIndexOf:
                _sb.Append(_rootTable).Append(".ListIndexOf__(");
                EmitExpr(listIndexOf.List);
                _sb.Append(", ");
                EmitExpr(listIndexOf.Value);
                if (listIndexOf.EqualityComparer is not null)
                {
                    _sb.Append(", ");
                    EmitExpr(listIndexOf.EqualityComparer);
                }
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
                if (listRemove.EqualityComparer is not null)
                {
                    _sb.Append(", ");
                    EmitExpr(listRemove.EqualityComparer);
                }
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
            case IRQueueDequeue queueDequeue:
                _sb.Append(_rootTable).Append(".QueueDequeue__(");
                EmitExpr(queueDequeue.Queue);
                _sb.Append(')');
                break;
            case IRQueuePeek queuePeek:
                _sb.Append(_rootTable).Append(".QueuePeek__(");
                EmitExpr(queuePeek.Queue);
                _sb.Append(')');
                break;
            case IRStackPop stackPop:
                _sb.Append(_rootTable).Append(".StackPop__(");
                EmitExpr(stackPop.Stack);
                _sb.Append(')');
                break;
            case IRStackPeek stackPeek:
                _sb.Append(_rootTable).Append(".StackPeek__(");
                EmitExpr(stackPeek.Stack);
                _sb.Append(')');
                break;
            case IRStackToArray stackToArray:
                _sb.Append(_rootTable).Append(".StackToArray__(");
                EmitExpr(stackToArray.Stack);
                _sb.Append(')');
                break;
            case IRHashSetNew hashSetNew:
                _sb.Append(_rootTable).Append(hashSetNew.UseLinearKeys ? ".HashSetLinearNew__(" : ".HashSetNew__(");
                if (hashSetNew.UseLinearKeys && hashSetNew.KeyComparer is not null)
                {
                    EmitExpr(hashSetNew.KeyComparer);
                }
                _sb.Append(')');
                break;
            case IRHashSetCount hashSetCount:
                _sb.Append(_rootTable).Append(hashSetCount.UseLinearKeys ? ".HashSetLinearCount__(" : ".HashSetCount__(");
                EmitExpr(hashSetCount.Set);
                _sb.Append(')');
                break;
            case IRHashSetAdd hashSetAdd:
                _sb.Append(_rootTable).Append(hashSetAdd.UseLinearKeys ? ".HashSetLinearAdd__(" : ".HashSetAdd__(");
                EmitExpr(hashSetAdd.Set);
                _sb.Append(", ");
                EmitExpr(hashSetAdd.Value);
                _sb.Append(')');
                break;
            case IRHashSetRemove hashSetRemove:
                _sb.Append(_rootTable).Append(hashSetRemove.UseLinearKeys ? ".HashSetLinearRemove__(" : ".HashSetRemove__(");
                EmitExpr(hashSetRemove.Set);
                _sb.Append(", ");
                EmitExpr(hashSetRemove.Value);
                _sb.Append(')');
                break;
            case IRHashSetContains hashSetContains:
                _sb.Append(_rootTable).Append(hashSetContains.UseLinearKeys ? ".HashSetLinearContains__(" : ".HashSetContains__(");
                EmitExpr(hashSetContains.Set);
                _sb.Append(", ");
                EmitExpr(hashSetContains.Value);
                _sb.Append(')');
                break;
            case IRHashSetClear hashSetClear:
                _sb.Append(_rootTable).Append(hashSetClear.UseLinearKeys ? ".HashSetLinearClear__(" : ".HashSetClear__(");
                EmitExpr(hashSetClear.Set);
                _sb.Append(')');
                break;
            case IRHashSetToArray hashSetToArray:
                _sb.Append(_rootTable).Append(hashSetToArray.UseLinearKeys ? ".HashSetLinearToArray__(" : ".HashSetToArray__(");
                EmitExpr(hashSetToArray.Set);
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

    private static bool UsesCoroutineHelpers(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => m.IsCoroutine || BlockUsesCoroutineHelpers(m.Body));

    private static bool UsesTernaryHelper(IRModule module)
        => module.Types.SelectMany(t => t.Methods).Any(m => BlockUsesTernaryHelper(m.Body));

    private void CollectCollectionHelpers(IRModule module)
    {
        foreach (var method in module.Types.SelectMany(t => t.Methods))
        {
            CollectCollectionHelpers(method.Body);
        }
    }

    private void AddCollectionHelperDependencies()
    {
        var changed = true;
        while (changed)
        {
            changed = false;

            if (_listHelpers.Contains(ListHelper.Wrap)) changed |= _listHelpers.Add(ListHelper.Nil);
            if (_listHelpers.Contains(ListHelper.Unwrap)) changed |= _listHelpers.Add(ListHelper.Nil);
            if (_listHelpers.Contains(ListHelper.New)) changed |= _listHelpers.Add(ListHelper.Wrap);
            if (_listHelpers.Contains(ListHelper.Get)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.Set)) changed |= _listHelpers.Add(ListHelper.Wrap);
            if (_listHelpers.Contains(ListHelper.Add)) changed |= _listHelpers.Add(ListHelper.Wrap);
            if (_listHelpers.Contains(ListHelper.AddRange)) changed |= _listHelpers.Add(ListHelper.Wrap);
            if (_listHelpers.Contains(ListHelper.IndexOf))
            {
                changed |= _listHelpers.Add(ListHelper.Wrap);
                changed |= _listHelpers.Add(ListHelper.Unwrap);
            }
            if (_listHelpers.Contains(ListHelper.Contains)) changed |= _listHelpers.Add(ListHelper.IndexOf);
            if (_listHelpers.Contains(ListHelper.Insert)) changed |= _listHelpers.Add(ListHelper.Wrap);
            if (_listHelpers.Contains(ListHelper.Remove))
            {
                changed |= _listHelpers.Add(ListHelper.IndexOf);
                changed |= _listHelpers.Add(ListHelper.RemoveAt);
            }
            if (_listHelpers.Contains(ListHelper.Iterate)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.Sort)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.ToArray)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.QueueDequeue)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.QueuePeek)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.StackPop)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.StackPeek)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.StackIterate)) changed |= _listHelpers.Add(ListHelper.Unwrap);
            if (_listHelpers.Contains(ListHelper.StackToArray)) changed |= _listHelpers.Add(ListHelper.Unwrap);

            if (_dictionaryHelpers.Contains(DictionaryHelper.Get)) changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
            if (_dictionaryHelpers.Contains(DictionaryHelper.Set)) changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
            if (_dictionaryHelpers.Contains(DictionaryHelper.Add)) changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
            if (_dictionaryHelpers.Contains(DictionaryHelper.Values))
            {
                changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
                changed |= _listHelpers.Add(ListHelper.New);
                changed |= _listHelpers.Add(ListHelper.Wrap);
            }
            if (_dictionaryHelpers.Contains(DictionaryHelper.Iterate)) changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
            if (_dictionaryHelpers.Contains(DictionaryHelper.Keys)) changed |= _listHelpers.Add(ListHelper.New);

            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearGet))
            {
                changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
                changed |= _dictionaryHelpers.Add(DictionaryHelper.LinearFind);
            }
            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearSet))
            {
                changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
                changed |= _dictionaryHelpers.Add(DictionaryHelper.LinearFind);
            }
            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearAdd))
            {
                changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
                changed |= _dictionaryHelpers.Add(DictionaryHelper.LinearFind);
            }
            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearRemove)) changed |= _dictionaryHelpers.Add(DictionaryHelper.LinearFind);
            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearContainsKey)) changed |= _dictionaryHelpers.Add(DictionaryHelper.LinearFind);
            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearValues))
            {
                changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
                changed |= _listHelpers.Add(ListHelper.New);
                changed |= _listHelpers.Add(ListHelper.Wrap);
            }
            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearIterate)) changed |= _dictionaryHelpers.Add(DictionaryHelper.Nil);
            if (_dictionaryHelpers.Contains(DictionaryHelper.LinearKeys)) changed |= _listHelpers.Add(ListHelper.New);

            if (_hashSetHelpers.Contains(HashSetHelper.LinearAdd)) changed |= _hashSetHelpers.Add(HashSetHelper.LinearFind);
            if (_hashSetHelpers.Contains(HashSetHelper.LinearRemove)) changed |= _hashSetHelpers.Add(HashSetHelper.LinearFind);
            if (_hashSetHelpers.Contains(HashSetHelper.LinearContains)) changed |= _hashSetHelpers.Add(HashSetHelper.LinearFind);
        }
    }

    private void CollectCollectionHelpers(IRBlock block)
    {
        foreach (var stmt in block.Statements)
        {
            CollectCollectionHelpers(stmt);
        }
    }

    private void CollectCollectionHelpers(IRStmt stmt)
    {
        switch (stmt)
        {
            case IRBlock block:
                CollectCollectionHelpers(block);
                break;
            case IRLocalDecl local when local.Initializer is not null:
                CollectCollectionHelpers(local.Initializer);
                break;
            case IRMultiLocalDecl local:
                foreach (var initializer in local.Initializers) CollectCollectionHelpers(initializer);
                break;
            case IRAssign assign:
                CollectCollectionHelpers(assign.Target);
                CollectCollectionHelpers(assign.Value);
                break;
            case IRMultiAssign assign:
                foreach (var target in assign.Targets) CollectCollectionHelpers(target);
                foreach (var value in assign.Values) CollectCollectionHelpers(value);
                break;
            case IRExprStmt exprStmt:
                CollectCollectionHelpers(exprStmt.Expression);
                break;
            case IRBaseConstructorCall baseCall:
                foreach (var argument in baseCall.Arguments) CollectCollectionHelpers(argument);
                break;
            case IRReturn ret when ret.Value is not null:
                CollectCollectionHelpers(ret.Value);
                break;
            case IRMultiReturn ret:
                foreach (var value in ret.Values) CollectCollectionHelpers(value);
                break;
            case IRIf iff:
                CollectCollectionHelpers(iff.Condition);
                CollectCollectionHelpers(iff.Then);
                if (iff.Else is not null) CollectCollectionHelpers(iff.Else);
                break;
            case IRSwitch sw:
                CollectCollectionHelpers(sw.Value);
                foreach (var section in sw.Sections)
                {
                    foreach (var label in section.Labels) CollectCollectionHelpers(label);
                    CollectCollectionHelpers(section.Body);
                }
                break;
            case IRWhile wh:
                CollectCollectionHelpers(wh.Condition);
                CollectCollectionHelpers(wh.Body);
                break;
            case IRFor fr:
                if (fr.Initializer is not null) CollectCollectionHelpers(fr.Initializer);
                if (fr.Condition is not null) CollectCollectionHelpers(fr.Condition);
                foreach (var incrementor in fr.Incrementors) CollectCollectionHelpers(incrementor);
                CollectCollectionHelpers(fr.Body);
                break;
            case IRForEach fe:
                if (fe.UseListIterator)
                {
                    _listHelpers.Add(ListHelper.Iterate);
                }
                CollectCollectionHelpers(fe.Collection);
                CollectCollectionHelpers(fe.Body);
                break;
            case IRDictionaryForEach fe:
                _dictionaryHelpers.Add(fe.UseLinearKeys ? DictionaryHelper.LinearIterate : DictionaryHelper.Iterate);
                CollectCollectionHelpers(fe.Dictionary);
                CollectCollectionHelpers(fe.Body);
                break;
            case IRStackForEach fe:
                _listHelpers.Add(ListHelper.StackIterate);
                CollectCollectionHelpers(fe.Stack);
                CollectCollectionHelpers(fe.Body);
                break;
            case IRHashSetForEach fe:
                _hashSetHelpers.Add(fe.UseLinearKeys ? HashSetHelper.LinearIterate : HashSetHelper.Iterate);
                CollectCollectionHelpers(fe.Set);
                CollectCollectionHelpers(fe.Body);
                break;
            case IRTry tr:
                CollectCollectionHelpers(tr.Try);
                if (tr.Catch is not null) CollectCollectionHelpers(tr.Catch);
                if (tr.Finally is not null) CollectCollectionHelpers(tr.Finally);
                break;
            case IRThrow th when th.Value is not null:
                CollectCollectionHelpers(th.Value);
                break;
        }
    }

    private void CollectCollectionHelpers(IRExpr expr)
    {
        switch (expr)
        {
            case IRMemberAccess member:
                CollectCollectionHelpers(member.Target);
                break;
            case IRElementAccess element:
                CollectCollectionHelpers(element.Target);
                CollectCollectionHelpers(element.Index);
                break;
            case IRLength length:
                CollectCollectionHelpers(length.Target);
                break;
            case IRInvocation invocation:
                CollectCollectionHelpers(invocation.Callee);
                foreach (var argument in invocation.Arguments) CollectCollectionHelpers(argument);
                break;
            case IRFunctionExpression functionExpression:
                CollectCollectionHelpers(functionExpression.Body);
                break;
            case IRArrayLiteral array:
                foreach (var item in array.Items) CollectCollectionHelpers(item);
                break;
            case IRArrayNew arrayNew:
                CollectCollectionHelpers(arrayNew.Size);
                break;
            case IRStringConcat concat:
                foreach (var part in concat.Parts) CollectCollectionHelpers(part);
                break;
            case IRDictionaryNew dictionaryNew:
                _dictionaryHelpers.Add(dictionaryNew.UseLinearKeys ? DictionaryHelper.LinearNew : DictionaryHelper.New);
                if (dictionaryNew.KeyComparer is not null) CollectCollectionHelpers(dictionaryNew.KeyComparer);
                break;
            case IRDictionaryCount dictionaryCount:
                _dictionaryHelpers.Add(dictionaryCount.UseLinearKeys ? DictionaryHelper.LinearCount : DictionaryHelper.Count);
                CollectCollectionHelpers(dictionaryCount.Table);
                break;
            case IRDictionaryGet dictionaryGet:
                _dictionaryHelpers.Add(dictionaryGet.UseLinearKeys ? DictionaryHelper.LinearGet : DictionaryHelper.Get);
                CollectCollectionHelpers(dictionaryGet.Table);
                CollectCollectionHelpers(dictionaryGet.Key);
                break;
            case IRDictionaryAdd dictionaryAdd:
                _dictionaryHelpers.Add(dictionaryAdd.UseLinearKeys ? DictionaryHelper.LinearAdd : DictionaryHelper.Add);
                CollectCollectionHelpers(dictionaryAdd.Table);
                CollectCollectionHelpers(dictionaryAdd.Key);
                CollectCollectionHelpers(dictionaryAdd.Value);
                break;
            case IRDictionarySet dictionarySet:
                _dictionaryHelpers.Add(dictionarySet.UseLinearKeys ? DictionaryHelper.LinearSet : DictionaryHelper.Set);
                CollectCollectionHelpers(dictionarySet.Table);
                CollectCollectionHelpers(dictionarySet.Key);
                CollectCollectionHelpers(dictionarySet.Value);
                break;
            case IRDictionaryRemove dictionaryRemove:
                _dictionaryHelpers.Add(dictionaryRemove.UseLinearKeys ? DictionaryHelper.LinearRemove : DictionaryHelper.Remove);
                CollectCollectionHelpers(dictionaryRemove.Table);
                CollectCollectionHelpers(dictionaryRemove.Key);
                break;
            case IRDictionaryContainsKey dictionaryContainsKey:
                _dictionaryHelpers.Add(dictionaryContainsKey.UseLinearKeys ? DictionaryHelper.LinearContainsKey : DictionaryHelper.ContainsKey);
                CollectCollectionHelpers(dictionaryContainsKey.Table);
                CollectCollectionHelpers(dictionaryContainsKey.Key);
                break;
            case IRDictionaryClear dictionaryClear:
                _dictionaryHelpers.Add(dictionaryClear.UseLinearKeys ? DictionaryHelper.LinearClear : DictionaryHelper.Clear);
                CollectCollectionHelpers(dictionaryClear.Table);
                break;
            case IRDictionaryKeys dictionaryKeys:
                _dictionaryHelpers.Add(dictionaryKeys.UseLinearKeys ? DictionaryHelper.LinearKeys : DictionaryHelper.Keys);
                CollectCollectionHelpers(dictionaryKeys.Table);
                break;
            case IRDictionaryValues dictionaryValues:
                _dictionaryHelpers.Add(dictionaryValues.UseLinearKeys ? DictionaryHelper.LinearValues : DictionaryHelper.Values);
                CollectCollectionHelpers(dictionaryValues.Table);
                break;
            case IRListNew listNew:
                _listHelpers.Add(ListHelper.New);
                foreach (var item in listNew.Items) CollectCollectionHelpers(item);
                break;
            case IRListCount listCount:
                _listHelpers.Add(ListHelper.Count);
                CollectCollectionHelpers(listCount.List);
                break;
            case IRListGet listGet:
                _listHelpers.Add(ListHelper.Get);
                CollectCollectionHelpers(listGet.List);
                CollectCollectionHelpers(listGet.Index);
                break;
            case IRListSet listSet:
                _listHelpers.Add(ListHelper.Set);
                CollectCollectionHelpers(listSet.List);
                CollectCollectionHelpers(listSet.Index);
                CollectCollectionHelpers(listSet.Value);
                break;
            case IRListAdd listAdd:
                _listHelpers.Add(ListHelper.Add);
                CollectCollectionHelpers(listAdd.List);
                CollectCollectionHelpers(listAdd.Value);
                break;
            case IRListAddRange listAddRange:
                _listHelpers.Add(ListHelper.AddRange);
                CollectCollectionHelpers(listAddRange.List);
                CollectCollectionHelpers(listAddRange.Items);
                break;
            case IRListClear listClear:
                _listHelpers.Add(ListHelper.Clear);
                CollectCollectionHelpers(listClear.List);
                break;
            case IRListContains listContains:
                _listHelpers.Add(ListHelper.Contains);
                CollectCollectionHelpers(listContains.List);
                CollectCollectionHelpers(listContains.Value);
                if (listContains.EqualityComparer is not null) CollectCollectionHelpers(listContains.EqualityComparer);
                break;
            case IRListIndexOf listIndexOf:
                _listHelpers.Add(ListHelper.IndexOf);
                CollectCollectionHelpers(listIndexOf.List);
                CollectCollectionHelpers(listIndexOf.Value);
                if (listIndexOf.EqualityComparer is not null) CollectCollectionHelpers(listIndexOf.EqualityComparer);
                break;
            case IRListInsert listInsert:
                _listHelpers.Add(ListHelper.Insert);
                CollectCollectionHelpers(listInsert.List);
                CollectCollectionHelpers(listInsert.Index);
                CollectCollectionHelpers(listInsert.Value);
                break;
            case IRListRemove listRemove:
                _listHelpers.Add(ListHelper.Remove);
                CollectCollectionHelpers(listRemove.List);
                CollectCollectionHelpers(listRemove.Value);
                if (listRemove.EqualityComparer is not null) CollectCollectionHelpers(listRemove.EqualityComparer);
                break;
            case IRListRemoveAt listRemoveAt:
                _listHelpers.Add(ListHelper.RemoveAt);
                CollectCollectionHelpers(listRemoveAt.List);
                CollectCollectionHelpers(listRemoveAt.Index);
                break;
            case IRListReverse listReverse:
                _listHelpers.Add(ListHelper.Reverse);
                CollectCollectionHelpers(listReverse.List);
                break;
            case IRListSort listSort:
                _listHelpers.Add(ListHelper.Sort);
                CollectCollectionHelpers(listSort.List);
                if (listSort.Comparer is not null) CollectCollectionHelpers(listSort.Comparer);
                break;
            case IRListToArray listToArray:
                _listHelpers.Add(ListHelper.ToArray);
                CollectCollectionHelpers(listToArray.List);
                break;
            case IRQueueDequeue queueDequeue:
                _listHelpers.Add(ListHelper.QueueDequeue);
                CollectCollectionHelpers(queueDequeue.Queue);
                break;
            case IRQueuePeek queuePeek:
                _listHelpers.Add(ListHelper.QueuePeek);
                CollectCollectionHelpers(queuePeek.Queue);
                break;
            case IRStackPop stackPop:
                _listHelpers.Add(ListHelper.StackPop);
                CollectCollectionHelpers(stackPop.Stack);
                break;
            case IRStackPeek stackPeek:
                _listHelpers.Add(ListHelper.StackPeek);
                CollectCollectionHelpers(stackPeek.Stack);
                break;
            case IRStackToArray stackToArray:
                _listHelpers.Add(ListHelper.StackToArray);
                CollectCollectionHelpers(stackToArray.Stack);
                break;
            case IRHashSetNew hashSetNew:
                _hashSetHelpers.Add(hashSetNew.UseLinearKeys ? HashSetHelper.LinearNew : HashSetHelper.New);
                if (hashSetNew.KeyComparer is not null) CollectCollectionHelpers(hashSetNew.KeyComparer);
                break;
            case IRHashSetCount hashSetCount:
                _hashSetHelpers.Add(hashSetCount.UseLinearKeys ? HashSetHelper.LinearCount : HashSetHelper.Count);
                CollectCollectionHelpers(hashSetCount.Set);
                break;
            case IRHashSetAdd hashSetAdd:
                _hashSetHelpers.Add(hashSetAdd.UseLinearKeys ? HashSetHelper.LinearAdd : HashSetHelper.Add);
                CollectCollectionHelpers(hashSetAdd.Set);
                CollectCollectionHelpers(hashSetAdd.Value);
                break;
            case IRHashSetRemove hashSetRemove:
                _hashSetHelpers.Add(hashSetRemove.UseLinearKeys ? HashSetHelper.LinearRemove : HashSetHelper.Remove);
                CollectCollectionHelpers(hashSetRemove.Set);
                CollectCollectionHelpers(hashSetRemove.Value);
                break;
            case IRHashSetContains hashSetContains:
                _hashSetHelpers.Add(hashSetContains.UseLinearKeys ? HashSetHelper.LinearContains : HashSetHelper.Contains);
                CollectCollectionHelpers(hashSetContains.Set);
                CollectCollectionHelpers(hashSetContains.Value);
                break;
            case IRHashSetClear hashSetClear:
                _hashSetHelpers.Add(hashSetClear.UseLinearKeys ? HashSetHelper.LinearClear : HashSetHelper.Clear);
                CollectCollectionHelpers(hashSetClear.Set);
                break;
            case IRHashSetToArray hashSetToArray:
                _hashSetHelpers.Add(hashSetToArray.UseLinearKeys ? HashSetHelper.LinearToArray : HashSetHelper.ToArray);
                CollectCollectionHelpers(hashSetToArray.Set);
                break;
            case IRLuaRequire luaRequire:
                CollectCollectionHelpers(luaRequire.ModuleName);
                break;
            case IRLuaGlobal luaGlobal:
                CollectCollectionHelpers(luaGlobal.Name);
                break;
            case IRLuaAccess luaAccess:
                CollectCollectionHelpers(luaAccess.Target);
                CollectCollectionHelpers(luaAccess.Name);
                break;
            case IRLuaMethodInvocation luaMethodInvocation:
                CollectCollectionHelpers(luaMethodInvocation.Target);
                CollectCollectionHelpers(luaMethodInvocation.Name);
                foreach (var argument in luaMethodInvocation.Arguments) CollectCollectionHelpers(argument);
                break;
            case IRRuntimeInvocation runtimeInvocation:
                foreach (var argument in runtimeInvocation.Arguments) CollectCollectionHelpers(argument);
                break;
            case IRTableLiteralNew tableLiteralNew:
                foreach (var (_, value) in tableLiteralNew.Fields) CollectCollectionHelpers(value);
                break;
            case IRStructValueTable structValueTable:
                CollectCollectionHelpers(structValueTable.Value);
                break;
            case IRBinary binary:
                CollectCollectionHelpers(binary.Left);
                CollectCollectionHelpers(binary.Right);
                break;
            case IRUnary unary:
                CollectCollectionHelpers(unary.Operand);
                break;
            case IRTernary ternary:
                CollectCollectionHelpers(ternary.Condition);
                CollectCollectionHelpers(ternary.WhenTrue);
                CollectCollectionHelpers(ternary.WhenFalse);
                break;
            case IRIs isExpr:
                CollectCollectionHelpers(isExpr.Value);
                break;
            case IRAs asExpr:
                CollectCollectionHelpers(asExpr.Value);
                break;
        }
    }

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
            IRListContains listContains => ExprUsesTernaryHelper(listContains.List) || ExprUsesTernaryHelper(listContains.Value) || (listContains.EqualityComparer is not null && ExprUsesTernaryHelper(listContains.EqualityComparer)),
            IRListIndexOf listIndexOf => ExprUsesTernaryHelper(listIndexOf.List) || ExprUsesTernaryHelper(listIndexOf.Value) || (listIndexOf.EqualityComparer is not null && ExprUsesTernaryHelper(listIndexOf.EqualityComparer)),
            IRListInsert listInsert => ExprUsesTernaryHelper(listInsert.List) || ExprUsesTernaryHelper(listInsert.Index) || ExprUsesTernaryHelper(listInsert.Value),
            IRListRemove listRemove => ExprUsesTernaryHelper(listRemove.List) || ExprUsesTernaryHelper(listRemove.Value) || (listRemove.EqualityComparer is not null && ExprUsesTernaryHelper(listRemove.EqualityComparer)),
            IRListRemoveAt listRemoveAt => ExprUsesTernaryHelper(listRemoveAt.List) || ExprUsesTernaryHelper(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesTernaryHelper(listReverse.List),
            IRListSort listSort => ExprUsesTernaryHelper(listSort.List) || (listSort.Comparer is not null && ExprUsesTernaryHelper(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesTernaryHelper(listToArray.List),
            IRQueueDequeue queueDequeue => ExprUsesTernaryHelper(queueDequeue.Queue),
            IRQueuePeek queuePeek => ExprUsesTernaryHelper(queuePeek.Queue),
            IRStackPop stackPop => ExprUsesTernaryHelper(stackPop.Stack),
            IRStackPeek stackPeek => ExprUsesTernaryHelper(stackPeek.Stack),
            IRStackToArray stackToArray => ExprUsesTernaryHelper(stackToArray.Stack),
            IRHashSetNew hashSetNew => hashSetNew.KeyComparer is not null && ExprUsesTernaryHelper(hashSetNew.KeyComparer),
            IRHashSetCount hashSetCount => ExprUsesTernaryHelper(hashSetCount.Set),
            IRHashSetAdd hashSetAdd => ExprUsesTernaryHelper(hashSetAdd.Set) || ExprUsesTernaryHelper(hashSetAdd.Value),
            IRHashSetRemove hashSetRemove => ExprUsesTernaryHelper(hashSetRemove.Set) || ExprUsesTernaryHelper(hashSetRemove.Value),
            IRHashSetContains hashSetContains => ExprUsesTernaryHelper(hashSetContains.Set) || ExprUsesTernaryHelper(hashSetContains.Value),
            IRHashSetClear hashSetClear => ExprUsesTernaryHelper(hashSetClear.Set),
            IRHashSetToArray hashSetToArray => ExprUsesTernaryHelper(hashSetToArray.Set),
            IRStructValueTable structValueTable => ExprUsesTernaryHelper(structValueTable.Value),
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
            IRStackForEach fe => ExprUsesTypeChecks(fe.Stack) || BlockUsesTypeChecks(fe.Body),
            IRHashSetForEach fe => ExprUsesTypeChecks(fe.Set) || BlockUsesTypeChecks(fe.Body),
            IRTry tr => BlockUsesTypeChecks(tr.Try)
                || (tr.Catch is not null && BlockUsesTypeChecks(tr.Catch))
                || (tr.Finally is not null && BlockUsesTypeChecks(tr.Finally)),
            IRThrow th => th.Value is not null && ExprUsesTypeChecks(th.Value),
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
            IRStackForEach fe => ExprUsesCoroutineHelpers(fe.Stack) || BlockUsesCoroutineHelpers(fe.Body),
            IRHashSetForEach fe => ExprUsesCoroutineHelpers(fe.Set) || BlockUsesCoroutineHelpers(fe.Body),
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
            IRStackForEach fe => ExprUsesStringConcat(fe.Stack) || BlockUsesStringConcat(fe.Body),
            IRHashSetForEach fe => ExprUsesStringConcat(fe.Set) || BlockUsesStringConcat(fe.Body),
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
            IRDictionaryNew dictionaryNew => dictionaryNew.KeyComparer is not null && ExprUsesTypeChecks(dictionaryNew.KeyComparer),
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
            IRListContains listContains => ExprUsesTypeChecks(listContains.List) || ExprUsesTypeChecks(listContains.Value) || (listContains.EqualityComparer is not null && ExprUsesTypeChecks(listContains.EqualityComparer)),
            IRListIndexOf listIndexOf => ExprUsesTypeChecks(listIndexOf.List) || ExprUsesTypeChecks(listIndexOf.Value) || (listIndexOf.EqualityComparer is not null && ExprUsesTypeChecks(listIndexOf.EqualityComparer)),
            IRListInsert listInsert => ExprUsesTypeChecks(listInsert.List) || ExprUsesTypeChecks(listInsert.Index) || ExprUsesTypeChecks(listInsert.Value),
            IRListRemove listRemove => ExprUsesTypeChecks(listRemove.List) || ExprUsesTypeChecks(listRemove.Value) || (listRemove.EqualityComparer is not null && ExprUsesTypeChecks(listRemove.EqualityComparer)),
            IRListRemoveAt listRemoveAt => ExprUsesTypeChecks(listRemoveAt.List) || ExprUsesTypeChecks(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesTypeChecks(listReverse.List),
            IRListSort listSort => ExprUsesTypeChecks(listSort.List) || (listSort.Comparer is not null && ExprUsesTypeChecks(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesTypeChecks(listToArray.List),
            IRQueueDequeue queueDequeue => ExprUsesTypeChecks(queueDequeue.Queue),
            IRQueuePeek queuePeek => ExprUsesTypeChecks(queuePeek.Queue),
            IRStackPop stackPop => ExprUsesTypeChecks(stackPop.Stack),
            IRStackPeek stackPeek => ExprUsesTypeChecks(stackPeek.Stack),
            IRStackToArray stackToArray => ExprUsesTypeChecks(stackToArray.Stack),
            IRHashSetNew hashSetNew => hashSetNew.KeyComparer is not null && ExprUsesTypeChecks(hashSetNew.KeyComparer),
            IRHashSetCount hashSetCount => ExprUsesTypeChecks(hashSetCount.Set),
            IRHashSetAdd hashSetAdd => ExprUsesTypeChecks(hashSetAdd.Set) || ExprUsesTypeChecks(hashSetAdd.Value),
            IRHashSetRemove hashSetRemove => ExprUsesTypeChecks(hashSetRemove.Set) || ExprUsesTypeChecks(hashSetRemove.Value),
            IRHashSetContains hashSetContains => ExprUsesTypeChecks(hashSetContains.Set) || ExprUsesTypeChecks(hashSetContains.Value),
            IRHashSetClear hashSetClear => ExprUsesTypeChecks(hashSetClear.Set),
            IRHashSetToArray hashSetToArray => ExprUsesTypeChecks(hashSetToArray.Set),
            IRLuaRequire luaRequire => ExprUsesTypeChecks(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesTypeChecks(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesTypeChecks(luaAccess.Target) || ExprUsesTypeChecks(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesTypeChecks(luaMethodInvocation.Target) || ExprUsesTypeChecks(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesTypeChecks),
            IRRuntimeInvocation runtimeInvocation => runtimeInvocation.Arguments.Any(ExprUsesTypeChecks),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesTypeChecks(f.Value)),
            IRStructValueTable structValueTable => ExprUsesTypeChecks(structValueTable.Value),
            IRBinary binary => ExprUsesTypeChecks(binary.Left) || ExprUsesTypeChecks(binary.Right),
            IRUnary unary => ExprUsesTypeChecks(unary.Operand),
            _ => false,
        };

    private static bool ExprUsesStringConcat(IRExpr expr)
        => expr switch
        {
            IRStringConcat => true,
            IRDictionaryNew dictionaryNew => dictionaryNew.KeyComparer is not null && ExprUsesStringConcat(dictionaryNew.KeyComparer),
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
            IRListContains listContains => ExprUsesStringConcat(listContains.List) || ExprUsesStringConcat(listContains.Value) || (listContains.EqualityComparer is not null && ExprUsesStringConcat(listContains.EqualityComparer)),
            IRListIndexOf listIndexOf => ExprUsesStringConcat(listIndexOf.List) || ExprUsesStringConcat(listIndexOf.Value) || (listIndexOf.EqualityComparer is not null && ExprUsesStringConcat(listIndexOf.EqualityComparer)),
            IRListInsert listInsert => ExprUsesStringConcat(listInsert.List) || ExprUsesStringConcat(listInsert.Index) || ExprUsesStringConcat(listInsert.Value),
            IRListRemove listRemove => ExprUsesStringConcat(listRemove.List) || ExprUsesStringConcat(listRemove.Value) || (listRemove.EqualityComparer is not null && ExprUsesStringConcat(listRemove.EqualityComparer)),
            IRListRemoveAt listRemoveAt => ExprUsesStringConcat(listRemoveAt.List) || ExprUsesStringConcat(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesStringConcat(listReverse.List),
            IRListSort listSort => ExprUsesStringConcat(listSort.List) || (listSort.Comparer is not null && ExprUsesStringConcat(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesStringConcat(listToArray.List),
            IRQueueDequeue queueDequeue => ExprUsesStringConcat(queueDequeue.Queue),
            IRQueuePeek queuePeek => ExprUsesStringConcat(queuePeek.Queue),
            IRStackPop stackPop => ExprUsesStringConcat(stackPop.Stack),
            IRStackPeek stackPeek => ExprUsesStringConcat(stackPeek.Stack),
            IRStackToArray stackToArray => ExprUsesStringConcat(stackToArray.Stack),
            IRHashSetNew hashSetNew => hashSetNew.KeyComparer is not null && ExprUsesStringConcat(hashSetNew.KeyComparer),
            IRHashSetCount hashSetCount => ExprUsesStringConcat(hashSetCount.Set),
            IRHashSetAdd hashSetAdd => ExprUsesStringConcat(hashSetAdd.Set) || ExprUsesStringConcat(hashSetAdd.Value),
            IRHashSetRemove hashSetRemove => ExprUsesStringConcat(hashSetRemove.Set) || ExprUsesStringConcat(hashSetRemove.Value),
            IRHashSetContains hashSetContains => ExprUsesStringConcat(hashSetContains.Set) || ExprUsesStringConcat(hashSetContains.Value),
            IRHashSetClear hashSetClear => ExprUsesStringConcat(hashSetClear.Set),
            IRHashSetToArray hashSetToArray => ExprUsesStringConcat(hashSetToArray.Set),
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
            IRStructValueTable structValueTable => ExprUsesStringConcat(structValueTable.Value),
            IRBinary binary => ExprUsesStringConcat(binary.Left) || ExprUsesStringConcat(binary.Right),
            IRUnary unary => ExprUsesStringConcat(unary.Operand),
            IRIs isExpr => ExprUsesStringConcat(isExpr.Value),
            IRAs asExpr => ExprUsesStringConcat(asExpr.Value),
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
            IRListContains listContains => ExprUsesCoroutineHelpers(listContains.List) || ExprUsesCoroutineHelpers(listContains.Value) || (listContains.EqualityComparer is not null && ExprUsesCoroutineHelpers(listContains.EqualityComparer)),
            IRListIndexOf listIndexOf => ExprUsesCoroutineHelpers(listIndexOf.List) || ExprUsesCoroutineHelpers(listIndexOf.Value) || (listIndexOf.EqualityComparer is not null && ExprUsesCoroutineHelpers(listIndexOf.EqualityComparer)),
            IRListInsert listInsert => ExprUsesCoroutineHelpers(listInsert.List) || ExprUsesCoroutineHelpers(listInsert.Index) || ExprUsesCoroutineHelpers(listInsert.Value),
            IRListRemove listRemove => ExprUsesCoroutineHelpers(listRemove.List) || ExprUsesCoroutineHelpers(listRemove.Value) || (listRemove.EqualityComparer is not null && ExprUsesCoroutineHelpers(listRemove.EqualityComparer)),
            IRListRemoveAt listRemoveAt => ExprUsesCoroutineHelpers(listRemoveAt.List) || ExprUsesCoroutineHelpers(listRemoveAt.Index),
            IRListReverse listReverse => ExprUsesCoroutineHelpers(listReverse.List),
            IRListSort listSort => ExprUsesCoroutineHelpers(listSort.List) || (listSort.Comparer is not null && ExprUsesCoroutineHelpers(listSort.Comparer)),
            IRListToArray listToArray => ExprUsesCoroutineHelpers(listToArray.List),
            IRQueueDequeue queueDequeue => ExprUsesCoroutineHelpers(queueDequeue.Queue),
            IRQueuePeek queuePeek => ExprUsesCoroutineHelpers(queuePeek.Queue),
            IRStackPop stackPop => ExprUsesCoroutineHelpers(stackPop.Stack),
            IRStackPeek stackPeek => ExprUsesCoroutineHelpers(stackPeek.Stack),
            IRStackToArray stackToArray => ExprUsesCoroutineHelpers(stackToArray.Stack),
            IRHashSetNew hashSetNew => hashSetNew.KeyComparer is not null && ExprUsesCoroutineHelpers(hashSetNew.KeyComparer),
            IRHashSetCount hashSetCount => ExprUsesCoroutineHelpers(hashSetCount.Set),
            IRHashSetAdd hashSetAdd => ExprUsesCoroutineHelpers(hashSetAdd.Set) || ExprUsesCoroutineHelpers(hashSetAdd.Value),
            IRHashSetRemove hashSetRemove => ExprUsesCoroutineHelpers(hashSetRemove.Set) || ExprUsesCoroutineHelpers(hashSetRemove.Value),
            IRHashSetContains hashSetContains => ExprUsesCoroutineHelpers(hashSetContains.Set) || ExprUsesCoroutineHelpers(hashSetContains.Value),
            IRHashSetClear hashSetClear => ExprUsesCoroutineHelpers(hashSetClear.Set),
            IRHashSetToArray hashSetToArray => ExprUsesCoroutineHelpers(hashSetToArray.Set),
            IRLuaRequire luaRequire => ExprUsesCoroutineHelpers(luaRequire.ModuleName),
            IRLuaGlobal luaGlobal => ExprUsesCoroutineHelpers(luaGlobal.Name),
            IRLuaAccess luaAccess => ExprUsesCoroutineHelpers(luaAccess.Target) || ExprUsesCoroutineHelpers(luaAccess.Name),
            IRLuaMethodInvocation luaMethodInvocation => ExprUsesCoroutineHelpers(luaMethodInvocation.Target) || ExprUsesCoroutineHelpers(luaMethodInvocation.Name) || luaMethodInvocation.Arguments.Any(ExprUsesCoroutineHelpers),
            IRTableLiteralNew tableLiteralNew => tableLiteralNew.Fields.Any(f => ExprUsesCoroutineHelpers(f.Value)),
            IRStructValueTable structValueTable => ExprUsesCoroutineHelpers(structValueTable.Value),
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
            case IRStackForEach fe:
                _usedIdentifiers.Add(fe.ItemName);
                CollectIdentifiers(fe.Stack);
                CollectIdentifiers(fe.Body);
                break;
            case IRHashSetForEach fe:
                _usedIdentifiers.Add(fe.ItemName);
                CollectIdentifiers(fe.Set);
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
            case IRStructValueTable structValueTable:
                CollectIdentifiers(structValueTable.Value);
                break;
            case IRStringConcat concat:
                foreach (var part in concat.Parts) CollectIdentifiers(part);
                break;
            case IRDictionaryNew dictionaryNew:
                if (dictionaryNew.KeyComparer is not null) CollectIdentifiers(dictionaryNew.KeyComparer);
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
                if (listContains.EqualityComparer is not null) CollectIdentifiers(listContains.EqualityComparer);
                break;
            case IRListIndexOf listIndexOf:
                CollectIdentifiers(listIndexOf.List);
                CollectIdentifiers(listIndexOf.Value);
                if (listIndexOf.EqualityComparer is not null) CollectIdentifiers(listIndexOf.EqualityComparer);
                break;
            case IRListInsert listInsert:
                CollectIdentifiers(listInsert.List);
                CollectIdentifiers(listInsert.Index);
                CollectIdentifiers(listInsert.Value);
                break;
            case IRListRemove listRemove:
                CollectIdentifiers(listRemove.List);
                CollectIdentifiers(listRemove.Value);
                if (listRemove.EqualityComparer is not null) CollectIdentifiers(listRemove.EqualityComparer);
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
            case IRQueueDequeue queueDequeue:
                CollectIdentifiers(queueDequeue.Queue);
                break;
            case IRQueuePeek queuePeek:
                CollectIdentifiers(queuePeek.Queue);
                break;
            case IRStackPop stackPop:
                CollectIdentifiers(stackPop.Stack);
                break;
            case IRStackPeek stackPeek:
                CollectIdentifiers(stackPeek.Stack);
                break;
            case IRStackToArray stackToArray:
                CollectIdentifiers(stackToArray.Stack);
                break;
            case IRHashSetNew hashSetNew:
                if (hashSetNew.KeyComparer is not null) CollectIdentifiers(hashSetNew.KeyComparer);
                break;
            case IRHashSetCount hashSetCount:
                CollectIdentifiers(hashSetCount.Set);
                break;
            case IRHashSetAdd hashSetAdd:
                CollectIdentifiers(hashSetAdd.Set);
                CollectIdentifiers(hashSetAdd.Value);
                break;
            case IRHashSetRemove hashSetRemove:
                CollectIdentifiers(hashSetRemove.Set);
                CollectIdentifiers(hashSetRemove.Value);
                break;
            case IRHashSetContains hashSetContains:
                CollectIdentifiers(hashSetContains.Set);
                CollectIdentifiers(hashSetContains.Value);
                break;
            case IRHashSetClear hashSetClear:
                CollectIdentifiers(hashSetClear.Set);
                break;
            case IRHashSetToArray hashSetToArray:
                CollectIdentifiers(hashSetToArray.Set);
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
