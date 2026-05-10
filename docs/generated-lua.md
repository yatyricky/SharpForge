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
| `List<T>`, `Dictionary<K,V>` | compact table helper runtimes |

C# line comments, block comments, and XML doc comments on lowered types, members, fields, and statements are emitted as Lua `--` comments near the corresponding generated code.

## Minimal Runtime Bias

SharpForge emits only helpers needed by the lowered code. `List<T>` uses a compact Lua table with helper functions for add, count, indexing, iteration, and sorting. These are not full .NET collection surfaces translated into Lua.

That bias keeps output small and predictable: use platform functions directly, model only what the generated Lua needs, and avoid a broad compatibility runtime.

## Build Integration

`sf-transpile` only emits Lua. It does not mutate `.w3x` files.

`sf-build` owns Lua dependency bundling and map injection. The separation keeps the C# compiler path independent from MPQ/archive work.

## API Reference

See [docs/api/](api/) for complete documentation of every supported language construct and how it lowers to Lua.