# Async / Coroutines

SharpForge maps `async`/`await` to Lua coroutines. Only `Task` from `SFLib.Async` is supported; `System.Threading.Tasks.Task` is not.

## async Task method

An `async Task` method lowers to a function that returns `SF__.CorRun__(function() ... end)`. The coroutine body wraps the method body.

```csharp
using SFLib.Async;

public static class Waves
{
    public static async Task Spawn()
    {
        await Task.Delay(1000);
        SpawnUnit();
    }
}
```

```lua
function SF__.Waves.Spawn()
    return SF__.CorRun__(function()
        SF__.CorWait__(1000)
        SpawnUnit()
    end)
end
```

## Task.Delay

`await Task.Delay(ms)` lowers to `SF__.CorWait__(ms)`. This suspends the coroutine using a WC3 timer and resumes it after `ms` milliseconds.

The timer pool helpers are emitted automatically when `Task.Delay` is used:

```lua
function SF__.CorRun__(fn)
    local thread = coroutine.create(fn)
    local ok, err = coroutine.resume(thread)
    if not ok then error(err) end
    return thread
end

function SF__.CorWait__(milliseconds)
    if milliseconds <= 0 then return end
    local thread = coroutine.running()
    local timer = SF__.CorAcquireTimer__()
    TimerStart(timer, milliseconds / 1000, false, function()
        local ok, err = coroutine.resume(thread)
        SF__.CorReleaseTimer__(timer)
        if not ok then error(err) end
    end)
    return coroutine.yield()
end

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
```

## yield return (iterator-style)

Simple `yield return` sequences in a method are materialized into a `List<T>` at call time. The method returns a `List<T>` containing all yielded values.

## Unsupported

`Task<T>` (a task with a return value), `async` methods without `await`, `CancellationToken`, and `ValueTask` produce a transpiler error.
