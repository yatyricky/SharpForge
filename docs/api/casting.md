# Casting

## Explicit Cast

An explicit cast between numeric types and between a class and its base/derived type lowers to the expression itself — the cast is erased.

```csharp
float f = (float)intValue;
Hero h = (Hero)unit;
```

```lua
local f = intValue
local h = unit
```

## is

`is` checks emit a type metadata lookup using a `SF__.TypeIs__` helper:

```lua
function SF__.TypeIs__(obj, target)
    if obj == nil then return false end
    local type = obj.__sf_type
    while type ~= nil do
        if type == target then return true end
        if type.__sf_interfaces ~= nil and type.__sf_interfaces[target] then return true end
        type = type.__sf_base
    end
    return false
end

function SF__.TypeAs__(obj, target)
    if SF__.TypeIs__(obj, target) then return obj end
    return nil
end
```

```csharp
if (unit is Hero)
{
    DoHeroThings();
}
```

```lua
if SF__.TypeIs__(unit, SF__.Hero) then
    DoHeroThings()
end
```

## as

`as` lowers to a conditional return of the value or `nil`.

```csharp
Hero? h = unit as Hero;
```

```lua
local h = SF__.TypeAs__(unit, SF__.Hero)
```

## Unsupported

`checked` casts, `unchecked` casts, pattern matching with `is` (e.g., `unit is Hero h`), and `switch` pattern matching produce a transpiler error.
