# Collections

SharpForge supports `List<T>` and `Dictionary<K,V>` from `SFLib.Collections`. No other collection types are provided; build your own from these primitives if needed.

## List\<T\>

```csharp
using SFLib.Collections;

var list = new List<string>();
list.Add("a");
list.Add("b");
int count = list.Count;
string item = list[0];
list[1] = "c";
bool has = list.Contains("a");
int idx = list.IndexOf("a");
list.Insert(0, "z");
list.Remove("a");
list.RemoveAt(0);
list.Reverse();
list.Sort();
list.Sort((a, b) => a < b);
string[] arr = list.ToArray();
list.Clear();
```

All `List<T>` operations lower to `SF__.ListXxx__(list, ...)` helper calls. Indexing is 0-based in C#; the transpiler adds 1 for all list index expressions. The core helpers emitted at runtime:

```lua
function SF__.ListNew__(items)
    local list = { items = {}, version = 0 }
    if items ~= nil then
        for i = 1, #items do
            list.items[i] = items[i]
        end
    end
    return list
end

function SF__.ListCount__(list)  return #list.items end
function SF__.ListGet__(list, index)  return list.items[index + 1] end
function SF__.ListSet__(list, index, value)  list.items[index + 1] = value; list.version = list.version + 1 end
function SF__.ListAdd__(list, value)  table.insert(list.items, value); list.version = list.version + 1 end
function SF__.ListRemoveAt__(list, index)  table.remove(list.items, index + 1); list.version = list.version + 1 end
function SF__.ListClear__(list)  list.items = {}; list.version = list.version + 1 end
```

`nil` values in a list are wrapped internally using a sentinel (`SF__.ListNil__`) so that `nil` entries don't collapse the table. `ListGet__` and `ListSet__` unwrap/wrap transparently.

```lua
local list = SF__.ListNew__({})
SF__.ListAdd__(list, "a")
SF__.ListAdd__(list, "b")
local count = SF__.ListCount__(list)
local item = SF__.ListGet__(list, 1)
SF__.ListSet__(list, 2, "c")
local has = SF__.ListContains__(list, "a")
local idx = SF__.ListIndexOf__(list, "a")
SF__.ListInsert__(list, 1, "z")
SF__.ListRemove__(list, "a")
SF__.ListRemoveAt__(list, 1)
SF__.ListReverse__(list)
SF__.ListSort__(list)
SF__.ListSort__(list, function(a, b) return (a < b) end)
local arr = SF__.ListToArray__(list)
SF__.ListClear__(list)
```

### foreach over List\<T\>

```csharp
foreach (var item in list)
{
    Use(item);
}
```

```lua
for i = 1, SF__.ListCount__(list) do
    local item = SF__.ListGet__(list, i)
    Use(item)
end
```

## Dictionary\<K,V\>

```csharp
using SFLib.Collections;

var dict = new Dictionary<string, int>();
dict.Add("key", 1);
dict["key"] = 2;
int val = dict["key"];
bool has = dict.ContainsKey("key");
dict.Remove("key");
int count = dict.Count;
dict.Clear();
```

```lua
local dict = SF__.DictNew__()
SF__.DictAdd__(dict, "key", 1)
SF__.DictSet__(dict, "key", 2)
local val = SF__.DictGet__(dict, "key")
local has = SF__.DictContainsKey__(dict, "key")
SF__.DictRemove__(dict, "key")
local count = SF__.DictCount__(dict)
SF__.DictClear__(dict)
```

The core helpers emitted at runtime:

```lua
function SF__.DictNew__()  return { data = {}, keys = {}, version = 0 } end
function SF__.DictCount__(dict)  return #dict.keys end
function SF__.DictGet__(dict, key)
    local value = dict.data[key]
    if value == SF__.DictNil__ then return nil end
    return value
end
function SF__.DictSet__(dict, key, value)
    if dict.data[key] == nil then table.insert(dict.keys, key) end
    dict.data[key] = value == nil and SF__.DictNil__ or value
    dict.version = dict.version + 1
end
function SF__.DictAdd__(dict, key, value)
    if dict.data[key] ~= nil then error("duplicate key") end
    table.insert(dict.keys, key)
    dict.data[key] = value == nil and SF__.DictNil__ or value
    dict.version = dict.version + 1
end
function SF__.DictContainsKey__(dict, key)  return dict.data[key] ~= nil end
function SF__.DictClear__(dict)  dict.data = {}; dict.keys = {}; dict.version = dict.version + 1 end
```

`nil` values are stored using a `SF__.DictNil__` sentinel so that `nil` entries are distinct from missing keys.

### foreach over Dictionary\<K,V\>

```csharp
foreach (var kv in dict)
{
    Use(kv.Key, kv.Value);
}
```

```lua
for _, kv__ in ipairs(SF__.DictKeys__(dict)) do
    local kv__key = kv__
    local kv__val = SF__.DictGet__(dict, kv__)
    Use(kv__key, kv__val)
end
```

## Struct Keys / Values

When `K` or `V` is a struct type, dictionaries use a linear key comparer. The helper functions adjust accordingly.
