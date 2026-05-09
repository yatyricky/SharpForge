# Collections

SharpForge includes small collection helpers for emitted Lua. These helpers are shaped for generated code, not for full .NET API compatibility.

## Design Goal

The runtime should be small, deterministic, and easy to inspect in generated Lua.

SharpForge prefers:

- compact tables
- direct helper functions
- stable behavior needed by emitted C# patterns
- no broad compatibility layer for unused .NET interfaces

For example, SharpForge wants a minimal usable `List<T>`, not a Lua reimplementation of:

```csharp
public class List<T> :
    ICollection<T>,
    IEnumerable<T>,
    IList<T>,
    IReadOnlyCollection<T>,
    IReadOnlyList<T>,
    IList
```

## Dictionary Shape

Dictionary helpers use two tables:

```lua
data = { key1 = value1, key2 = value2 }
keys = { key1, key2 }
```

- `data` gives normal table lookup and assignment.
- `keys` preserves insertion order for stable iteration.
- `nil` values are stored through an internal sentinel so `nil` can be distinguished from an absent key.

## Operations

`DictNew__()` creates a dictionary table:

```lua
return {
    data = {},
    keys = {}
}
```

`DictGet__(dict, key)` returns the stored value, translating the internal nil sentinel back to `nil`.

`DictSet__(dict, key, value)` inserts new keys into `keys` and stores values in `data`.

`DictRemove__(dict, key)` clears the key from `data` and removes it from `keys` with a linear scan.

`DictIterate__(dict)` returns key/value pairs in insertion order.

## Tradeoff

Set/get are constant-time table operations. Removal pays a linear scan to preserve stable iteration order. That is intentional: deletion is less common than lookup/iteration in the map-script workloads this helper targets.