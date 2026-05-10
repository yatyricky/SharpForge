# Diagnostics

## [Debugger] Probes

Decorate a method with `SFLib.Diagnostics.DebuggerAttribute` to insert `BJDebugMsg` calls between each statement.

```csharp
using SFLib.Diagnostics;

public static class Waves
{
    [Debugger]
    public static void Spawn(int wave, string name)
    {
        var count = wave + 1;
        count += 2;
    }
}
```

```lua
function SF__.Waves.Spawn(wave, name)
    local count = (wave + 1)
    BJDebugMsg(SF__.StrConcat__("{Waves.Spawn step 1} {", "wave=", wave, " name=", name, " count=", count, "}"))
    count = (count + 2)
    BJDebugMsg(SF__.StrConcat__("{Waves.Spawn step 2} {", "wave=", wave, " name=", name, " count=", count, "}"))
end
```

Probe labels follow the format `{Class.Method step N}`. Visible values include primitive, enum, `string`, and `bool` parameters and locals known at that point. Object, collection, and struct values are omitted from automatic probes.

For custom formatting of complex values, use `BJDebugMsg(...)` or `LuaInterop.CallGlobal("BJDebugMsg", ...)` directly.

## DesyncLinter

The `DesyncLinter` analyzer emits warnings for operations that are likely to cause desync in Warcraft 3 network games, such as reading random values or system state inside deterministic code paths. These are compile-time warnings visible in the editor; they do not block transpilation.
