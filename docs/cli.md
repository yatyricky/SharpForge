# CLI

SharpForge ships three command-line tools. Each executable does one job and returns `0` on success, `1` for compile/lowering failures, and `2` for invalid inputs or usage-level failures where applicable.

## `sf-transpile`

Transpile C# source files into one Lua file.

```powershell
sf-transpile <input-dir> [options]
```

Arguments:

- `<input-dir>`: directory containing C# source files. Files are scanned recursively, excluding build output directories.

Options:

- `-o, --output <out.lua>`: output Lua file. Defaults to `<input-dir>/sharpforge.lua`.
- `-c, --check`: parse, type-check, lower, and emit in memory without writing output.
- `--init`: copy editor-support assets only, then exit.
- `-d, --define <SYMBOL>`: preprocessor symbols. Repeatable.
- `-r, --root-table <name>`: Lua root table for generated C# types. Default: `SF__`.
- `-i, --ignore-class <name>`: class names compiled for symbols but skipped during Lua emit. Default: `JASS`. Repeatable.
- `--library-folder <name>`: folder names containing C# library or extern stubs to compile for symbols but skip during Lua lowering. Default: `libs`. Repeatable.
- `-v, --verbose`: print verbose diagnostics.

Examples:

```powershell
sf-transpile .\CSProject -o .\LuaProject\Main.lua --root-table SF__ --ignore-class JASS --library-folder libs
sf-transpile .\CSProject --check -d MAP_NAME_twistedmeadows
sf-transpile .\CSProject --init
```

Initialization behavior:

- Every run copies bundled C# stubs under `<input-dir>/libs`.
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
- `--output <map.w3x>`: copies the archive to `<map>.sf-build.w3x`, then injects the copied archive.
- `--output <map.w3x-folder>`: injects `<map.w3x-folder>/war3map.lua`.
- `--output <war3map.lua>`: injects that Lua file directly.
- Other existing file targets are rejected with exit code `2`.

Map injection wraps the bundle in `function SF__Bundle()` and splices a guarded `pcall(SF__Bundle)` into `function main()`. Previous SharpForge bundle blocks are replaced using `--sf-builder:<length>/<checksum>` markers.

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
sf-jassgen .\jass --host-class JASS
```

Generated files:

- `Handles.g.cs`
- `Natives.g.cs`
- `Globals.g.cs`
- `NativeExt.g.cs`
- `GlobalUsings.g.cs`