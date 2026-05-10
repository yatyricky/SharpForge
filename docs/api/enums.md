# Enums

Enum members lower to integer constants under the type's Lua table.

```csharp
public enum Rarity
{
    Common = 0,
    Rare = 1,
    Epic = 2,
}
```

```lua
SF__.Rarity = SF__.Rarity or {}
SF__.Rarity.Common = 0
SF__.Rarity.Rare = 1
SF__.Rarity.Epic = 2
```

Enum values are used directly as integers in generated code:

```csharp
Rarity r = Rarity.Rare;
```

```lua
local r = SF__.Rarity.Rare
```

## Flags Enums

`[Flags]` enums produce a transpiler diagnostic. Bitwise enum operations are not supported.
