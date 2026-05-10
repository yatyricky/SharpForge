# SharpForge

> **This project is under active development.**

SharpForge is a C# scripting toolchain for Warcraft III: Reforged map development. It lets map authors write selected systems in strongly typed C#, emit plain Lua, bundle that Lua with existing Lua projects, and inject the result into a `.w3x` map.

## Philosophy

- **Do one thing and do it well.** Each tool has a narrow job: transpile C# to Lua, bundle Lua, inject maps, generate JASS bindings, or orchestrate those tools in the GUI.
- **Integrate with your current toolchain.** World Editor still owns terrain, object data, and placed units. SharpForge fits beside your editor, source tree, Lua modules, and map build process.
- **Do not reinvent the JASS API.** SharpForge does not translate platform calls into a fantasy object model. `KillUnit(unit)` stays `KillUnit(unit)`, not `unit.Kill()`.
- **Migrate incrementally.** Existing Lua modules keep working. New C# code can call into Lua, Lua can call generated output, and projects can move one subsystem at a time.
- **Emit performant Lua.** Generated code is direct Lua with a small helper surface. The bundled C# library is intentionally minimal: usable `List<T>` and `Dictionary<K,V>` shapes, not a full clone of every .NET collection interface.
- **Keep the runtime small.** SharpForge provides only the helpers needed by emitted code and interop stubs. It does not ship a broad standard-library translation layer.

## Discussion

https://www.hiveworkshop.com/threads/wip-introducing-sharpforge-a-c-to-lua-toolchain.371979

## Sample Projects

- https://github.com/yatyricky/lua-maps

## Documentation

- [CLI](docs/cli.md) - man page for `sf-transpile`, `sf-build`, and `sf-jassgen`.
- [GUI](docs/gui.md) - GUI wrapper behavior and fields.
- [Generated Lua](docs/generated-lua.md) - emitted Lua shape, root table contract, runtime bias, and build notes.

### API Reference

Complete documentation for every supported language construct and how it lowers to Lua:

- [Types](docs/api/types.md) — primitives, literals
- [Operators](docs/api/operators.md) — binary, unary, compound assignment
- [Strings](docs/api/strings.md) — interpolation, concatenation, format specifiers
- [Control Flow](docs/api/control-flow.md) — if/else, while, for, foreach, switch, break, continue
- [Arrays](docs/api/arrays.md) — creation, element access, length
- [Classes](docs/api/classes.md) — static/instance, constructors, fields, auto-properties, events
- [Inheritance](docs/api/inheritance.md) — single inheritance, virtual/override, base(), interfaces
- [Structs](docs/api/structs.md) — field flattening, multi-return, SoA List
- [Enums](docs/api/enums.md) — enum lowering, Flags diagnostic
- [Exceptions](docs/api/exceptions.md) — try/catch/finally, throw
- [Delegates](docs/api/delegates.md) — lambdas, Func\<\>, operator overloads
- [Async](docs/api/async.md) — coroutines, Task.Delay, CorRun__/CorWait__
- [Collections](docs/api/collections.md) — List\<T\> and Dictionary\<K,V\>
- [Regex](docs/api/regex.md) — Regex.IsMatch subset
- [Lua Interop](docs/api/lua-interop.md) — LuaInterop, LuaObject, [Lua(...)] attributes
- [Casting](docs/api/casting.md) — explicit cast erasure, is/as
- [Conditional](docs/api/conditional.md) — ternary ?:, Ternary__ helper
- [Diagnostics](docs/api/diagnostics.md) — [Debugger] probes, DesyncLinter

## Repository Layout

```text
SharpForge/
  src/
    Transpiler/   sf-transpile.exe - C# to Lua
    Builder/      sf-build.exe     - Lua bundler and map injector
    JassGen/      sf-jassgen.exe   - JASS to C# binding stubs
    Gui/          sf-gui.exe       - wrapper UI for the tools
  tests/
    Transpiler.Tests/
  assets/
    jass/         checked-in common.j/blizzard.j sources
    libs/         bundled C# stubs copied into user projects
```

## Build

Requires .NET 10 SDK. The SDK version is pinned by [global.json](global.json).

```powershell
dotnet build SharpForge.sln
dotnet test tests\Transpiler.Tests\Transpiler.Tests.csproj
```

Publish framework-dependent single-file executables:

```powershell
.\publish-all.ps1
```

## License

[MIT](LICENSE) © 2026 SharpForge contributors.

## AI Assistance

Significant portions of this codebase were drafted, refactored, and reviewed with AI pair programming in VS Code with GitHub Copilot Chat. All AI-generated changes are reviewed, compiled, and tested by a human before commit. Project conventions, design decisions, and acceptance criteria are owned by the maintainers, not the model.