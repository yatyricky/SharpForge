``` csharp
LuaObject FrameTimer = LuaInterop.Require("Lib.FrameTimer");

LuaObject systems = new List<LuaObject>();
systems.Add(LuaInterop.Call<LuaObject>(LuaInterop.Require("System.ItemSystem"), "new"));

```

becomes

``` lua
local FrameTimer = require("Lib.FrameTimer")

local systems = {} -- stub, will use our SFLib.List transpiled lua list
table.insert(systems, require("System.ItemSystem").new())
```