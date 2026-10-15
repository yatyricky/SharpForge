# GUI

`sf-gui.exe` is a thin WPF wrapper around the command-line tools and Warcraft III launch command. It saves user settings as `gui-settings.json` next to the executable.

## Purpose

The GUI is for repeatable local workflows:

1. transpile C# to Lua with `sf-transpile`
2. bundle/inject Lua with `sf-build`
3. optionally launch Warcraft III with the built map
4. generate JASS bindings with `sf-jassgen`

It does not implement separate build logic. The generated command preview is the source of truth for what will run.

## Map Builder Tab

Project fields:

- Warcraft installation path
- Map path, supporting `.w3x` archive maps and folder-format maps

Transpiler fields:

- Enabled toggle
- C# project path
- Output Lua path
- JASS bindings version (shown on the card; empty resolves to the latest bundled version). Pick from the dropdown in the card's Edit dialog, which lists the versions reported by `sf-transpile --list-jass`.
- Trace calls toggle (next to the Init/Check/Transpile buttons): passes `--trace-calls` so StdLib assertion errors embed the live call chain (`SF__TRACE__`) into their messages. Debug builds only — it adds per-call overhead.
- Preprocessor symbols
- Root table
- Ignore class list
- Library folder list
- `Init` button
- `Check` button

Builder fields:

- Enabled toggle
- Main Lua file
- Output path
- Include paths

Run fields:

- editable command preview
- `Run`
- `Replay`

When fields change, the command preview is regenerated. Manual edits are honored until the next regeneration.

## JassGen Tab

The tab is a development tool: production maps do not need it or its settings — the Transpiler card's JASS bindings version selects the bindings that `sf-transpile` deploys. A hint on the page states this.

Fields:

- JASS source folder
- Output folder
- Host class
- `Generate`

The tab runs `sf-jassgen` with the selected values.

## Command Execution

Run executes non-empty preview lines as a `cmd.exe /D /C` chain joined with `&&`. Later tool commands run only if earlier commands succeed.

Warcraft launch commands are treated specially: after preceding tool commands succeed, the GUI starts Warcraft as a detached process. This avoids `cmd.exe start` quoting issues and lets the log capture only SharpForge tool output.

If Builder output is a real `.w3x` archive, the Warcraft launch command uses the copied build map:

```text
demo.sf-build.w3x
```

not the source map archive.

## Validation And Logging

Required fields are validated before actions run. Invalid controls are marked in the UI and an error message is shown.

The log panel is shared across tabs. Run markers from generated commands drive the busy overlays for the Transpiler and Builder cards.