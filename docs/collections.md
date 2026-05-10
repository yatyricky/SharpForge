# Collections

SharpForge includes small collection helpers in `SFLib.Collections` for emitted Lua. They support the collection operations that SharpForge lowers directly, not the full .NET collection interface surface.

```csharp
using SFLib.Collections;
```

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

Local `List<struct>` values can lower to parallel field arrays. In that shape, `RemoveAt`, `Remove`, `Clear`, `Contains`, and `IndexOf` operate across every field array instead of boxing each struct into a table.

## Queue<T>

Supported `Queue<T>` members:

- `Count`
- `Enqueue`
- `Dequeue`
- `Peek`
- `Clear`
- `Contains`
- `ToArray`
- `foreach`

Queues use first-in, first-out ordering. `Dequeue()`, `Peek()`, `ToArray()`, and `foreach` return values in queue order. `Dequeue()` and `Peek()` throw if the queue is empty.

For flattened struct element types, `Contains` uses the same typed `Equals(T)` rule as `List<T>`.

Local `Queue<struct>` values can lower to parallel field arrays. `Enqueue` appends every field, while `Dequeue`, `Clear`, and `Contains` update or scan every field array in lockstep.

## Stack<T>

Supported `Stack<T>` members:

- `Count`
- `Push`
- `Pop`
- `Peek`
- `Clear`
- `Contains`
- `ToArray`
- `foreach`

Stacks use last-in, first-out ordering. `Pop()`, `Peek()`, `ToArray()`, and `foreach` return the most recently pushed value first. `Pop()` and `Peek()` throw if the stack is empty.

For flattened struct element types, `Contains` uses the same typed `Equals(T)` rule as `List<T>`.

Local `Stack<struct>` values can lower to parallel field arrays. `Push` appends every field, while `Pop`, `Clear`, and `Contains` update or scan every field array in lockstep.

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

## HashSet<T>

Supported `HashSet<T>` members:

- `Count`
- `Add`
- `Contains`
- `Remove`
- `Clear`
- `ToArray`
- `foreach`

For non-struct values, hash sets use Lua table key semantics plus an insertion-order value list. `Add` returns `false` when the value is already present. `Remove` returns `false` when the value is absent. `ToArray()` and `foreach` follow insertion order, and modifying a hash set during iteration throws.

For flattened struct values, hash sets use a linear value list and the struct's typed `Equals(T)` method, matching struct-keyed dictionary behavior. Struct hash set values require:

```csharp
public bool Equals(MyStruct other)
```

If the method is missing, SharpForge emits a diagnostic. Struct hash set values do not use `GetHashCode()` or `Equals(object)`.

Local `HashSet<struct>` values can lower to parallel field arrays when the surrounding uses stay within `Add`, `Contains`, `Remove`, `Clear`, and `Count`. In that shape, set membership scans spread struct fields into the typed `Equals(T)` method and removals delete the matched index from every field array.

## Limits

SharpForge collection helpers are runtime helpers for generated Lua. They are not drop-in replacements for all `System.Collections.Generic` APIs, collection interfaces, LINQ extension methods, capacity-based constructors, comparer-based .NET overloads, or set algebra APIs.