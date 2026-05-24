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
