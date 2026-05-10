# Operators

## Binary Operators

Standard C# binary operators are emitted directly. All binary expressions are explicitly parenthesized because Lua operator precedence differs from C#.

```csharp
int result = (a + b) * c;
bool eq = x == y;
bool gt = x > y;
```

```lua
local result = ((a + b) * c)
local eq = (x == y)
local gt = (x > y)
```

Supported: `+`, `-`, `*`, `/`, `%`, `==`, `!=`, `<`, `<=`, `>`, `>=`, `&&`, `||`, `&`, `|`, `^`, `<<`, `>>`.

`!=` lowers to `~=` in Lua. `&&` lowers to `and`, `||` lowers to `or`.

## Unary Operators

```csharp
bool b = !flag;
int n = -x;
```

```lua
local b = (not flag)
local n = (-x)
```

## Compound Assignment

`+=`, `-=`, `*=`, `/=`, `%=` lower to expanded assignment:

```csharp
count += 1;
```

```lua
count = (count + 1)
```

## String Concatenation

The `+` operator on strings lowers to the nil-safe `SF__.StrConcat__(...)` helper. See [strings.md](strings.md).

## Unsupported Operators

`??`, `??=`, `?[]`, bitwise assignment (`&=`, `|=`, `^=`, `<<=`, `>>=`), and checked arithmetic produce a transpiler error.
