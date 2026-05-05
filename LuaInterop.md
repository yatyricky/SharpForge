# Lua Interop

SharpForge can call existing Lua in two ways:

- raw `LuaInterop` helpers for quick migration
- typed `LuaObject` wrappers for concise, IDE-friendly calls into known Lua modules

Keep interop wrapper files under a configured `--library-folder` such as `libs`. They compile for symbols and IntelliSense, but are skipped during Lua lowering.

## Raw `LuaInterop`

`SFLib.LuaInterop` is a raw escape hatch. Users are responsible for matching the Lua value and function shape they access.

```csharp
using SFLib;

LuaObject frameTimer = LuaInterop.Require("Lib.FrameTimer");
LuaObject itemSystem = LuaInterop.Require("System.ItemSystem");
LuaObject item = LuaInterop.Call<LuaObject>(itemSystem, "new");

int charges = LuaInterop.Get<int>(item, "charges");
LuaInterop.Set(item, "charges", charges + 1);
LuaInterop.SetGlobal("CurrentItemSystem", item);
LuaInterop.Call(frameTimer, "start", item);
LuaInterop.CallMethod(item, "Method", 1, "abc");
LuaInterop.CallGlobal("BJDebugMsg", "ready");
```

becomes

```lua
local frameTimer = require("Lib.FrameTimer")
local itemSystem = require("System.ItemSystem")
local item = itemSystem.new()

local charges = item.charges
item.charges = (charges + 1)
CurrentItemSystem = item
frameTimer.start(item)
item:Method(1, "abc")
BJDebugMsg("ready")
```

Rules:

- `Require("module")` emits `require("module")`.
- `Get(target, "Name")` emits `target.Name`.
- `Set(target, "Name", value)` emits `target.Name = value`.
- `GetGlobal("Name")` emits `Name`.
- `SetGlobal("Name", value)` emits `Name = value`.
- `Call(target, "Name", args...)` emits a dot call: `target.Name(args...)`.
- `CallMethod(target, "Name", args...)` emits a colon call: `target:Name(args...)`.
- `CallGlobal("Name", args...)` emits `Name(args...)`.

String member and global names that are valid Lua identifiers emit as dot or bare syntax. Dynamic names, or names that are not Lua identifiers, emit through bracket access or `_G[...]`.

## Typed `LuaObject` Wrappers

Lua modules can be wrapped with small C# extern types by inheriting from `LuaObject`.

```csharp
using System;
using SFLib;

namespace Lua;

[Lua(Module = "Lib.FrameTimer")]
public class FrameTimer : LuaObject
{
    [Lua(StaticMethod = "new")]
    public FrameTimer(Action<float> callback, int count, int loops) => throw new NotImplementedException();

    public void Start() => throw new NotImplementedException();
}
```

Usage:

```csharp
var timer = new FrameTimer(dt => LuaInterop.CallGlobal("Tick", dt), 1, -1);
timer.Start();
```

Generated Lua:

```lua
local FrameTimer = require("Lib.FrameTimer")
local timer = FrameTimer.new(function(dt)
    Tick(dt)
end, 1, (-1))
timer:Start()
```

## `LuaAttribute`

`[Lua(Module = "...")]` loads a module when the wrapper type is used.

When `Module` is used without `Name`, the module return value is treated as the Lua table:

```csharp
[Lua(Module = "Lib.FrameTimer")]
public class FrameTimer : LuaObject {}
```

```lua
local FrameTimer = require("Lib.FrameTimer")
FrameTimer.new(...)
```

When `Module` is combined with a type-level `Name`, the module is loaded for side effects and the named Lua path is used as the table:

```csharp
[Lua(Module = "Objects.Weapon", Name = "Weapon")]
public class Weapon : LuaObject {}
```

```lua
require("Objects.Weapon")
Weapon.CreateInstance()
```

Nested global paths are allowed:

```csharp
[Lua(Module = "Objects.Weapon", Name = "_G.Objs.Weapon")]
public class Weapon : LuaObject {}
```

```lua
require("Objects.Weapon")
_G.Objs.Weapon.CreateInstance()
```

Member attributes:

- `[Lua(Name = "...")]` renames a member while preserving its default call kind.
- `[Lua(StaticMethod = "...")]` emits a dot call with that Lua name.
- `[Lua(Method = "...")]` emits a colon call with that Lua name.

Class attributes:

- `[Lua(Require = "...")]` emits a top-level `require("...")` before the generated type table or Lua class declaration. The attribute can be repeated to require multiple modules in declaration order.
- `[Lua(Class = "...")]` marks a `LuaObject` subclass as C#-implemented game logic instead of an extern wrapper. The type is emitted under the SharpForge root table and initialized with the Lua `class` helper. If its base type is an extern Lua wrapper with `[Lua(Module = "...")]`, the base module is required and passed to `class`.

```csharp
[Lua(Require = "Lib.class")]
[Lua(Require = "Lib.maths")]
public class EntryClass { }
```

```lua
require("Lib.class")
require("Lib.maths")
SF__.EntryClass = SF__.EntryClass or {}
```

```csharp
[Lua(Module = "System.SystemBase")]
public class SystemBase : LuaObject { }

[Lua(Class = "InitAbilitiesSystem")]
public class InitAbilitiesSystem : SystemBase { }
```

```lua
local SystemBase = require("System.SystemBase")
SF__.Systems.InitAbilitiesSystem = SF__.Systems.InitAbilitiesSystem or class("InitAbilitiesSystem", SystemBase)
```

Default call kinds:

- static wrapper methods lower to dot calls
- instance wrapper methods lower to colon calls
- constructors lower to a static factory, usually `new`
- static wrapper fields/properties lower to table fields and respect `[Lua(Name = "...")]`

Example:

```csharp
[Lua(Module = "Lib.Time")]
public class Time : LuaObject
{
    [Lua(Name = "Time")]
    public static float CurrentTime;
}
```

```csharp
var now = Time.CurrentTime;
```

```lua
local Time = require("Lib.Time")
local now = Time.Time
```