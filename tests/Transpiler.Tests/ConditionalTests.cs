using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ConditionalTests
{
    [Fact]
    public async Task Ternary_operator_emits_helper_call()
    {
        var src = """
            public static class Demo
            {
                public static int Max(int a, int b)
                {
                    return a > b ? a : b;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("SF__.Ternary__", lua);
        Assert.Contains("a > b", lua);
    }
}
