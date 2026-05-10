# Exceptions

## try / catch / finally

`try`/`catch`/`finally` lowers to `pcall` scaffolding. At most one `catch` clause is supported.

```csharp
try
{
    DoWork();
}
catch (Exception e)
{
    HandleError(e.Message);
}
finally
{
    Cleanup();
}
```

```lua
local ok__, err__ = pcall(function()
    DoWork()
end)
if not ok__ then
    local e = err__
    HandleError(e)
end
Cleanup()
```

## throw

`throw` inside a `try` block emits `error(...)`. `throw` outside a `try` block is a transpiler error.

```csharp
throw new Exception("bad");
```

```lua
error("bad")
```

## Unsupported

Multiple `catch` clauses, `catch` with type filters, `when` clauses, and re-throw (`throw;`) produce a transpiler error.
