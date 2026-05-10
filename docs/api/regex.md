# Regex

SharpForge supports a subset of `System.Text.RegularExpressions.Regex`.

## Regex.IsMatch

`Regex.IsMatch(input, pattern)` is the only supported method. The pattern must be a compile-time string literal. The transpiler compiles the .NET regex pattern to a Lua pattern string at transpile time.

```csharp
using System.Text.RegularExpressions;

bool match = Regex.IsMatch(s, @"^\d+$");
```

```lua
local match = (string.find(s, "^%d+$") ~= nil)
```

## Supported Pattern Syntax

The following .NET regex constructs are supported:

- `.` (any character)
- `^`, `$` (anchors)
- `*`, `+`, `?` (quantifiers)
- `{n}`, `{n,}`, `{n,m}` (counted quantifiers)
- `[...]`, `[^...]` (character classes)
- `\d`, `\D`, `\w`, `\W`, `\s`, `\S` (shorthand classes)
- `(...)` (grouping — erased in Lua pattern)
- `|` (alternation — not supported in Lua patterns; produces a transpiler error)

## Unsupported

Any pattern construct not listed above, named captures, lookahead/lookbehind, backreferences, and non-`IsMatch` overloads produce a transpiler error.
