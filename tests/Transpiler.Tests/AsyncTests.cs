using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class AsyncTests
{
    [Fact]
    public async Task Task_delay_emits_coroutine_timer_runtime_call()
    {
        var src = """
            using System.Threading.Tasks;

            public static class AsyncDemo
            {
                public static async Task Tick()
                {
                    await Task.Delay(1000);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("SF__.CorTimerPool__ = SF__.CorTimerPool__ or {}", lua);
        Assert.Contains("function SF__.CorAcquireTimer__()", lua);
        Assert.Contains("function SF__.CorReleaseTimer__(timer)", lua);
        Assert.Contains("TimerStart(timer, milliseconds / 1000, false, function()", lua);
        Assert.Contains("DestroyTimer(timer)", lua);
        Assert.Contains("return SF__.CorRun__(function()", lua);
        Assert.Contains("SF__.CorWait__(1000)", lua);
        Assert.DoesNotContain("Task.Delay", lua);
    }

    [Fact]
    public async Task Async_await_and_simple_iterators_emit_sync_mvp_shapes()
    {
        var src = """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            public static class AsyncIterators
            {
                public static async Task<int> Load()
                {
                    return 5;
                }

                public static async Task<int> RunAsync()
                {
                    var value = await Load();
                    return value + 1;
                }

                public static IEnumerable<int> Values(int start)
                {
                    yield return start;
                    yield return start + 1;
                }

                public static int Sum()
                {
                    var total = 0;
                    foreach (var value in Values(2))
                    {
                        total += value;
                    }
                    return total;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("function SF__.AsyncIterators.Load()", lua);
        Assert.Contains("return 5", lua);
        Assert.Contains("local value = SF__.AsyncIterators.Load()", lua);
        Assert.Contains("function SF__.AsyncIterators.Values(start)", lua);
        Assert.Contains("return {start, (start + 1)}", lua);
        Assert.Contains("local collection = SF__.AsyncIterators.Values(2)", lua);
    }
}
