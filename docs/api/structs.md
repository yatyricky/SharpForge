# Structs

Structs in SharpForge are flattened at their use sites. A struct local is not a Lua table; its fields become individual local variables.

## Field Flattening

```csharp
public struct Point
{
    public float X;
    public float Y;
}

Point p = new Point { X = 1.0f, Y = 2.0f };
float x = p.X;
```

```lua
local p__X = 1.0
local p__Y = 2.0
local x = p__X
```

## Multi-Return

Functions that return a struct return the fields as multiple Lua values in declaration order.

```csharp
public static Point GetOrigin()
{
    return new Point { X = 0.0f, Y = 0.0f };
}
```

```lua
function SF__.MyClass.GetOrigin()
    return 0.0, 0.0
end
```

Assigning the result of a struct-returning call uses multi-return destructuring:

```csharp
Point p = GetOrigin();
```

```lua
local p__X, p__Y = SF__.MyClass.GetOrigin()
```

## Struct Parameters

Struct parameters are flattened at the call site and unpacked at the function definition.

```csharp
public static float Distance(Point a, Point b)
{
    float dx = a.X - b.X;
    float dy = a.Y - b.Y;
    return MathF.Sqrt(dx * dx + dy * dy);
}
```

```lua
function SF__.MyClass.Distance(a__X, a__Y, b__X, b__Y)
    local dx = (a__X - b__X)
    local dy = (a__Y - b__Y)
    return MathF.Sqrt(((dx * dx) + (dy * dy)))
end
```

## Struct Methods

Instance methods on a struct receive the flattened fields as individual parameters and return the modified fields as multiple values if the struct is mutated.

## SoA List\<Struct\>

`List<T>` where `T` is a struct uses a Structure-of-Arrays (SoA) layout: one parallel array per field. Element access and iteration unpack the per-field arrays.

## Unsupported

Nested structs (a struct containing another struct as a field), `ref struct`, and passing structs to non-SharpForge code produce a transpiler error.
