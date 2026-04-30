SF__ = SF__ or {}
function SF__.StrConcat__(...)
    local result = ""
    for i = 1, select("#", ...) do
        local part = select(i, ...)
        if part ~= nil then
            result = result .. tostring(part)
        end
    end
    return result
end

SF__.CorTimerPool__ = SF__.CorTimerPool__ or {}
SF__.CorTimerPoolSize__ = SF__.CorTimerPoolSize__ or 0
SF__.CorMaxTimerPoolSize__ = SF__.CorMaxTimerPoolSize__ or 32

function SF__.CorAcquireTimer__()
    local size = SF__.CorTimerPoolSize__
    if size > 0 then
        local timer = SF__.CorTimerPool__[size]
        SF__.CorTimerPool__[size] = nil
        SF__.CorTimerPoolSize__ = size - 1
        return timer
    end
    return CreateTimer()
end

function SF__.CorReleaseTimer__(timer)
    PauseTimer(timer)
    local size = SF__.CorTimerPoolSize__
    if size < SF__.CorMaxTimerPoolSize__ then
        size = size + 1
        SF__.CorTimerPool__[size] = timer
        SF__.CorTimerPoolSize__ = size
    else
        DestroyTimer(timer)
    end
end

function SF__.CorRun__(fn)
    local thread = coroutine.create(fn)
    local ok, err = coroutine.resume(thread)
    if not ok then error(err) end
    return thread
end

function SF__.CorWait__(milliseconds)
    if milliseconds <= 0 then return end
    local thread = coroutine.running()
    if thread == nil then error("CorWait must be called from a coroutine") end
    if coroutine.isyieldable ~= nil and not coroutine.isyieldable() then error("CorWait cannot yield from this context") end
    local timer = SF__.CorAcquireTimer__()
    TimerStart(timer, milliseconds / 1000, false, function()
        local ok, err = coroutine.resume(thread)
        SF__.CorReleaseTimer__(timer)
        if not ok then error(err) end
    end)
    return coroutine.yield()
end

SF__.DictNil__ = SF__.DictNil__ or {}
function SF__.DictNew__()
    return { data = {}, keys = {} }
end

function SF__.DictGet__(dict, key)
    local value = dict.data[key]
    if value == SF__.DictNil__ then return nil end
    return value
end

function SF__.DictSet__(dict, key, value)
    if dict.data[key] == nil then
        table.insert(dict.keys, key)
    end
    dict.data[key] = value == nil and SF__.DictNil__ or value
end

function SF__.DictRemove__(dict, key)
    if dict.data[key] ~= nil then
        dict.data[key] = nil
        for i, storedKey in ipairs(dict.keys) do
            if storedKey == key then
                table.remove(dict.keys, i)
                break
            end
        end
        return true
    end
    return false
end

function SF__.DictIterate__(dict)
    local i = 0
    return function()
        i = i + 1
        local key = dict.keys[i]
        if key ~= nil then
            local value = dict.data[key]
            if value == SF__.DictNil__ then value = nil end
            return key, value
        end
    end
end

SF__.Game = SF__.Game or {}
-- Game.Hello
SF__.Game.Hello = SF__.Game.Hello or {}
function SF__.Game.Hello.Greet(n)
    if (n > 0) then
        return (n + 1)
    end
    return 0
end

function SF__.Game.Hello.Main()
    local hero = SF__.Game.Hero.New("Arthur", 100)
    BJDebugMsg(hero:ToString())
    hero:LevelUp()
    BJDebugMsg(hero:ToString())
end
-- Game.Unit
SF__.Game.Unit = SF__.Game.Unit or {}
function SF__.Game.Unit.__Init(self, name1, hp2)
    self.__sf_type = SF__.Game.Unit
    self.Name = nil
    self.HP = 0
    self.Name = name1
    self.HP = hp2
end

function SF__.Game.Unit.New(name1, hp2)
    local self = setmetatable({}, { __index = SF__.Game.Unit })
    SF__.Game.Unit.__Init(self, name1, hp2)
    return self
end

function SF__.Game.Unit:Update()
    BJDebugMsg("Tick")
end

function SF__.Game.Unit:TheExcitingPart()
    return SF__.CorRun__(function()
        while true do
            SF__.CorWait__(1000)
            self:Update()
            ::continue::
        end
    end)
end

function SF__.Game.Unit:LevelUp()
    BJDebugMsg("Level Up!")
    local messages = {}
    table.insert(messages, "You have leveled up!")
    do
        local collection = messages
        for i1, message in ipairs(collection) do
            BJDebugMsg(message)
        end
    end
    do
        local i = 0
        while (i < #messages) do
            BJDebugMsg(messages[(i + 1)])
            if (i < 10) then
                i = (i + 1)
            end
            ::continue::
        end
    end
    local dict = SF__.DictNew__()
    SF__.DictSet__(dict, "Level", 2)
    SF__.DictSet__(dict, "HP", 66)
    BJDebugMsg(SF__.StrConcat__("Level: ", SF__.DictGet__(dict, "Level"), ", HP: ", SF__.DictGet__(dict, "HP")))
    do
        local dict2 = dict
        for p__Key, p__Value in SF__.DictIterate__(dict2) do
            local p = {k = p__Key, v = p__Value}
            local pair = p
            BJDebugMsg(SF__.StrConcat__(p.k, ": ", p.v))
            local keyValue = pair.k
        end
    end
end

function SF__.Game.Unit:ToString()
    return SF__.StrConcat__(self.Name, " - HP: ", self.HP)
end
-- Game.Hero
SF__.Game.Hero = SF__.Game.Hero or {}
setmetatable(SF__.Game.Hero, { __index = SF__.Game.Unit })
SF__.Game.Hero.__sf_base = SF__.Game.Unit
function SF__.Game.Hero.__Init(self, name, hp)
    SF__.Game.Unit.__Init(self, name, hp)
    self.__sf_type = SF__.Game.Hero
    self.Name = SF__.StrConcat__("H", name)
    self.HP = (hp * 2)
end

function SF__.Game.Hero.New(name, hp)
    local self = setmetatable({}, { __index = SF__.Game.Hero })
    SF__.Game.Hero.__Init(self, name, hp)
    return self
end

function SF__.Game.Hero:LevelUp()
    SF__.Game.Unit.LevelUp(self)
    self.HP = (self.HP + 10)
end

function SF__.Game.Hero:ToString()
    return SF__.StrConcat__("Hero: ", self.Name, " - HP: ", self.HP)
end
