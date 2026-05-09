# Collections

SharpForge includes small `SFLib` collection helpers for emitted Lua. They support the collection operations that SharpForge lowers directly, not the full .NET collection interface surface.

## List<T>

Supported `List<T>` members:

- `Count`
- index get/set
- `Add`
- `AddRange(List<T>)`
- `AddRange(T[])`
- `Clear`
- `Contains`
- `IndexOf`
- `Insert`
- `Remove`
- `RemoveAt`
- `Reverse`
- `Sort()`
- `Sort(Func<T, T, bool>)`
- `ToArray`
- `foreach`

Lists preserve `nil` elements with an internal sentinel. `ToArray()` unwraps the sentinel back to `nil`.

For normal values, `Contains`, `IndexOf`, and `Remove` use Lua equality. For flattened struct element types, SharpForge requires a public typed equality method:

```csharp
public bool Equals(MyStruct other)
```

When that method exists, generated Lua calls it for `Contains`, `IndexOf`, and `Remove`. `Equals(object)` and `GetHashCode()` are not used for struct collection equality.

## Dictionary<K, V>

Supported `Dictionary<K, V>` members:

- `Count`
- index get/set
- `Add`
- `Get`
- `Set`
- `ContainsKey`
- `Remove`
- `Clear`
- `Keys`
- `Values`
- `foreach`

Dictionary values preserve `nil` with an internal sentinel, so a present key with a `nil` value is distinct from an absent key.

For non-struct keys, dictionaries use a Lua table plus an insertion-order key list:

```lua
data = { key1 = value1, key2 = value2 }
keys = { key1, key2 }
```

Lookup and assignment use Lua table key semantics. Iteration follows insertion order, and modifying a dictionary during iteration throws.

For flattened struct keys, dictionaries use a linear key/value list and the struct's typed `Equals(T)` method. This avoids hashes and string key canonicalization, preserves the original key values for `Keys` and `foreach`, and lets user code define equality for floats, object-like fields, Lua values, and JASS handles.

Struct dictionary keys require:

```csharp
public bool Equals(MyStruct other)
```

If the method is missing, SharpForge emits a diagnostic. Struct dictionary keys do not use `GetHashCode()` or `Equals(object)`.

## Limits

SharpForge collection helpers are runtime helpers for generated Lua. They are not drop-in replacements for all `System.Collections.Generic` APIs, collection interfaces, LINQ extension methods, or comparer-based .NET overloads.