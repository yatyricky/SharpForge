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
| String interpolation | Lua `..` concatenation |
| `try` / `catch` / `finally` | Lua `pcall` scaffolding |
| `is` / `as` | emitted type metadata helpers when needed |
| `List<T>` | compact table helper runtime |

C# line comments, block comments, and XML doc comments on lowered types, members, fields, and statements are emitted as Lua `--` comments near the corresponding generated code.

## Current Coverage

Implemented lowering includes:

- static classes and methods
- namespaces
- instance classes
- constructors and object creation
- instance methods with colon calls
- implicit `this` field access
- compound assignment
- string interpolation
- `if`, `while`, `for`, `foreach`, `break`, and `continue`
- static field initializers and static constructors
- auto-properties as fields
- constructor and method overload name suffixes
- single inheritance, `virtual`, and `override`
- `base(...)` and `base.Method()`
- exception MVP with one `catch` and `finally`
- interfaces, `is`, and `as`
- arrays and minimal `List<T>`/dictionary helpers
- struct field flattening and multi-return lowering; see [struct](struct.md)
- validated `Regex.IsMatch` subset; see [regular expressions](regex.md)
- computed properties, indexers, delegates, and field-like events
- synchronous `await` and simple `yield return` materialization
- custom root table names
- unsupported syntax diagnostics

Planned or partial areas include broader conditional pruning, source-map line annotations, and deeper generic/async coverage.

## Minimal Runtime Bias

SharpForge emits only helpers needed by the lowered code. For example, `List<T>` uses a compact Lua table with helper functions for add, count, indexing, iteration, and sorting. It is not a full .NET `List<T>` surface translated into Lua.

That bias keeps output small and predictable: use platform functions directly, model only what the generated Lua needs, and avoid a broad compatibility runtime.

## Build Integration

`sf-transpile` only emits Lua. It does not mutate `.w3x` files.

`sf-build` owns Lua dependency bundling and map injection. The separation keeps the C# compiler path independent from MPQ/archive work.