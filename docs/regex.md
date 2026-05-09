# Regular Expressions

SharpForge supports a small, validated subset of `System.Text.RegularExpressions.Regex` and lowers it to Warcraft III Lua pattern matching.

Lua patterns are not .NET regular expressions. SharpForge only accepts patterns it can compile predictably. Unsupported APIs or pattern features produce transpiler diagnostics instead of best-effort Lua.

## Supported API

Only static `Regex.IsMatch(input, constantPattern)` is supported:

```csharp
using System.Text.RegularExpressions;

public static bool IsUnitCode(string value)
{
    return Regex.IsMatch(value, @"^\d+[A-Z]?\s\w\.$");
}
```

Generated Lua uses `string.find` with a compiled Lua pattern:

```lua
return (string.find(value, "^%d+[A-Z]?%s[%w_]%.$") ~= nil)
```

The pattern must be a compile-time constant string so SharpForge can validate and compile it during lowering.

## Supported Pattern Features

Supported features are intentionally narrow:

| .NET regex feature | Lua pattern output |
| --- | --- |
| literal characters | escaped as Lua pattern literals when needed |
| `.` | `.` |
| `^` at the start | `^` |
| `$` at the end | `$` |
| character classes like `[A-Z]` | `[A-Z]` |
| negated classes like `[^0-9]` | `[^0-9]` |
| `*`, `+`, `?` | `*`, `+`, `?` |
| `\d`, `\D` | `%d`, `%D` |
| `\s`, `\S` | `%s`, `%S` |
| `\w` | `[%w_]` outside classes, `%w_` inside classes |
| `\W` | `[^%w_]` outside classes |
| `\n`, `\r`, `\t` | newline, carriage return, tab literals |
| escaped regex punctuation | Lua-escaped literal punctuation |

Quantifiers must follow a literal, `.`, supported escape, or character class.

`^` is supported only at the beginning of the pattern. `$` is supported only at the end.

## Unsupported Features

SharpForge emits diagnostics for unsupported Regex APIs:

- `new Regex(...)`
- instance `regex.IsMatch(...)`
- `Regex.IsMatch` overloads with `RegexOptions`
- dynamic pattern strings
- `Regex.Replace`
- `Regex.Split`
- `Regex.Match`
- `Regex.Matches`
- other Regex APIs

SharpForge also emits diagnostics for unsupported pattern features:

- alternation with `|`
- grouping and captures with `(...)`
- lookahead and lookbehind
- backreferences such as `\1`
- counted quantifiers such as `{2}` and `{1,3}`
- lazy quantifiers such as `*?`, `+?`, and `??`
- inline options
- named captures
- Unicode categories such as `\p{L}` and `\P{L}`
- nested or subtractive character classes
- `\W` inside a character class
- unsupported escapes

## Practical Guidance

Use Regex lowering for simple validation-style checks: prefixes, suffixes, numeric spans, identifier-like text, and small character-class patterns.

For richer regex behavior, call an existing Lua module through [Lua Interop](lua-interop.md). That keeps complex pattern semantics in a runtime designed for them instead of stretching Lua patterns past what they can represent.
