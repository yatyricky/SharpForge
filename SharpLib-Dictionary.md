SF__.Dict__ = {
    -- 内部结构：
    -- data = { key1 = value1, key2 = value2 }  -- hash 表，O(1) get/set
    -- keys = { key1, key2 }                    -- 数组，记录插入顺序，稳定遍历
    -- keySet = { [key1] = true, [key2] = true } -- 快速存在性检查
}

function SF__.Dict__.new()
    return {
        data = {},
        keys = {},
        keySet = {}
    }
end

function SF__.Dict__.get(dict, key)
    return dict.data[key]  -- O(1)
end

function SF__.Dict__.set(dict, key, value)
    if not dict.keySet[key] then
        -- 新 key：记录插入顺序
        dict.keySet[key] = true
        table.insert(dict.keys, key)
    end
    dict.data[key] = value  -- O(1)
end

function SF__.Dict__.remove(dict, key)
    if dict.keySet[key] then
        dict.data[key] = nil
        dict.keySet[key] = nil
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
function SF__.Dict__.iterate(dict)
    local i = 0
    return function()
        i = i + 1
        local key = dict.keys[i]
        if key ~= nil then
            return key, dict.data[key]
        end
    end
end