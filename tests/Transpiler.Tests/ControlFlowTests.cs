using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ControlFlowTests
{
    [Fact]
    public async Task For_loop_and_else_if_emit_lua_control_flow()
    {
        var src = """
            public static class Loops
            {
                public static int Sum(int limit)
                {
                    var total = 0;
                    for (int i = 0; i < limit; i++)
                    {
                        if (i == 1)
                        {
                            continue;
                        }
                        else if (i == 3)
                        {
                            break;
                        }
                        total += i;
                    }
                    return total;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("local i = 0", lua);
        Assert.Matches(@"while\s+\(?\s*i\s*<\s*limit\s*\)?\s+do", lua);
        Assert.Contains("elseif (i == 3) then", lua);
        Assert.Contains("repeat", lua);
        Assert.Contains("until true", lua);
        Assert.Matches(@"i\s*=\s*\(?\s*i\s*\+\s*1\s*\)?", lua);
    }

    [Fact]
    public async Task Continue_in_for_loop_uses_repeat_until_true()
    {
        var src = """
            public static class Loop
            {
                public static void Run()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        if (i == 5) continue;
                        DoSomething(i);
                    }
                }

                public static void DoSomething(int x) { }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Loop.cs");

        // No goto
        Assert.DoesNotContain("goto", lua);
        Assert.DoesNotContain("::continue::", lua);

        // Uses repeat...until true + break
        Assert.Contains("repeat", lua);
        Assert.Contains("until true", lua);
        Assert.Contains("break", lua);
    }
}
