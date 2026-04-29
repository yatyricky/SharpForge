-- 内部结构：
-- data = { key1 = value1, key2 = value2 }  -- hash 表，O(1) get/set
-- keys = { key1, key2 }                    -- 数组，记录插入顺序，稳定遍历

SF__.DictNil__ = SF__.DictNil__ or {}

function SF__.DictNew__()
    return {
        data = {},
        keys = {}
    }
end

function SF__.DictGet__(dict, key)
    local value = dict.data[key]  -- O(1)
    if value == SF__.DictNil__ then return nil end
    return value
end

function SF__.DictSet__(dict, key, value)
    if dict.data[key] == nil then
        -- 新 key：记录插入顺序
        table.insert(dict.keys, key)
    end
    dict.data[key] = value == nil and SF__.DictNil__ or value  -- O(1)
end

function SF__.DictRemove__(dict, key)
    if dict.data[key] ~= nil then
        dict.data[key] = nil
        -- 从 keys 数组中移除（需要线性扫描，但只在 delete 时）
        for i, k in ipairs(dict.keys) do
            if k == key then
                table.remove(dict.keys, i)
                break
            end
        end
    end
end

-- 稳定遍历
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
