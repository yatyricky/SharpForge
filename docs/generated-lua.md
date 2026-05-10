# Generated Lua

This page describes the Lua shape produced by `sf-transpile` and the small runtime helpers it may emit.

## Root Table Contract

Every transpiled namespace and type lives under one configurable root table. The default is `SF__`.

```lua
SF__ = SF__ or {}
SF__.Game = SF__.Game or {}
SF__.Game.Hero = SF__.Game.Hero or {}
```

Override the root table with:

```powershell
sf-transpile .\CSProject -o .\out.lua --root-table MyMod_
```

SharpForge writes generated C# types under this one root table so it does not collide with hand-written Lua modules or `war3map.lua` globals.

## Lowering Shape

Common C# constructs lower to direct Lua patterns:

| C# concept | Lua shape |
| --- | --- |
| Namespace | nested root-table fields |
| Static method | `function SF__.Type.Method(...)` |
| Instance method | `function SF__.Type:Method(...)` |
| Constructor | `SF__.Type.New(...)` with `setmetatable` |
| Object creation | `SF__.Type.New(...)` |
| `float` literal | emitted as its source-faithful decimal (`0.65f` → `0.65`, not `0.6499...`) |
| `double` literal | emitted with full round-trip precision |
| Struct field-only local | flattened locals named `<local>__<field>` |
| Struct object return | Lua multi-return in field declaration order |
| Instance field access | `self.Field` |
| Static field initializer | `SF__.Type.Field = ...` |
| String interpolation | nil-safe `SF__.StrConcat__(...)` calls; supported format clauses use `string.format(...)` |
| `try` / `catch` / `finally` | Lua `pcall` scaffolding |
| `is` / `as` | emitted type metadata helpers when needed |
| `List<T>`, `Dictionary<K,V>`, `Queue<T>`, `Stack<T>`, `HashSet<T>` | compact table helper runtimes |

C# line comments, block comments, and XML doc comments on lowered types, members, fields, and statements are emitted as Lua `--` comments near the corresponding generated code.

## String Interpolation Formats

Interpolated strings are emitted through the nil-safe `SF__.StrConcat__(...)` helper. Format clauses for fixed-point, decimal integer, and hexadecimal values are mapped to Lua `string.format(...)`:

```csharp
return $"damage:{damage:F0} count:{count:D3} id:{id:X2}";
```

```lua
return SF__.StrConcat__("damage:", string.format("%.0f", damage), " count:", string.format("%03d", count), " id:", string.format("%02X", id))
```

Supported interpolation format specifiers are `F`/`f`, `D`/`d`, and `X`/`x`, each with optional precision. Other .NET format strings produce a transpiler diagnostic instead of silently changing formatting semantics.

## Debugger Probes

Decorate a method with `SFLib.Diagnostics.DebuggerAttribute` to ask the transpiler to insert Warcraft debug messages between source statements:

```csharp
using SFLib.Diagnostics;

public static class Waves
{
	[Debugger]
	public static void Spawn(int wave, string name)
	{
		var count = wave + 1;
		count += 2;
	}
}
```

The generated Lua includes `BJDebugMsg` calls between statements. Probe labels use `{Class.Method step N}` and include visible values that are cheap and predictable to stringify, such as primitive, enum, `string`, and `bool` parameters or locals:

```lua
local count = (wave + 1)
BJDebugMsg(SF__.StrConcat__("{Waves.Spawn step 1} {wave=", wave, " name=", name, " count=", count, "}"))
count = (count + 2)
```

SharpForge intentionally skips object, collection, and struct values in automatic debugger probes. Use explicit `BJDebugMsg(...)` or `LuaInterop.CallGlobal("BJDebugMsg", ...)` when you need custom formatting for larger values.

## Current Coverage

Implemented lowering includes:

- static classes and methods
- namespaces
- instance classes
- constructors and object creation
- instance methods with colon calls
- implicit `this` field access
- compound assignment
- string interpolation, including `F`/`D`/`X` format clauses
- `if`, `while`, `for`, `foreach`, `break`, and `continue`
- static field initializers and static constructors
- auto-properties as fields
- constructor and method overload name suffixes
- single inheritance, `virtual`, and `override`
- `base(...)` and `base.Method()`
- exception MVP with one `catch` and `finally`
- interfaces, `is`, and `as`
- arrays and minimal `List<T>`, `Dictionary<K,V>`, `Queue<T>`, `Stack<T>`, and `HashSet<T>` helpers
- struct field flattening and multi-return lowering; see [struct](struct.md)
- validated `Regex.IsMatch` subset; see [regular expressions](regex.md)
- computed properties, indexers, delegates, and field-like events
- synchronous `await` and simple `yield return` materialization
- custom root table names
- `[Debugger]` method probes using `BJDebugMsg`
- unsupported syntax diagnostics

Planned or partial areas include broader conditional pruning, source-map line annotations, and deeper generic/async coverage.

## Minimal Runtime Bias

SharpForge emits only helpers needed by the lowered code. For example, `List<T>` uses a compact Lua table with helper functions for add, count, indexing, iteration, and sorting, while `HashSet<T>` uses membership helpers over key tables. These are not full .NET collection surfaces translated into Lua.

That bias keeps output small and predictable: use platform functions directly, model only what the generated Lua needs, and avoid a broad compatibility runtime.

## Build Integration

`sf-transpile` only emits Lua. It does not mutate `.w3x` files.

`sf-build` owns Lua dependency bundling and map injection. The separation keeps the C# compiler path independent from MPQ/archive work.