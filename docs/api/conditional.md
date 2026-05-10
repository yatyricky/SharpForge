# Conditional Expression

The ternary `?:` operator requires a helper because Lua has no equivalent single expression.

```csharp
int max = a > b ? a : b;
```

```lua
local max = SF__.Ternary__((a > b), a, b)
```

The `SF__.Ternary__` helper is emitted once when any ternary expression is used:

```lua
function SF__.Ternary__(cond, a, b)
    if cond then return a else return b end
end
```

## Nested Ternary

Nested ternary expressions work because each is wrapped in a `SF__.Ternary__` call:

```csharp
int v = a > 0 ? (b > 0 ? 1 : 2) : 3;
```

```lua
local v = SF__.Ternary__((a > 0), SF__.Ternary__((b > 0), 1, 2), 3)
```
