# Struct Lowering

This page documents the current `struct` lowering strategy in `sf-transpile`.

## Current Implementation

Structs are treated as value-shaped data, not runtime objects. SharpForge does not emit struct constructor functions, so generated Lua should not contain `StructName.New(...)` or `StructName.__Init(...)` for C# structs.

When a struct local is only used through its fields, the local is flattened into one Lua local per field:

```csharp
struct Vector2
{
    public float x;
    public float y;
}

var v = new Vector2 { x = 10, y = 5 };
BJDebugMsg($"x:{v.x} y:{v.y}");
```

emits in the shape:

```lua
local v__x, v__y = 10, 5
BJDebugMsg(SF__.StrConcat__("x:", v__x, " y:", v__y))
```

The flattened local naming convention is `<local>__<field>`. Field assignment and whole-local assignment update those field locals directly:

```csharp
v.x = 11;
v = new Vector2 { x = 12, y = 6 };
```

emits in the shape:

```lua
v__x = 11
v__x, v__y = 12, 6
```

Struct-returning methods can return multiple Lua values. A caller that stores the result in a field-only local receives those values into flattened locals:

```csharp
static Vector2 Make(float scale)
{
    return new Vector2 { x = scale + 1, y = scale + 2 };
}

var data = Make(2);
BJDebugMsg($"{data.x}:{data.y}");
```

emits in the shape:

```lua
function SF__.Demo.Make(scale)
    return (scale + 1), (scale + 2)
end

local data__x, data__y = SF__.Demo.Make(2)
BJDebugMsg(SF__.StrConcat__(data__x, ":", data__y))
```

Empty struct type blocks are skipped. A struct with methods or static members can still emit a root-table type entry for those members, but constructors are still omitted.

Struct-typed class fields and auto-properties are flattened into fields on the owning class. Instance members are stored on `self`, and static members are stored on the generated type table:

```csharp
struct Color
{
    public float r;
    public float g;
    public float b;
}

private Color tint;
private static Color defaultTint;
```

emits in the shape:

```lua
self.tint__r = 0
self.tint__g = 0
self.tint__b = 0
SF__.Sprite.defaultTint__r = 0
SF__.Sprite.defaultTint__g = 0
SF__.Sprite.defaultTint__b = 0
```

Access to struct fields uses the flattened member name:

```csharp
var red = tint.r * 255;
```

emits in the shape:

```lua
local red = (self.tint__r * 255)
```

### Struct as parameter

```csharp
struct Vector2
{
    public float x;
    public float y;
}

class Sample
{
    public void MoveUnitTo(unit u, Vector2 pos)
    {
        SetUnitX(u, pos.x);
        SetUnitY(u, pos.y);
    }
}
```
emits in the shape:
```lua
-- skip class and struct type blocks

function SF__.Sample.MoveUnitTo(u, pos__x, pos__y)
    SetUnitX(u, pos__x)
    SetUnitY(u, pos__y)
end
```

### Structs with Methods

```csharp
struct Vector2
{
    float x;
    float y;

    public static Vector2 operator +(Vector2 left, Vector2 right)
    {
        return new Vector2 { x = left.x + right.x, y = left.y + right.y };
    }

    public float Magnitude()
    {
        return Mathf.Sqrt(x * x + y * y);
    }
}
```

emits in the shape:

```lua
SF__.Vector2 = {}
function SF__.Vector2.op_Addition(left__x, left__y, right__x, right__y)
    return left__x + right__x, left__y + right__y
end
function SF__.Vector2.Magnitude(self__x, self__y)
    return SF__.Mathf.Sqrt(self__x * self__x + self__y * self__y)
end
```

### Structs in List\<T\>

Struct-typed lists use a **struct-of-arrays** (SoA) representation. Instead of storing struct values as table elements, the transpiler emits one parallel Lua array per leaf field. This extends the existing `<name>__<field>` flattening convention — the list local pluralizes into per-field tables indexed in lockstep.

#### Chosen Strategy: Struct-of-Arrays

Given `struct Vector2 { float x; float y; }` and `List<Vector2> dirs`:

