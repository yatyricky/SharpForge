# Types

## Primitives

SharpForge supports `int`, `float`, `double`, `bool`, `string`, and `null`. These map directly to Lua's numeric, boolean, string, and nil types.

| C# type | Lua type |
| --- | --- |
| `int` | integer |
| `float` | number |
| `double` | number |
| `bool` | boolean |
| `string` | string |
| `null` | nil |

## Literals

`float` literals are emitted source-faithfully (`0.65f` → `0.65`, not `0.6499...`). `double` literals are emitted with full round-trip precision.

```csharp
int i = 42;
float f = 0.65f;
bool flag = true;
string s = "hello";
```

```lua
local i = 42
local f = 0.65
local flag = true
local s = "hello"
```

## Unsupported Numeric Types

`long`, `short`, `byte`, `uint`, `ulong`, `decimal`, and other numeric types not listed above produce a transpiler error.
