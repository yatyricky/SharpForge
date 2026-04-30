# SharpForge

> Industrial-grade C# scripting toolchain for **Warcraft III: Reforged** map development.
> *Do one thing and do it well — per sub-project.*

SharpForge lets veteran authors write map logic in modern, strongly-typed C# (full IDE support, static checks, AI-assist) and ship it as a single Lua file injected into a `.w3x` map. Existing Lua code keeps working alongside.

---

## Philosophy

### Target audience

Veteran map authors who:

- already know JASS / Lua and have shipped complex maps,
- are tired of weak typing and zero debugging tools in large projects,
- do **not** want to learn a new language or replace the World Editor.

**Out of scope:** absolute beginners, one-click map generators, hardcore exploit / cheat developers.

### Problems solved

- **Stagnant scripting stack.** Replace vJASS / Zinc and untyped Lua with the modern C# / .NET ecosystem.
- **Maintenance hell.** Strong types, modular design, and modern tooling tame large-map codebases.
- **World Editor limits.** Bypass the "custom script" size limits (and the crashes they cause) by building externally and importing.
- **Desync risk.** Built-in thread-safe containers and deterministic codegen reduce multiplayer desync from the ground up.
- **Asset reuse / customization.** Conditional compilation can produce many tailored variants of a map from one core codebase.
- **Callback hell** → `async`/`await` state machines.
- **Zero debug tools** → FDF Inspector — Unity-Inspector-style live UI for WC3R, bringing game-industry workflows to the community.

### Key advantages

- **Future-proof.** Brings Unity-grade DX to the WC3R ecosystem on top of a mature C# toolchain.
- **AI-friendly.** Strong types massively boost AI assistants (e.g. GitHub Copilot) at generating and reasoning about map code.
- **Performance.** Compile-time analysis and tree-shaking strip unused code, producing minimal Lua output.
- **Incremental migration.** New C# code interoperates with legacy Lua so projects can be ported gradually.

### Differentiation vs. War3Net

War3Net takes a **full-replacement** approach (rewrite the workflow, replace the editor — high learning cost). SharpForge is **incremental enhancement**: start with one trigger in C#, migrate progressively, never rewrite the whole map. Lower adoption barrier, matches the "give me tools, not ceremony" mindset of veteran authors.

### Final value

SharpForge defines an industrial, sustainable modernization standard for WC3R map development. It is more than a transpiler — it is a paradigm upgrade that lets old projects "live again" while lowering the bar for new contributors.

---

## Repository layout

```
SharpForge/
├── src/
│   ├── Transpiler/   # sf-transpile.exe  — C# → Lua compiler
│   └── Builder/      # sf-build.exe      — Lua bundler + .w3x injector
├── tests/
│   └── Transpiler.Tests/
├── samples/
│   ├── Samples.csproj # standalone C# project for editor IntelliSense
│   ├── Hello.cs       # smallest static-class example
│   └── Hero.cs        # namespace + instance class + ctor + interpolation sample
└── SharpForge.sln
```

`samples/` is intentionally not included in `SharpForge.sln`; open or build `samples/Samples.csproj` directly when editing sample scripts.

### Build

Requires .NET 10 SDK (verified on 10.0.202+). Projects target `net10.0`.

```powershell
dotnet build SharpForge.sln
dotnet test  tests\Transpiler.Tests\Transpiler.Tests.csproj
```

Single-file publish:

```powershell
.\publish-all.ps1
```

---

## The `SF__` root table (single global contract)

Every transpiled namespace and type lives under **one** configurable top-level Lua table — default `SF__`. This is the *only* global SharpForge writes, so generated code never collides with `war3map.lua`, or hand-written Lua libraries.

```lua
-- SharpForge top level
SF__ = SF__ or {}
-- SharpForge Game namespace
SF__.Game = SF__.Game or {}
-- SharpForge Class: Hero
SF__.Game.Hero = SF__.Game.Hero or {}
```

