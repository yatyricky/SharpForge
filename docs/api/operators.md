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

User-defined operator overloads lower to static Lua functions. When multiple overloads share the same operator name, SharpForge appends the same simplified parameter-signature suffix used for overloaded methods.

```csharp
public static Vector3 operator *(Vector3 v, float f) => new(v.x * f, v.y * f, v.z * f);
public static Vector3 operator *(float f, Vector3 v) => new(v.x * f, v.y * f, v.z * f);
```

```lua
function SF__.Vector3.op_Multiply__vector3f(v__x, v__y, v__z, f)
	return (v__x * f), (v__y * f), (v__z * f)
end

function SF__.Vector3.op_Multiply__fvector3(f, v__x, v__y, v__z)
	return (v__x * f), (v__y * f), (v__z * f)
end
```

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
