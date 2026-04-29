# SharpForge — repo facts (Copilot instructions)

## Layout
- `src/Transpiler/` → `sf-transpile.exe` (Roslyn → IR → Lua emitter)
- `src/Builder/` → `sf-build.exe` (stub: pack + inject .w3x)
- `src/JassGen/` → `sf-jassgen.exe` (common.j/blizzard.j → C# extern stubs on `static partial class SF__JASSGEN`, plus `global using static SF__JASSGEN;` shim)
- `tests/Transpiler.Tests/` xUnit
- `samples/` flat: `Hello.cs`, `Hero.cs` (no nested `cs_src/` or `expected/`)
- `assets/jass/` ships the JASS source for `sf-jassgen`

## Build
- .NET 10 SDK (10.0.202). Projects target `net8.0`. `TreatWarningsAsErrors`, single-file Exe.
- Single-file publish needs `Basic.Reference.Assemblies.Net80` (1.7.9) — `typeof(object).Assembly.Location` triggers IL3000.

## Key packages
- Microsoft.CodeAnalysis.CSharp 4.12.0 + .Workspaces 4.12.0
- System.CommandLine 2.0.0-beta4.22272.1 → use context-based `cmd.SetHandler(async ctx => { ... ctx.ExitCode = ... })`
- xUnit 2.9.2 + Microsoft.NET.Test.Sdk 17.11.1

## CLI surface (transpiler)
- Single command, no `build` verb: `sf-transpile <input-dir> [-o out.lua] [--check] [-r SF__] [-d SYM]... [-i CLASS]... [-v]`
- `--output` optional → defaults to `<input>/sf-out.lua`
- `--check` → run frontend+lower+emit but do NOT write file (lint mode); exit code reflects diagnostics
- `--ignore-class`/`-i` → class names to skip during Lua emit; defaults to `[SF__JASSGEN]` so the JASS-binding host class is not lowered. Repeatable.
- Exit codes: 0 ok, 1 compile errors, 2 input/output validation

## CLI surface (jassgen)
- `sf-jassgen <input-dir> [-o out-dir] [--host-class NAME] [-v]`
- `--host-class`/`-c` → name of the static partial class hosting natives + globals; default `SF__JASSGEN`. Must match the transpiler's `--ignore-class`.

## Lua emission contract
- ALL globals live under one root table (default `SF__`, configurable via `--root-table`)
- Namespace tables emitted idempotently: `SF__.X = SF__.X or {}` (each prefix once)
- Static methods → `function ROOT.NS.T.M(args)`
- Instance classes → `setmetatable({}, { __index = ROOT.NS.T })` + `function ROOT.NS.T:Method()` colon syntax
- Constructors → `.New(...)` returning `self`
- Implicit `this` field access rewritten via Roslyn semantic model in `IRLowering.LowerIdentifier`
- Binary/unary expressions ALWAYS parenthesized (Lua precedence differs from C# for `..`, `and`/`or`, bitwise)
- String interpolation → `..` chain, empty parts dropped (no `"" ..` prefix)
- Compound assignment expanded to `x = x op v`

## Test style
- Structural assertions only (`Assert.Contains` / `Assert.Matches` with regex tolerating parens)
- No byte-equal golden file checks
- `ClassSampleTests` reads `samples/Hero.cs` via `FindRepoRoot()` helper

## Output path validation
- Reject when `-o` points to existing directory (exit 2)
- Guard `Directory.CreateDirectory(null!)` when output is a bare filename
