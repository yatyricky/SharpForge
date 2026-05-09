# SharpForge

> **This project is under active development.**

SharpForge is a C# scripting toolchain for Warcraft III: Reforged map development. It lets map authors write selected systems in strongly typed C#, emit plain Lua, bundle that Lua with existing Lua projects, and inject the result into a `.w3x` map.

## Philosophy

- **Do one thing and do it well.** Each tool has a narrow job: transpile C# to Lua, bundle Lua, inject maps, generate JASS bindings, or orchestrate those tools in the GUI.
- **Integrate with your current toolchain.** World Editor still owns terrain, object data, and placed units. SharpForge fits beside your editor, source tree, Lua modules, and map build process.
- **Do not reinvent the JASS API.** SharpForge does not translate platform calls into a fantasy object model. `KillUnit(unit)` stays `KillUnit(unit)`, not `unit.Kill()`.
- **Migrate incrementally.** Existing Lua modules keep working. New C# code can call into Lua, Lua can call generated output, and projects can move one subsystem at a time.
- **Emit performant Lua.** Generated code is direct Lua with a small helper surface. The bundled C# library is intentionally minimal, such as a usable `List<T>` shape, not a full clone of every .NET collection interface.
- **Keep the runtime small.** SharpForge provides only the helpers needed by emitted code and interop stubs. It does not ship a broad standard-library translation layer.

## Discussion

https://www.hiveworkshop.com/threads/wip-introducing-sharpforge-a-c-to-lua-toolchain.371979

## Sample Projects

- https://github.com/yatyricky/lua-maps

## Documentation

- [CLI](docs/cli.md) - man page for `sf-transpile`, `sf-build`, and `sf-jassgen`.
- [GUI](docs/gui.md) - GUI wrapper behavior and fields.
- [Generated Lua](docs/generated-lua.md) - emitted Lua shape, debugger probes, root table contract, lowering coverage, and build notes.
- [Lua Interop](docs/lua-interop.md) - raw `LuaInterop` calls and typed `LuaObject` wrappers for existing Lua modules.
- [Collections](docs/collections.md) - minimal collection runtime notes.
- [Struct Lowering](docs/struct.md) - current struct flattening behavior and open method-lowering work.
- [Conditional Expression](docs/conditional-expression.md) - ternary `?:` lowering strategy.
- [Type Casting](docs/type-casting.md) - explicit casting, `is`/`as`, and struct equality constraints.
- [Regular Expressions](docs/regex.md) - supported `Regex.IsMatch` subset and Lua pattern limits.

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
  samples/
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