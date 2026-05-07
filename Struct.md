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

If a whole struct value is still needed as an expression, object creation falls back to a plain Lua table literal with the struct fields:

```lua
local v = {x = 10, y = 5}
```

Empty struct type blocks are skipped. A struct with static members can still emit a root-table type entry for those members, but constructors are still omitted.

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

## Todo

- Structs may still need to emit a type/class table when methods are defined.
- That table should hold the lowered struct methods.
- Static struct methods should lower to functions on that table.
- Instance struct methods should lower as static functions on that table, with the first parameter representing `self`.
