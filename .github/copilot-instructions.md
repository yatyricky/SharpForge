# SharpForge Agent Notes

For product context and user-facing behavior, start with [README.md](../README.md). Treat source and tests as the source of truth when README roadmap text disagrees with implementation.

## Layout
- `src/Transpiler/`: `sf-transpile.exe`, Roslyn frontend -> SharpForge IR -> Lua emitter.
- `src/Builder/`: `sf-build.exe`, Lua dependency bundler plus `.w3x`/`war3map.lua` injector.
- `src/JassGen/`: `sf-jassgen.exe`, parses `common.j`/`blizzard.j` into C# JASS stubs.
- `tests/Transpiler.Tests/`: xUnit coverage for all three tools.
- `samples/CSProject/` and `samples/LuaProject/`: runnable sample inputs and generated outputs.
- `assets/jass/`: checked-in JASS source. `assets/libs/`: bundled C# library stubs copied into user projects.

## Commands
- Build: `dotnet build SharpForge.sln`
- Test: `dotnet test tests\Transpiler.Tests\Transpiler.Tests.csproj`
- Publish: `./publish-all.ps1`

The SDK is pinned by [global.json](../global.json) to .NET 10.0.202 with `rollForward: latestFeature`. Projects target `net10.0`, enable nullable/implicit usings/latest language version, and treat warnings as errors.

## CLI Contracts
- `sf-transpile <input-dir> [-o out.lua] [--init] [--check] [-r SF__] [-d SYM]... [-i CLASS]... [--library-folder libs]... [-v]`; there is no `build` subcommand. Output defaults to `<input>/sf-out.lua`; `--check` does not write a file; `--init` only copies the project template and bundled libs, then exits.
- `sf-build <entry.lua> [-o output-or-target] [--include a.lua;b.lua] [--csharp cs-dir] [-r SF__] [-v]`; there is no `pack` subcommand. No output writes `bundle.lua` next to the entry script. `.w3x` files/folders and existing `war3map.lua` targets are injected; other folders receive `bundle.lua`; other files exit 2.
- `sf-jassgen <input-dir> [-o out-dir] [--host-class NAME] [-v]`; default host class is `JASS`, which must stay aligned with transpiler `--ignore-class` defaults.

Use the existing System.CommandLine beta style in CLI factories. Context-based handlers set `context.ExitCode`; delegate handlers in `sf-build` currently set `Environment.ExitCode`.

## Lua And Builder Semantics
- All transpiled globals live under one root table, default `SF__`, configurable with `--root-table`.
- Namespace/type tables are idempotent (`ROOT.NS = ROOT.NS or {}`); static methods use dot syntax, instance methods use colon syntax, constructors emit `.New(...)` and return `self`.
- Roslyn semantic lowering rewrites implicit `this` field access; binary/unary expressions are deliberately parenthesized because Lua precedence differs from C#.
- String concatenation/interpolation uses the nil-safe `StrConcat__` helper when needed.
- Builder dependency scanning follows literal `require`, `dofile`, `doFile`, `loadfile`, `loadFile`, `package.load`, `include`, `import`, and `load`; plain commented calls are ignored, `-- !require(...)` is forced, and dynamic paths need `--include`.
- Injection wraps generated bundles between `--sf-builder:<length>/<checksum>` markers, emits `function SF__Bundle()`, and splices a single `pcall(SF__Bundle)` into `function main()`.

## Testing Style
- Prefer focused xUnit tests near the affected tool: `EmitSmokeTests`, `BuilderTests`, `JassGenTests`, or `ClassSampleTests`.
- Assert structural behavior with `Assert.Contains` / `Assert.Matches`; avoid byte-for-byte golden assertions for emitted Lua.
- Tests commonly create temporary source trees and call pipelines directly instead of shelling out to published executables.

## Pitfalls
- Do not edit `bin/`, `obj/`, or `publish/` outputs unless explicitly asked.
- Avoid hand-editing generated `*.g.cs` stubs in `assets/libs/Jass-2.0.4/` unless the task is specifically about the checked-in generated baseline; normally regenerate with `sf-jassgen`.
- `Basic.Reference.Assemblies.Net100` is used for Roslyn references; avoid `typeof(object).Assembly.Location` because single-file publish triggers IL3000.
- Keep changes surgical. The repo has intentionally separate tools, so avoid cross-project abstractions unless tests or repeated local patterns justify them.
