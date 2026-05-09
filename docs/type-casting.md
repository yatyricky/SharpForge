## Explicit Casting

Explicit casts between non-struct values are erased when lowering to Lua. They are treated as compile-time C# conveniences, not runtime conversion operations.

Struct casts are diagnostics. SharpForge structs are compile-time value shapes that may be flattened into multiple Lua values, so a runtime value such as `object` cannot be cast back into a struct shape.

Unsupported examples:

```csharp
var data = (AbilityData)obj;
object boxed = data;
```

## `is` / `as`

Class and interface `is` / `as` checks use generated type metadata helpers:

```lua
SF__.TypeIs__(obj, SF__.ClassName)
SF__.TypeAs__(obj, SF__.ClassName)
```

Struct `is` / `as` checks are diagnostics. Structs do not carry runtime type metadata such as `__sf_type`, and the transpiler cannot recover flattened fields from an arbitrary runtime value.

Unsupported examples:

```csharp
if (obj is AbilityData) { }
if (obj is AbilityData data) { }
```

## Struct Equality

Do not use boxed equality for structs. These patterns are diagnostics:

- `Equals(object)` on a struct
- `GetHashCode()` on a struct

`IEquatable<T>` may be written on a struct for IntelliSense and C# type-checking, but it is erased during lowering. SharpForge does not emit `__sf_interfaces` metadata for struct tables because flattened structs do not participate in runtime interface checks.

Use explicit typed comparison methods instead:

```csharp
public bool ApproximatelyEquals(AbilityData other)
{
	return math.abs(DamageScaling - other.DamageScaling) < 0.001f;
}
```
