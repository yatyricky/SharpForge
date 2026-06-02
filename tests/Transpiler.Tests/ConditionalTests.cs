using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ConditionalTests
{
    [Fact]
    public async Task Ternary_operator_emits_iife()
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

        // Ternary emits as IIFE for lazy evaluation
        Assert.Contains("(function()", lua);
        Assert.Contains("if", lua);
        Assert.Contains("then return", lua);
        Assert.Contains("else return", lua);
        Assert.Contains("end)()", lua);
        Assert.DoesNotContain("Ternary__", lua);
    }

    [Fact]
    public async Task Ternary_does_not_eagerly_evaluate_branches()
    {
        // Regression: cond ? get_Item(0) : nil must NOT call get_Item(0) when cond is false.
        // Previously emitted as SF__.Ternary__(cond, get_Item(0), nil) which eagerly evaluates both.
        var src = """
            using SFLib.Interop;
            using StdLib;

            public static class Demo
            {
                public static int? SafeGet(List<int> list)
                {
                    return list.Count > 0 ? list[0] : null;
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);
        Assert.Equal(0, exitCode);

        // Must NOT use Ternary__ (eager evaluation)
        Assert.DoesNotContain("Ternary__", lua);
        // Must use IIFE pattern (lazy evaluation)
        Assert.Contains("(function()", lua);
    }
}
