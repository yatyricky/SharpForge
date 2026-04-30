local __sf_modules = {}
local require = function(path)
    local module = __sf_modules[path]
    if module == nil then
        local dotPath = string.gsub(path, "/", ".")
        module = __sf_modules[dotPath]
        __sf_modules[path] = module
    end
    if module ~= nil then
        if not module.inited then
            module.cached = module.loader()
            module.inited = true
        end
        return module.cached
    else
        error("module not found " .. path)
        return nil
    end
end

__sf_modules["Lib.Time"]={loader=function()
print("This is Time module")

function Run()
end
end}

__sf_modules["NotRefed"]={loader=function()
local cls = {}

require("Lib.Time")
function cls.Run()
    print("This is NotRefed module")
end

return cls
end}

__sf_modules["Main"]={loader=function()
local Time = require("Lib.Time")
require("NotRefed")

local game = FrameTimer.new(function(dt)
    local now = MathRound(Time.Time * 100) * 0.01
    for _, system in ipairs(systems) do
        system:Update(dt, now)
    end
end, 1, -1)
game:Start()
end}

