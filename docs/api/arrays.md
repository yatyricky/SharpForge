# Arrays

## Creation

Fixed-size array creation with `new T[n]` lowers to an empty Lua table `{}`; the size hint is dropped.

```csharp
int[] arr = new int[3];
```

```lua
local arr = {}
```

Array literals lower directly:

```csharp
string[] names = new[] { "a", "b", "c" };
```

```lua
local names = {"a", "b", "c"}
```

## Element Access

C# arrays are 0-based. Lua tables are 1-based. The transpiler adjusts all index expressions by adding 1.

```csharp
arr[0] = 42;
int v = arr[1];
```

```lua
arr[1] = 42
local v = arr[2]
```

## Length

```csharp
int len = arr.Length;
```

```lua
local len = #arr
```

## foreach

```csharp
foreach (var item in arr)
{
    Use(item);
}
```

```lua
for _, item in ipairs(arr) do
    Use(item)
end
```

## Unsupported

Multi-dimensional arrays, jagged arrays, and `Array` static methods produce a transpiler error.
