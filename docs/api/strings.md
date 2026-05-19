# Strings

## Concatenation

String `+` and string `+=` lower to `SF__.StrConcat__(...)`. The helper is emitted once into the output when needed:

```lua
function SF__.StrConcat__(...)
    local result = ""
    for i = 1, select("#", ...) do
        local part = select(i, ...)
        if part ~= nil then
            result = result .. tostring(part)
        end
    end
    return result
end
```

```csharp
string msg = "damage:" + damage + " count:" + count;
```

```lua
local msg = SF__.StrConcat__("damage:", damage, " count:", count)
```

If a concatenated part has a C# `ToString()` override on its class type, SharpForge lowers that part to a nil-safe method call before it reaches `SF__.StrConcat__(...)`.

```csharp
string msg = "[" + unitName + "]";
```

```lua
local msg = SF__.StrConcat__("[", (function()
    local strPart = unitName
    if (strPart ~= nil) then
        return strPart:ToString()
    end
    return nil
end)(), "]")
```

If a concatenated or interpolated part has a C# `ToString()` override on a struct type, SharpForge lowers it through the struct's emitted type method using the flattened self-field ABI.

```csharp
string msg = "[" + point + "]";
```

```lua
local msg = SF__.StrConcat__("[", SF__.Point.ToString(point__x, point__y), "]")
```

## Interpolation

Interpolated strings lower to `SF__.StrConcat__(...)` calls. Parts with no format clause are passed directly, except operands with a C# `ToString()` override: class-typed parts lower through the nil-safe `:ToString()` wrapper used by string `+`, while struct-typed parts lower through the emitted flattened struct method call. Parts with a supported format clause lower to `string.format(...)`.

```csharp
string s = $"damage:{damage:F0} count:{count:D3} id:{id:X2}";
```

```lua
local s = SF__.StrConcat__("damage:", string.format("%.0f", damage), " count:", string.format("%03d", count), " id:", string.format("%02X", id))
```

Supported format specifiers: `F`/`f` (fixed-point), `D`/`d` (decimal integer), `X`/`x` (hexadecimal), each with optional width/precision digits. Any other .NET format string produces a transpiler error.

## Verbatim Strings

Verbatim string literals (`@"..."`) are supported and lower to Lua string literals.

## Unsupported

`string.Format(...)`, `.ToString(format)`, and other .NET string methods not listed here produce a transpiler error.
