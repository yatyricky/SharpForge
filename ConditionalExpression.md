# Conditional Expression (Ternary `?:`)

This page documents how `sf-transpile` lowers the C# conditional (ternary) expression.

## The Lua Pitfall

The Lua idiom that looks like a ternary:

```lua
local result = condition and value1 or value2
```

is **not safe**. When `value1` is a falsy value (`false` or `nil`), the expression
short-circuits and returns `value2` regardless of the condition:

```lua
local x = true and false or 99   -- returns 99, not false
local y = true and nil   or 99   -- returns 99, not nil
```

This silently corrupts the result for any branch that returns `false`, `0`-treated-as-false
in other languages, or any nullable/optional value.

## Lowering Strategy

SharpForge lowers every `? :` expression to a call to a strict helper function:

```lua
function SF__.Ternary__(cond, a, b)
    if cond then return a else return b end
end
```

This helper is emitted once at the top of the output file whenever any ternary
appears in user code. The helper is safe regardless of whether the branch values
are falsy.

### Example

```csharp
int Max(int a, int b) => a > b ? a : b;
string Label(bool flag) => flag ? "yes" : "no";
int FalsyBranch(bool flag) => flag ? 0 : 1;   // value1 == 0
```

emits:

```lua
function SF__.Ternary__(cond, a, b)
    if cond then return a else return b end
end

function SF__.Demo.Max(a, b)
    return SF__.Ternary__((a > b), a, b)
end

function SF__.Demo.Label(flag)
    return SF__.Ternary__(flag, "yes", "no")
end

function SF__.Demo.FalsyBranch(flag)
    return SF__.Ternary__(flag, 0, 1)
end
```

The falsy-branch case (`value1 = 0`) is handled correctly because `Ternary__`
evaluates the condition with a real `if` statement, not the `and/or` trick.

## IR Representation

The lowering step produces an `IRTernary(Condition, WhenTrue, WhenFalse)` node.
The emitter renders it as `SF__.Ternary__(condition, whenTrue, whenFalse)`.
