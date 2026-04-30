local Time = require("Lib.Time")
require("NotRefed")

local game = FrameTimer.new(function(dt)
    local now = MathRound(Time.Time * 100) * 0.01
    for _, system in ipairs(systems) do
        system:Update(dt, now)
    end
end, 1, -1)
game:Start()
