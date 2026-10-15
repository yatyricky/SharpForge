# SharpForge Agent Instructions

SharpForge is an infrastructure project (C# -> Lua toolchain for Warcraft III maps). Stability of updates and regressions comes first. These rules apply to every coding agent working in this repository; they were consolidated from `.github/` instruction files on 2026-10-14.

## SharpForge Agent Notes

For product context and user-facing behavior, start with [README.md](README.md). Treat source and tests as the source of truth when README roadmap text disagrees with implementation.

### Layout

- `src/Transpiler/`: `sf-transpile.exe`, Roslyn frontend -> SharpForge IR -> Lua emitter.
- `src/Builder/`: `sf-build.exe`, Lua dependency bundler plus `.w3x`/`war3map.lua` injector.
- `src/JassGen/`: `sf-jassgen.exe`, parses `common.j`/`blizzard.j` into C# JASS stubs.
- `tests/Transpiler.Tests/`: xUnit coverage for all three tools.
- `assets/jass/`: checked-in JASS source. `assets/libs/`: bundled C# library stubs copied into user projects.

### Commands

- Build: `dotnet build SharpForge.sln`
- Test: `dotnet test tests\Transpiler.Tests\Transpiler.Tests.csproj`
- Publish: `./publish-all.ps1`

The SDK is pinned by [global.json](global.json) to .NET 10.0.202 with `rollForward: latestFeature`. Projects target `net10.0`, enable nullable/implicit usings/latest language version, and treat warnings as errors.

### CLI Contracts

- `sf-transpile <input-dir> [-o out.lua] [--init] [--check] [-r SF__] [-d SYM]... [-i CLASS]... [--library-folder libs]... [--trace-calls] [-v]`; there is no `build` subcommand. Output defaults to `<input>/sf-out.lua`; `--check` does not write a file; `--init` only copies the project template and bundled libs, then exits; `--trace-calls` instruments a shadow call stack for debug builds.
- `sf-build <entry.lua> [-o output-or-target] [--include a.lua;b.lua] [-v]`; there is no `pack` subcommand. No output writes `bundle.lua` next to the entry script. `.w3x` files/folders and existing `war3map.lua` targets are injected; archive (`.w3x` file) targets also mirror the injected `war3map.lua` next to the map; other folders receive `bundle.lua`; other files exit 2.
- `sf-jassgen <input-dir> [-o out-dir] [--host-class NAME] [-v]`; default host class is `JASS`, which must stay aligned with transpiler `--ignore-class` defaults.

### Design Philosophy

- Do one thing and do it well. Keep tool responsibilities separate: Builder owns Lua dependency bundling and `.w3x`/`war3map.lua` injection only; it must not invoke or reference Transpiler. C# -> Lua belongs to `sf-transpile` and GUI orchestration.

Use the existing System.CommandLine beta style in CLI factories. Context-based handlers set `context.ExitCode`; delegate handlers in `sf-build` currently set `Environment.ExitCode`.

### Lua And Builder Semantics

- All transpiled globals live under one root table, default `SF__`, configurable with `--root-table`.
- Namespace/type tables are idempotent (`ROOT.NS = ROOT.NS or {}`); static methods use dot syntax, instance methods use colon syntax, constructors emit `.New(...)` and return `self`.
- Roslyn semantic lowering rewrites implicit `this` field access; binary/unary expressions are deliberately parenthesized because Lua precedence differs from C#.
- String concatenation/interpolation uses the nil-safe `StrConcat__` helper when needed.
- Builder dependency scanning follows literal `require`, `dofile`, `doFile`, `loadfile`, `loadFile`, `package.load`, `include`, `import`, and `load`; plain commented calls are ignored, `-- !require(...)` is forced, and dynamic paths need `--include`.
- Injection wraps generated bundles between `--sf-builder:<length>/<checksum>` markers, emits `function SF__Bundle()` plus `function SF__BundleError__(m)`, and splices a single `xpcall(SF__Bundle, SF__BundleError__)` into `function main()`.

### Testing Style

- Prefer focused xUnit tests per feature: `StringsTests`, `StructsTests`, `ClassesTests`, etc. Each test file covers one lowering feature.
- Use `Assert.Equal` with full expected Lua output as the default assertion style. Use `Assert.Contains`/`Assert.Matches` only when output includes generated temporaries with numeric suffixes.
- Tests commonly create temporary source trees and call pipelines directly instead of shelling out to published executables.

### Pitfalls

- Do not edit `bin/`, `obj/`, or `publish/` outputs unless explicitly asked.
- Avoid hand-editing generated `*.g.cs` stubs in `assets/libs/Jass-*/` unless the task is specifically about the checked-in generated baseline; normally regenerate with `sf-jassgen`. `sf-transpile` embeds every `assets/libs/Jass-*` baseline and deploys only the one picked by `--jass-version` (default: latest), removing other bundled baselines from the target `libs`.
- `Basic.Reference.Assemblies.Net100` is used for Roslyn references; avoid `typeof(object).Assembly.Location` because single-file publish triggers IL3000.
- Keep changes surgical. The repo has intentionally separate tools, so avoid cross-project abstractions unless tests or repeated local patterns justify them.

## General Coding Behaviour

- Keep changes scoped to the requested behavior and existing tool boundaries.
- Do not hard-code to satisfy a sample prompt; clarify missing requirements instead.
- Update docs for user-visible behavior changes. Use `docs/cli.md` for CLI contracts, `docs/gui.md` for GUI behavior, and generated-Lua/lowering docs for transpiler output or runtime helpers.
- Keep tests close to the changed behavior. For transpiler features, tests in `tests/Transpiler.Tests/` are the executable language specification.
- Before finishing feature work, check likely interactions with existing representations, type behavior, null/default handling, overloads, and Lua/JASS interop. Unsupported intersections should produce diagnostics rather than best-effort output.
- Put durable implementation lessons in the most specific doc or instruction file, and keep instruction updates short, general, and non-duplicative.

## Karpathy-inspired coding guidelines

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

### 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

### 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

### 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak ones ("make it work") require constant clarification.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

## Test Quality

### Verify actual output, not assumptions

When writing a test that asserts generated Lua output, you MUST run the transpiler on the test source and inspect the actual output before writing assertions. Do not assume what the output looks like based on reading the transpiler code — run it.

```bash
# Write source to a temp file, run transpiler, inspect output
mkdir -p /tmp/sf-test-xxx
cat > /tmp/sf-test-xxx/Test.cs << 'EOF'
<source code>
EOF
dotnet run --project src/Transpiler/Transpiler.csproj --no-restore -- /tmp/sf-test-xxx -o /tmp/sf-test-xxx/out.lua --ignore-namespace SFLib.Interop
cat /tmp/sf-test-xxx/out.lua
```

### Test both positive and negative cases

When testing a feature that conditionally generates code (e.g., struct equality only when used), test BOTH:
- The case where the code IS generated (e.g., struct uses `LuaInterop.Eq`)
- The case where the code is NOT generated (e.g., struct without equality usage)

### Use the correct assertion

- `Assert.Equal(expected, lua)` — for full output comparison when the output is small and stable
- `Assert.Contains(substring, lua)` — for checking specific functions or patterns exist
- `Assert.DoesNotContain(substring, lua)` — for verifying code is NOT emitted
- `Assert.Matches(regex, lua)` — for patterns with generated names (numeric suffixes)

### Test on real code when possible

For features that affect the user's project (like struct equality emission), verify against the actual project output at `C:\Users\yatyr\workspace\lua-maps\LuaProject\Main.lua` in addition to isolated tests.

### Every bug fix must have a regression test

When fixing a bug, you MUST add a test that reproduces the bug and verifies the fix. The test should:
1. Fail before the fix (reproduces the bug)
2. Pass after the fix (verifies correctness)
3. Remain in the test suite permanently (prevents regression)

Do not ship a bug fix without a corresponding test. This is non-negotiable.

## Lowering Feature Design

Applies to `src/Transpiler/**` and `tests/Transpiler.Tests/**`.

Keep this section short and general. Detailed feature notes belong in docs and tests, especially [docs/generated-lua.md](docs/generated-lua.md).

- Prefer direct, predictable Lua with a small helper surface.
- Canonicalize C# conveniences into existing semantic paths before adding Lua Emission Layer special cases.
- Choose the simplest design that preserves semantics; weigh performance, implementation fit, correctness risk, output size, and readability.
- Add focused tests for observable lowering behavior and boundary cases.
- Emit diagnostics for unsupported constructs instead of guessing.
