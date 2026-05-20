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

`is null` and `is not null` lower to direct `nil` checks. The nullable suppression operator is erased before lowering, so `value! is not null` has the same runtime shape as `value is not null`.

```csharp
return value! is not null;
```

```lua
return (not (value == nil))
```

Inside generic methods, `is T name` in an `if` condition lowers to a `TypeIs__` check against the hidden runtime type-argument parameter and binds `name` to the tested value for the `if` body.

```csharp
if (component is T typed)
{
    return typed;
}
```

```lua
do
    local typed = component
    if SF__.TypeIs__(typed, T) then
        return typed
    end
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

## GetType and Runtime Type Metadata

Every emitted type table receives `Name` and `FullName` metadata fields:

```lua
SF__.MyName.Queue = SF__.MyName.Queue or {}
SF__.MyName.Queue.Name = "Queue"
SF__.MyName.Queue.FullName = "MyName.Queue"
```

Static fields named `Name` or `FullName` are emitted later and can intentionally overwrite these defaults.

`GetType()` on a class instance lowers to the runtime type metadata stored on the object:

```csharp
var q = new Queue();
var t = q.GetType();
```

```lua
local q = SF__.MyName.Queue.New()
local t = q.__sf_type
```

`Type.Name` and `Type.FullName` lower to runtime metadata field reads:

```csharp
BJDebugMsg(t.Name);
BJDebugMsg(t.FullName);
BJDebugMsg(q.GetType().FullName);
```

```lua
BJDebugMsg(t.Name)
BJDebugMsg(t.FullName)
BJDebugMsg(q.__sf_type.FullName)
```

## Unsupported

`checked` casts, `unchecked` casts, declaration patterns with binding outside an `if` condition (e.g., `return unit is Hero h`), `switch` pattern matching, `GetType()` on structs, and `System.Type` properties other than `Name` and `FullName` produce a transpiler error.
