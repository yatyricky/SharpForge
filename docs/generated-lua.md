# Generated Lua

This page describes the Lua shape produced by `sf-transpile` and the small runtime helpers it may emit.

For lower-level design guidance behind these shapes, see [Transpiler Lowering Design Notes](lowering-design-notes.md).

## Transpiler Architecture

SharpForge treats C# input as a layered authoring surface that gets progressively simplified before Lua emission:

| Layer | Role | Examples |
| --- | --- | --- |
| 4. User code | Map and gameplay code written by project authors. | Game systems, triggers, abilities, project-specific helpers |
| 3. Library surface | Reusable C#-shaped APIs that user code can reference without turning SharpForge into a .NET compatibility layer. | `List<T>`, `Dictionary<K,V>`, `Vector3`, `SpellEvent`, interop stubs |
| 2. Normalization | Broad C# syntax conveniences are rewritten into simpler, Lua-oriented forms before final Lua emission. | Method groups become lambdas, `async`/`await` becomes coroutine flow, `action?.Invoke()` becomes an explicit nil-guarded call |
| 1. Lua emission | The final lowered representation is emitted as direct Lua with the smallest practical helper surface. | Root-table functions, tables, nil guards, coroutine helpers only when needed |

The intent is that the final Lua emitter stays small and predictable. Authoring ergonomics belong in user code, reusable concepts belong in libraries, and broad language features should usually be normalized before the last Lua-lowering layer sees them.

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
| `this(...)` constructor initializer | current type `__Init...` call on `self` before the constructor body |
| Object creation | `SF__.Type.New(...)` |
| Object initializer assignment | temporary object plus assignments, then returned temporary |
| `float` literal | emitted as its source-faithful decimal (`0.65f` → `0.65`, not `0.6499...`) |
| `double` literal | emitted with full round-trip precision |
| `string.Empty` | empty string literal (`""`) |
| Struct field-only local | flattened locals named `<local>__<field>` |
| Struct object return | Lua multi-return in field declaration order |
| Instance field access | `self.Field` |
| Static field initializer | `SF__.Type.Field = ...` |
| `const` field | static type-table field (`SF__.Type.Field = ...`) |
| String interpolation | nil-safe `SF__.StrConcat__(...)` calls; supported format clauses use `string.format(...)` |
| `try` / `catch` / `finally` | Lua `pcall` scaffolding with typed SharpForge exception headers |
| `is` / `as` | emitted type metadata helpers when needed |
| `??=` | nil-check assignment; expression form returns the existing or assigned value |
| `List<T>`, `Dictionary<K,V>` | library-layer APIs backed by explicit Lua/interoperability behavior |

C# line comments, block comments, and XML doc comments on lowered types, members, fields, and statements are emitted as Lua `--` comments near the corresponding generated code.

## Exceptions

SharpForge exceptions are represented as Lua error strings. The first 13 characters are a compile-time type header in the form `SF__E########`, where the hash-like suffix is derived from the C# exception type symbol. User-defined classes that inherit from `System.Exception` receive their own headers.

`throw new SomeException("boom")` lowers to an `error(...)` call whose message begins with that header. `catch` clauses compare the header, so multiple catches and catches of user-defined base exception types can dispatch without constructing a .NET exception object at runtime. A `catch (Exception)` clause catches any SharpForge exception header; unmatched Lua errors are rethrown after `finally` runs.

## Minimal Runtime Bias

SharpForge emits only helpers needed by the lowered code. Library APIs such as `List<T>` and `Dictionary<K,V>` belong above the final Lua emission layer, backed by explicit library or interop code rather than a broad translated runtime.

That bias keeps output small and predictable: use platform functions directly, model only what the generated Lua needs, and avoid a broad compatibility runtime.

## Build Integration

`sf-transpile` only emits Lua. It does not mutate `.w3x` files.

`sf-build` owns Lua dependency bundling and map injection. The separation keeps the C# compiler path independent from MPQ/archive work.

## API Reference

See [docs/api/](api/) for complete documentation of every supported language construct and how it lowers to Lua.