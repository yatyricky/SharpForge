# Control Flow

## if / else

```csharp
if (x > 0)
    DoA();
else
    DoB();
```

```lua
if (x > 0) then
    DoA()
else
    DoB()
end
```

## while

```csharp
while (count > 0)
{
    count -= 1;
}
```

```lua
while (count > 0) do
    count = (count - 1)
end
```

## for

```csharp
for (int i = 0; i < 10; i++)
{
    Process(i);
}
```

```lua
local i = 0
while (i < 10) do
    Process(i)
    i = (i + 1)
end
```

## foreach over array

```csharp
foreach (var item in items)
{
    Use(item);
}
```

```lua
for _, item in ipairs(items) do
    Use(item)
end
```

## foreach over List\<T\>

```csharp
foreach (var item in list)
{
    Use(item);
}
```

```lua
for i = 1, SF__.ListCount__(list) do
    local item = SF__.ListGet__(list, i)
    Use(item)
end
```

## foreach over Dictionary\<K,V\>

```csharp
foreach (var kv in dict)
{
    Use(kv.Key, kv.Value);
}
```

```lua
for _, kv__ in ipairs(SF__.DictKeys__(dict)) do
    local kv__key = kv__
    local kv__val = SF__.DictGet__(dict, kv__)
    Use(kv__key, kv__val)
end
```

## switch

`switch` on integer and string values is supported. Each section must end with `break` or `return`. Fall-through (no `break`/`return`) produces a transpiler error.

```csharp
switch (x)
{
    case 1:
        DoA();
        break;
    case 2:
        DoB();
        break;
    default:
        DoC();
        break;
}
```

```lua
if (x == 1) then
    DoA()
elseif (x == 2) then
    DoB()
else
    DoC()
end
```

## break / continue

`break` and `continue` are supported inside `while`, `for`, and `foreach`. `continue` lowers to a `goto continue__` jump.

## Unsupported

`do...while`, `goto` (other than internal `continue__`), labeled statements, and `switch` with fall-through produce a transpiler error.
