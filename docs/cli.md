# CLI

SharpForge ships three command-line tools. Each executable does one job and returns `0` on success, `1` for compile/lowering failures, and `2` for invalid inputs or usage-level failures where applicable.

## `sf-transpile`

Transpile C# source files into one Lua file.

```powershell
sf-transpile <input-dir> [options]
```

Arguments:

- `<input-dir>`: directory containing C# source files. Files are scanned recursively, excluding build output directories. Optional when `--list-jass` is used.

Options:

- `-o, --output <out.lua>`: output Lua file. Defaults to `<input-dir>/sharpforge.lua`.
- `-c, --check`: parse, type-check, lower, and emit in memory without writing output.
- `--init`: copy editor-support assets only, then exit.
- `-d, --define <SYMBOL>`: preprocessor symbols. Repeatable.
- `-r, --root-table <name>`: Lua root table for generated C# types. Default: `SF__`.
- `-i, --ignore-class <name>`: class names compiled for symbols but skipped during Lua emit. Default: `JASS`. Repeatable.
- `--library-folder <name>`: folder names containing C# library or extern stubs to compile for symbols but skip during Lua lowering. Default: `libs`. Repeatable.
- `--jass-version <VERSION>`: bundled JASS bindings baseline deployed to `<input-dir>/libs/Jass-<VERSION>` (e.g. `3.0.0`). Defaults to the latest bundled version. Other bundled baseline folders left in `libs` from earlier runs are removed so two binding sets never coexist.
- `--list-jass`: print the bundled JASS bindings versions (oldest first) and exit.
- `--trace-calls`: debug-build instrumentation (WC3 embeds Lua 5.3 without `debug`/`<close>`). Every method pushes a frame onto a shadow call stack (`SF__TRACE__`) and runs its body under `xpcall`; the message handler appends the live chain to the error message (`... | at A -> B -> C`), so WC3's single error line carries the stack. Errors are also written to disk as `sf-error-<N>.pld` via the PreloadGen natives (50 per session). Adds per-call overhead; leave off for normal builds.
- `-v, --verbose`: print verbose diagnostics.

Examples:

```powershell
sf-transpile .\CSProject -o .\LuaProject\Main.lua --root-table SF__ --ignore-class JASS --library-folder libs
sf-transpile .\CSProject --check -d MAP_NAME_twistedmeadows
sf-transpile .\CSProject --init
sf-transpile .\CSProject --jass-version 3.0.0
sf-transpile --list-jass
```

Initialization behavior:

- Every run deploys bundled C# stubs under `<input-dir>/libs`: the JASS bindings version selected by `--jass-version` (default: latest bundled) plus the tool-owned `SFLib` and `StdLib` support stubs.
- If `<input-dir>/<FolderName>.csproj` is missing, the bundled IntelliSense project template is copied.
- `--init` performs only this setup and does not transpile.

## `sf-build`

Bundle Lua dependencies and optionally inject the result into a map.

```powershell
sf-build <entry.lua> [options]
```

Arguments:

- `<entry.lua>`: entry Lua script to bundle.

Options:

- `-o, --output <target>`: output folder, `.w3x` map file, `.w3x` folder map, or `war3map.lua` file. Defaults to `bundle.lua` next to the entry script.
- `--include <paths>`: semicolon-separated Lua files to include for dynamic dependencies.
- `-v, --verbose`: print verbose diagnostics.

Examples:

```powershell
sf-build .\LuaProject\Main.lua
sf-build .\LuaProject\Main.lua -o .\dist
sf-build .\LuaProject\Main.lua -o .\demo.w3x
sf-build .\LuaProject\Main.lua --include Lib\Dynamic.lua;Lib\Manual.lua
```

Dependency discovery follows literal calls to:

- `require`
- `dofile`, `doFile`
- `loadfile`, `loadFile`
- `package.load`
- `include`, `import`, `load`

Paths may use `/`, `\`, or `.` separators. Plain commented calls are ignored. A comment prefixed with `!` forces a dependency, for example:

```lua
-- !require("Lib.Dynamic")
```

Output behavior:

- No `--output`: writes `bundle.lua` next to the entry script.
- `--output <folder>`: writes `<folder>/bundle.lua` when the folder is not a `.w3x` folder.
- `--output <map.w3x>`: copies the archive to `<map>.sf-build.w3x`, then injects the copied archive; after a successful injection the final `war3map.lua` text is also mirrored next to the map file for inspection.
- `--output <map.w3x-folder>`: injects `<map.w3x-folder>/war3map.lua`.
- `--output <war3map.lua>`: injects that Lua file directly.
- Other existing file targets are rejected with exit code `2`.

Map injection wraps the bundle in `function SF__Bundle()` and splices a guarded `xpcall(SF__Bundle, SF__BundleError__)` into `function main()`. On failure, the injected error handler prints the traceback via `print` and rethrows it. Previous SharpForge bundle blocks are replaced using `--sf-builder:<length>/<checksum>` markers.

## `sf-jassgen`

Generate C# JASS binding stubs from `common.j` and `blizzard.j` style source files.

```powershell
sf-jassgen <input-dir> [options]
```

Arguments:

- `<input-dir>`: directory containing JASS `*.j` source files. Files are scanned recursively.

Options:

- `-o, --output <out-dir>`: output directory for generated `.cs` files. Defaults to `<input-dir>/generated`.
- `-c, --host-class <name>`: static partial host class name. Default: `JASS`.
- `-v, --verbose`: print verbose diagnostics.

Examples:

```powershell
sf-jassgen .\assets\jass\v2.0.4 -o .\assets\libs\Jass-2.0.4
sf-jassgen .\assets\jass\v3.0.0 -o .\assets\libs\Jass-3.0.0
sf-jassgen .\jass --host-class JASS
```

Development workflow note: `sf-jassgen` is only needed to regenerate binding baselines shipped inside `sf-transpile`; production maps get their bindings from the transpiler's `--jass-version` option.

Generated files:

- `Handles.g.cs`
- `Natives.g.cs`
- `Globals.g.cs`
- `NativeExt.g.cs`
- `GlobalUsings.g.cs`