Override the name via the CLI (e.g. to satisfy a project's naming convention, or to ship multiple SharpForge bundles in one map):

```powershell
sf-transpile .\src -o .\out\sharp_forge.lua --root-table MyMod_
```

Legacy Lua interop uses the same convention (`__SF = __SF or {}; __SF.HeroSystem = HeroSystem`) so existing modules can be exposed back to transpiled C# code.

---

## Sub-projects

> Each sub-project compiles to a single, self-contained `.exe`.

### `Transpiler` (`sf-transpile.exe`)

Core thesis: *let WC3R authors write logic with industrial tooling — no workflow change, no Lua editing, no desyncs.*

A C# → Lua transpiler. Authors write strongly-typed C# with full IDE support, static type checking, and AI-assisted editing; SharpForge bundles many `.cs` files into a single `.lua` with dependency analysis, topological sort, conditional compilation, and tree-shaking that significantly shrinks the final script.

#### Pipeline

```
C# sources ──► Roslyn frontend ──► SharpForge IR ──► LuaEmitter ──► .lua
                    │                                       │
                    └──► Desync Linter (Roslyn analyzers)   └──► line-number Source Map
```

Key decisions:

- **Frontend:** Roslyn (C# 12 / latest, `Microsoft.CodeAnalysis.CSharp` 4.12).
- **Backend:** custom IR + emitter, all C#.
- **Output:** pure `.lua` — no MPQ / `.w3x` mutation, zero invasion.
- **Runtime:** pure Lua — no external DLLs, no `mscorlib`.
- **IDE:** VS Code by default (lightweight, cross-platform, customizable).

#### Design principles

- **Incremental, non-disruptive.** WE places units, C# writes logic, output is Lua.
- **Respect the platform — don't over-abstract.** `CreateUnit` is *not* renamed `Unit.Create`.
- **Only do C# → Lua transpilation.** No bundled runtime, no shadow standard library.

#### CLI

```powershell
sf-transpile <input-dir> [--output <out.lua>]   # default: <input>/sf-out.lua
                         [--check]               # lint only, no file written
                         [--root-table SF__]
                         [--define SYMBOL]...
                         [--verbose]

sf-build <entry.lua>                              # default output: <entry-dir>/bundle.lua
         [--output <map.w3x | map.w3x-folder | folder>]
         [--include <path1.lua;path2.lua>]
         [--csharp <cs-dir>]                      # transpile C# first, then bundle
         [--root-table SF__]
         [--verbose]
```

#### Currently implemented (lowering & emit)

| Feature                            | Status | Notes                                                    |
| ---------------------------------- | :----: | -------------------------------------------------------- |
| Static classes & methods           | ✅     | `function SF__.T.M(...)`                                 |
| Namespaces → nested tables         | ✅     | Each prefix emitted once, idempotent (`= … or {}`)       |
| Instance classes                   | ✅     | Single root table, `setmetatable` + `__index` for OO     |
| Constructors → `.New(...)`         | ✅     | Auto-injected `local self = setmetatable(...)`           |
| Instance methods → `:Method()`     | ✅     | Lua passes `self` implicitly                             |
| Object creation                    | ✅     | `new T(...)` → `ROOT.NS.T.New(...)`                      |
| Implicit `this` field access       | ✅     | Roslyn semantic-model rewrites bare `Field` to `self.X`  |
| Compound assignment (`+=`, etc.)   | ✅     | Expanded to `x = x op v`                                 |
| String interpolation `$"..."`      | ✅     | Lowered to `..` chain, empty parts dropped               |
| Control flow: `if` / `while` / `for` | ✅   | `break` / `continue` (`continue` → `goto continue`)      |
| Static field initializers          | ✅     | Emitted as `T.X = ...`                                   |
| Auto-properties                    | ✅     | Lowered as fields for the current MVP                    |
| Static constructors                | ✅     | Emitted after static member initializers                 |
| Constructor/method overload names  | ✅     | Arity-suffixed Lua names when overloads exist            |
| Single inheritance                 | ✅     | Derived class tables inherit through Lua metatables      |
| `virtual` / `override` methods     | ✅     | Override methods replace inherited dispatch entries      |
| `base(...)` / `base.Method()`      | ✅     | Base constructor init helpers and direct base calls       |
| Exception MVP                      | ✅     | `try` / single `catch` / `finally` via Lua `pcall`       |
| Interfaces + `is` / `as`           | ✅     | Type metadata checks against class/interface tables       |
| Collection MVP                     | ✅     | Arrays/`List<T>` as tables, `foreach`, `Count`, indexing  |
| Vector-style struct MVP            | ✅     | Struct constructors/fields plus user-defined operators    |
| Properties/indexers/delegates      | ✅     | Computed accessors, indexer methods, direct delegate calls |
| Event declarations                 | ✅     | Field-like event backing for current MVP                  |
| Async/iterator MVP                 | ✅     | Synchronous `await`; simple `yield return` table materialization |
| Type-check helper optimization     | ✅     | Emits `is` / `as` helpers only when needed                |
| Toolchain pack integration         | ✅     | `sf-build` bundles Lua and optional transpiled C#          |
| Custom root-table name             | ✅     | `--root-table` (default `SF__`)                          |
| Unsupported syntax diagnostics     | ✅     | `--check` exits non-zero with source locations           |
| Topological multi-file ordering    | ✅     | Entry-script dependency graph ordered dependency-first    |
| Conditional compilation directives | ⏳     | Frontend accepts `--define`; emit pruning planned        |
| Tree-shaking                       | ✅     | Unreachable Lua files are excluded by `sf-build`          |
| Source-map line annotations        | ⏳     | Planned                                                  |
| Generics, `async`/`await`          | ⏳     | Planned                                                  |

#### Desync Linter (headline feature)

Custom rules that flag sync-unsafe C# at compile time:

| Rule    | Description                                                    | Severity |
| :------ | -------------------------------------------------------------- | :------: |
| W3R0001 | Frame handles must not be local variables                      | Error    |
| W3R0002 | Sync-sensitive APIs must not appear inside anonymous closures  | Error    |
| W3R0003 | LocalOnly API return values must not flow into sync contexts   | Error    |
| W3R0005 | Prefer reusing Frames over `BlzDestroyFrame`                   | Warning  |
| W3R0006 | `GetLocalPlayer` must not be used for non-visual operations    | Error    |
| W3R0009 | Global `Location`/`Hashtable` must not initialize at declaration | Error  |

Status: skeleton (`DesyncLinter`) wired; W3R0006 stubbed. Full rule pack and analyzer-package shipping are planned.

#### Debug system

**FDF Inspector** — virtual-scrolling, dirty-marked refresh; auto-generated from `[Header]` / `[Button]` attributes (Unity-Inspector vibe, in-game).

#### Lua interop (the migration enabler)

Three tiers:

1. **Zero-config:** `Lua.Call<T>("module.fn", args)` — works immediately, weakly typed.
2. **Per-user precision:** hand-written `*.extern.cs` shims (only for hot modules).
3. **Official precision:** `War3Api.cs` — generated by SharpForge from `common.j` / `blizzard.j`.

Existing Lua exports follow the standard pattern `__SF = __SF or {}; __SF.HeroSystem = HeroSystem`, mirroring the SharpForge root-table convention so both directions of interop look symmetric.

### `Builder` (`sf-build.exe`)

1. starts from `<entry.lua>`, recursively follows literal script dependencies, topologically sorts them, emits one bundle, and loads the entry module when the bundle runs.

Dependency discovery supports literal `require`, `dofile`, `doFile`, `loadfile`, `loadFile`, `package.load`, `include`, `import`, and `load` calls with single or double quoted paths. Module paths may use `/`, `\`, or `.` separators. Regular commented-out calls are ignored; prefix a comment with `!` to force a dependency, for example `-- !require('Path.To.Module')`. Calculated paths are intentionally not evaluated; list extra scripts with `--include path/a.lua;path/b.lua`.

`Injection`: Wrap the whole bundle with `--sf-builder:000106309/7f181df892ea2f9e \nfunction SF__Bundle() <bundle.lua code>; require("Entry") end \n--sf-builder:000106309/7f181df892ea2f9e`; splices `local s, m = pcall(SF__Bundle) if not s then print(m) end` into the end of `function main()` in `war3map.lua`, replacing any previous SharpForge bundle block by identifying the `--sf-builder:<script length>/<script checksum>` comments.

Target behavior:

- No `-o, --output` writes `bundle.lua` next to the entry script.
- `-o, --output <map.w3x>` runs `Injection` into `war3map.lua` inside the MPQ archive.
- `-o, --output <map.w3x-folder>` runs `Injection` into `<map.w3x-folder>/war3map.lua` for folder maps.
- `-o, --output <folder>` writes `<folder>/bundle.lua` when the folder is not a `.w3x` folder.
- `-o, --output <**/war3map.lua>` runs `Injection` into the file.
- Non-`.w3x` file targets are rejected with exit code 2.

Archive mutation uses the managed `War3Net.IO.Mpq` package, giving StormLib-style MPQ read/write behavior without shipping a native DLL beside `sf-build`.

---

## Samples

| Sample          | Demonstrates                                                                |
| --------------- | --------------------------------------------------------------------------- |
| `samples/Hello` | Smallest static class, `if` / arithmetic / `return`                         |
| `samples/Class` | Namespace, instance class, constructor, instance methods, `+=`, `$"..."`    |

`samples/Class/expected/sharp_forge.lua` is the **golden** output and is verified by `ClassSampleTests` on every test run.

---

## License

[MIT](LICENSE) © 2026 SharpForge contributors.

## AI assistance

Significant portions of this codebase were drafted, refactored, and reviewed with AI pair-programming:

- **Editor:** [VS Code](https://code.visualstudio.com/) with GitHub Copilot Chat (agent mode).
- **Model:** Anthropic **Claude Opus 4.7**.

All AI-generated changes were reviewed, compiled, and tested by a human before commit. Project conventions, design decisions, and acceptance criteria are owned by the maintainers, not the model.

