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
        Assert.Contains("::continue::", lua);
        Assert.Matches(@"i\s*=\s*\(?\s*i\s*\+\s*1\s*\)?", lua);
    }
}