```
C#:           dirs[0]       dirs[1]       dirs[2]
Fields:       x0, y0        x1, y1        x2, y2

Lua:
dirs__x = {    x0,           x1,           x2,          … }
dirs__y = {    y0,           y1,           y2,          … }
```

**Access.** `dirs[i].x` → `dirs__x[i + 1]`, `dirs[i].y` → `dirs__y[i + 1]` (Lua is 1-based, so C# index `i` maps to Lua index `i + 1`).

**Declaration.**

```csharp
var dirs = new List<Vector2>();
```

emits in the shape:

```lua
local dirs__x = {}
local dirs__y = {}
```

**Add.**

```csharp
dirs.Add(new Vector2 { x = 1, y = 2 });
```

emits in the shape:

```lua
table.insert(dirs__x, 1)
table.insert(dirs__y, 2)
```

**Indexed assignment.**

```csharp
dirs[0] = new Vector2 { x = 3, y = 4 };
```

emits in the shape:

```lua
dirs__x[1] = 3
dirs__y[1] = 4
```

**Foreach iteration.**

```csharp
foreach (var d in dirs)
{
    BJDebugMsg($"{d.x}:{d.y}");
}
```

emits in the shape:

```lua
for i = 1, #dirs__x do
    local d__x = dirs__x[i]
    local d__y = dirs__y[i]
    BJDebugMsg(SF__.StrConcat__(d__x, ":", d__y))
end
```

**RemoveAt.**

```csharp
dirs.RemoveAt(0);
```

emits a helper that removes the same index from all parallel arrays:

```lua
SF__.ListRemoveAt__(dirs__x, dirs__y, 1)
```

**Count.**

```csharp
var n = dirs.Count;
```

emits in the shape:

```lua
local n = #dirs__x
```

Count reads from any parallel array — they are always the same length.

#### Pass and Return

A `List<Vector2>` parameter flattens into one parameter per field table:

```csharp
void Process(List<Vector2> dirs) { … }
```

```lua
function SF__.Demo.Process(dirs__x, dirs__y)
```

A method returning `List<Vector2>` uses Lua multi-return:

```csharp
List<Vector2> Make() => new List<Vector2>();
```

```lua
function SF__.Demo.Make()
    return {}, {}
end
```

Caller:

```csharp
var result = Make();
```

```lua
local result__x, result__y = SF__.Demo.Make()
```

#### Why Not Interleaved Array

An alternative is a single flat table with fields interleaved by stride (`{x0, y0, x1, y1, …}`). This was rejected for the following reasons, ordered by the [lowering feature design priorities](../.github/instructions/lowering-feature-design.instructions.md):

| Priority | Interleaved array | Struct-of-arrays |
|---|---|---|
| **1. Performance** | One table, access = `list[i*2+1]`. | N tables, access = `list__x[i]`. Both O(1); negligible difference for map-script workloads. |
| **2. Easy to implement** | Requires stride-aware member access IR and a separate `foreach` code path. New concepts at the IR boundary. | Extends the existing `<name>__<field>` naming convention. Foreach adapts the enumerator pattern. Minimal new infrastructure. |
| **3. Less likely to produce bugs** | Stride arithmetic is fragile: off-by-one in emitted indices, field reordering silently shifts offsets. | No arithmetic. Names encode semantics. Parallel arrays stay synchronized by construction. |
| **4. Concise output** | One table. | N tables per N-field struct. |
| **5. Human-readable output** | `lua_dirs[5]` is unreadable without the struct layout. | `dirs__x[3]` is self-documenting. |

Struct-of-arrays wins on priorities 2, 3, and 5; loses only on priority 4 (conciseness). Following the rule that higher priorities win conflicts, **struct-of-arrays is the chosen strategy**.

#### Nested Structs in Lists

Nested structs extend the SoA pattern naturally. Given `struct S2 { S1 s; bool bv; }` and `List<S2>`:

```
list__s__v1 = { … }
list__s__v2 = { … }
list__bv   = { … }
```

One parallel array per leaf field, indexed in lockstep. No new representation mechanism is needed — the same flattening recursion that produces `local__s__v1` for scalar variables produces `list__s__v1` for list-backed variables.

#### Non-Struct Lists

`List<float>`, `List<int>`, `List<SomeClass>`, and other non-struct element types remain single-table `List<T>` with no SoA treatment. The SoA path only activates when `T` is a struct type.

#### Runtime Wrapper Consistency

The current `List<T>` runtime wraps items in a version-tracked table:

```lua
{ items = { … }, version = 0 }
```

All helpers (`ListGet__`, `ListSet__`, `ListAdd__`, `ListIterate__`, `ListSort__`) operate on `.items` and bump `.version` on mutations. The `version` field enables detection of modification-during-iteration (the `ListIterate__` helper errors if the version changed mid-loop).

For SoA lists, the version-tracking wrapper must cover all parallel arrays atomically. The implementation has two viable shapes:

**Unified wrapper** (one `version`, N arrays stored as fields):

```lua
dirs = {
    __x = { x0, x1, … },
    __y = { y0, y1, … },
    version = 0
}
```

Access: `dirs.__x[i]`. All arrays share one `version`. A single mutation bumps it once.

**Separate wrappers** (N wrappers, each with its own `version`):

```lua
dirs__x = { items = { x0, x1, … }, version = 0 }
dirs__y = { items = { y0, y1, … }, version = 0 }
```

Access: `dirs__x.items[i]`. Mutations must bump all versions in lockstep.

The unified wrapper is preferred: it avoids duplicate version fields, guarantees atomic version bumps, and keeps the list conceptually as one object (matching the C# source where `dirs` is a single `List<Vector2>` variable).

Whichever shape is chosen, the doc examples above use the flattened local-name convention (`dirs__x`, `dirs__y`) to describe the *logical* emitted Lua. The actual runtime helper calls (`SF__.ListGet__`, etc.) wrap these details; the emitter is free to route through `.items` or a parallel-array-aware helper as needed.

#### Future List Methods

When implementing additional `List<T>` methods, each must account for the SoA case when `T` is a struct type. Methods that touch individual elements or iterate must be aware of parallel arrays:

| Method | SoA impact |
|---|---|
| `Insert(index, item)` | Must insert at the same index in all parallel arrays |
| `Remove(item)` | Must scan one array to find the index, then remove from all |
| `Clear()` | Must clear all parallel arrays |
| `IndexOf(item)` | Struct equality is unsupported (no `Equals`); this method is likely a diagnostic for struct `T` |
| `Contains(item)` | Same equality constraint as `IndexOf` |
| `CopyTo(array, index)` | Must copy all parallel arrays into the corresponding flattened destination |
| `ToArray()` | Must reconstruct a struct representation; may require boxing into a table per element |
| `GetEnumerator()` / manual enumeration | Must yield indices usable across all parallel arrays |
| `this[int].set` after `Add` | Must assign to the same logical index in all arrays |

The general rule: any operation that adds, removes, or moves elements must apply to all parallel arrays atomically. Operations that compare elements (`IndexOf`, `Contains`, `Remove(item)`) must respect the no-boxed-equality constraint on structs.

When adding a new `List<T>` method to the transpiler, check `IsListType(type)` and then inspect the type argument: if `T` is a struct (`CanFlattenStructType`), the lowering must emit parallel-array-aware IR or a diagnostic.

## Nested Structs

Nested structs (a struct whose field is another struct) extend the flattening model recursively. The design follows the same zero-cost, value-tuple-in-disguise approach used for single-level structs.

### Flattening Rule

A nested struct flattens into one Lua local per leaf field using depth-first, declaration-order traversal. The naming convention stacks `__` for each level:

```
<local>__<field>__<subfield>…__<leaf>
```

Given:

```csharp
struct S1 { int v1; int v2; }
struct S2 { S1 s; bool bv; }
```

The leaf fields of `S2` are `s.v1`, `s.v2`, `bv` — in that order.

### Declaration and Initialization

```csharp
var s2 = new S2 { s = new S1 { v1 = 1, v2 = 2 }, bv = true };
```

emits in the shape:

```lua
local s2__s__v1, s2__s__v2, s2__bv = 1, 2, true
```

### Field Access

```csharp
var x = s2.s.v1;
```

emits in the shape:

```lua
local x = s2__s__v1
```

### Parameter Passing

```csharp
void Process(S2 val) { … }
```

emits in the shape:

```lua
function Process(val__s__v1, val__s__v2, val__bv)
```

### Multi-Return

```csharp
S2 Make() => new S2 { s = new S1 { v1 = 1, v2 = 2 }, bv = true };
var result = Make();
```

emits in the shape:

```lua
function Make() return 1, 2, true end
local result__s__v1, result__s__v2, result__bv = Make()
```

### Sub-Struct Assignment

When a sub-struct is assigned as a whole, the transpiler decomposes the right-hand side into its leaf fields and assigns them into the corresponding flattened locals:

```csharp
S1 temp = new S1 { v1 = 5, v2 = 6 };
s2.s = temp;
```

emits in the shape:

```lua
local temp__v1, temp__v2 = 5, 6
s2__s__v1, s2__s__v2 = temp__v1, temp__v2
```

### Passing a Sub-Field as Argument

```csharp
void Bar(S1 val) { … }
Bar(s2.s);
```

emits in the shape:

```lua
Bar(s2__s__v1, s2__s__v2)
```

### Design Considerations

- **Recursive structs are not a concern.** C# disallows value types that contain themselves without indirection (`struct Node { Node next; }` is a compile error), so the transpiler never encounters infinite recursion.
- **Name collision.** A user local `a__b` and a struct field path `a.b.c` that flattens to `a__b__c` could collide at the `a__b` prefix. In practice user locals rarely contain `__`, and the transpiler can add a reserved prefix to flattened names if isolation must be guaranteed.
- **Name length.** Three or four nesting levels produce long local names (`grandparent__parent__child__field`). This is cosmetic; Lua identifiers have no meaningful length limit in Warcraft III's runtime, and the names are unambiguous.
- **Structs in collections.** A `List<S2>` flattens into parallel arrays following the struct-of-arrays pattern (see "Structs in List\<T\>" above). Nested structs extend SoA naturally: one array per leaf field, indexed in lockstep.
- **Methods on nested structs.** Instance methods flatten `self` into leaf parameters at the deepest struct level: `S1.Magnitude(self__v1, self__v2)`. Calling `s2.s.Magnitude()` emits `S1.Magnitude(s2__s__v1, s2__s__v2)`.
- **Equality and casting.** The same constraints from single-level structs apply: no `is`/`as` on structs, no boxed `Equals`, no cast from `object` back to a struct shape. Nested structs do not relax these restrictions.

## Structs in Dictionary\<TKey, TValue\>

The current `Dictionary<TKey, TValue>` runtime stores entries as:

```lua
{ data = {}, keys = {}, version = 0 }
```

- `data` — a Lua table mapping keys to values. Lua table key lookup is O(1) amortized for string/number keys but uses reference equality for table keys.
- `keys` — an array preserving insertion order for stable iteration via `DictIterate__`.
- `version` — bumped on mutations, checked during iteration to detect modification.
- `DictNil__` — a sentinel table used to distinguish stored `nil` values from absent keys.

### Struct as Value: Dictionary\<T, Struct\>

When a struct appears as the value type, each stored value is a struct with no runtime identity. Unlike `List<T>` where index-based access makes parallel arrays natural, Dictionary access is key-based — the value is retrieved by looking up a key in `data`.

**SoA for values is problematic.** Splitting `data` into per-field parallel tables (`data__x`, `data__y`) means every `DictGet__` must repeat the key lookup N times:

```lua
-- hypothetical SoA DictGet for Vector2 value:
local x = dict.data__x[key]
local y = dict.data__y[key]
```

This doubles the table lookups for `Vector2`, triples for `Vector3`, etc. And iteration must reconstruct the struct from N parallel tables in lockstep.

**Table-literal boxing is the pragmatic choice.** Each struct value is boxed into a Lua table literal:

```lua
dict.data[key] = { x = 1, y = 2 }
```

```csharp
var pos = dict["hero"];  // pos is Vector2
```

```lua
local pos = dict.data["hero"]
-- pos.x, pos.y accessed as pos.x, pos.y
local x = pos.x
```

The existing `IRDictionaryGet`/`IRDictionarySet` nodes don't change — the value expression is a table literal. The boxing allocates one table per stored value, which is acceptable for typical WC3 map-script dictionary sizes (dozens to low hundreds of entries).

**Foreach over Dictionary\<T, Struct\>** yields `KeyValue<T, Struct>` items. The value portion of each item is a table literal reference, so `kvp.Value.x` accesses a Lua table field. No new IR nodes needed — the existing `IRDictionaryForEach` handles this.

**Nested struct values** box into nested table literals:

```lua
dict.data[key] = { s = { v1 = 1, v2 = 2 }, bv = true }
```

### Struct as Key: Dictionary\<Struct, V\>

This is the hard case. Lua tables use reference equality for table keys — two equivalent struct values boxed into separate tables are *different* keys:

```lua
local a = { x = 1, y = 2 }
local b = { x = 1, y = 2 }
local t = {}
t[a] = "first"
t[b] = "second"   -- different key; t now has TWO entries
```

A struct-keyed dictionary must produce a **stable, comparable representation** to use as a Lua table key. This requires hashing the struct's field values into a canonical form.

**String hashing.** Concatenate field values into a string key:

```lua
-- Vector2 → key "1|2"
local key = tostring(x) .. "|" .. tostring(y)
dict.data[key] = value
```

Problems with string hashing:

1. **Floating-point precision.** `tostring(1.0)` vs `tostring(1.00)` may differ, or `0.30000000000000004` is not `0.3`. A canonical float format is required.

2. **Iteration semantics break.** `DictIterate__` yields the hashed key (a string), not the original struct. This means:
   - `foreach (var kvp in dict)` would have `kvp.Key` as a string, not a `Vector2` — a type mismatch from the C# source.
   - `dict.Keys` would be strings.
   - There is no way to recover the original struct fields from the hashed key without a deserializer.

3. **Nested structs** produce long concatenated strings, compounding the precision and inspection issues.

**Can we recover the struct?** To make iteration yield proper struct-typed keys, the runtime would need to store the original field values alongside the hash. This requires one of:

- **Dual storage.** Maintain parallel key-field arrays (`keys__x`, `keys__y`) alongside the hash-keyed `data` table. During iteration, reconstruct the struct from the parallel arrays. This is the SoA pattern applied to keys — significant implementation effort but clean semantics.
- **Deserializer.** Generate a function to parse the hashed string back into field values. Fragile (field reordering breaks the parser) and expensive.

Neither is trivial. The dual-storage approach mirrors the List SoA design and could reuse the same flattening infrastructure, but the iteration path (`IRDictionaryForEach`) would need to be struct-aware.

**Diagnostic for v1.** Given the complexity and the established constraint that structs carry no runtime type metadata (see [type-casting](type-casting.md)), **struct-keyed dictionaries are not supported in the initial implementation**. The transpiler should emit a diagnostic when it detects a `Dictionary<Struct, …>` instantiation.

Users can work around this by using a primitive key and managing the struct separately:

```csharp
// Instead of Dictionary<Vector2, Unit>
// Use:
Dictionary<int, Unit> grid;       // key = hash(gridX, gridY)
// or
Dictionary<string, Unit> named;   // key = $"{x}:{y}"
```

**Future design sketch.** If struct keys become necessary, the dual-storage approach aligns with the existing flattening conventions:

```lua
dict = {
    data = { ["1|2"] = value1, … },   -- hash → value
    keys = { "1|2", … },              -- hashed keys in insertion order
    keys__x = { 1, … },               -- parallel field arrays for key reconstruction
    keys__y = { 2, … },
    version = 0
}
```

`DictIterate__` would yield `(reconstructedKey, value)` by reading from the parallel `keys__*` arrays. This requires a struct-aware variant of the dictionary helpers and IR nodes.

### Struct as Both: Dictionary\<Struct, Struct\>

Combines the two cases above:

- **Key side** → diagnostic for v1 (see "Struct as Key" above).
- **Value side** → table-literal boxing (see "Struct as Value" above).

If struct keys are later supported via dual storage, the value side remains boxed table literals. The two decisions are independent.