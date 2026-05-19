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
Expression-bodied methods and property getters use the same shape, including target-typed creation such as `public static Point Origin => new(0, 0);`.

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
Assignments to flattened struct fields also expand to field assignments. For example, a class field `position` of struct type lowers to backing fields such as `self.position__X` and `self.position__Y`, and `position = new Point { X = 1, Y = 2 };` assigns those backing fields directly.
When the struct field is accessed through another object, that receiver is preserved: `trs.position = value;` assigns `trs.position__X` and `trs.position__Y`, not `self.position__X` and `self.position__Y`.
The same rule applies across files: reads such as `gameObject.transform.position.X` expand to `gameObject.transform.position__X` regardless of whether `Transform` is declared before or after the current type in source order.

The flattened local remains flattened when passed to another SharpForge method that accepts the same struct type:

```csharp
DrawPoint(p);
```

```lua
SF__.MyClass.DrawPoint(p__X, p__Y)
```

The same flattened local also stays expanded when assigned into another flattened struct target:

```csharp
other.Position = p;
```

```lua
other.position__X, other.position__Y = p__X, p__Y
```

Struct-returning SharpForge calls are already multi-return values. When passed to another SharpForge method or operator that accepts that struct, the call itself is forwarded instead of treating the result like a Lua table.

```csharp
public Point Clamp(float scale)
{
    return Normalized * scale;
}
```

```lua
return SF__.Point.op_Multiply__pointf(SF__.Point.get_Normalized(self__X, self__Y), scale)
```

Lua only preserves all returned values when a call stays in the final argument position. If a struct-returning SharpForge call feeds another struct parameter before later arguments, SharpForge spills the returned fields into temporary locals first.

```csharp
return (a + b) + (c + d);
```

```lua
local left__X, left__Y = SF__.Point.op_Addition(a__X, a__Y, b__X, b__Y)
return SF__.Point.op_Addition(left__X, left__Y, SF__.Point.op_Addition(c__X, c__Y, d__X, d__Y))
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

Computed instance properties on a struct use the same flattened receiver shape as instance methods. A property read such as `(left - right).Magnitude` calls the generated getter with the struct field values instead of using Lua `:` syntax on a table receiver.

## SoA List\<Struct\>

`List<T>` where `T` is a struct uses a Structure-of-Arrays (SoA) layout: one parallel array per field. Element access and iteration unpack the per-field arrays.

## Unsupported

Nested structs (a struct containing another struct as a field), `ref struct`, and passing structs to non-SharpForge code produce a transpiler error.
