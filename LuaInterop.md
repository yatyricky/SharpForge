`SFLib.LuaInterop` is a raw, not type-safe escape hatch for Lua interop. Users are responsible for matching the Lua value/function shape they access.

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

`Call` is a dot call: `target.Function(args...)`.
`CallMethod` is a colon call: `target:Method(args...)`.
