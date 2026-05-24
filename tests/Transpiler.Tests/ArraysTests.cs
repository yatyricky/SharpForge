using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ArraysTests
{
    [Fact]
    public async Task Array_allocation_without_initializer_emits_empty_table()
    {
        var src = """
            public static class Arrays
            {
                public static int[] NewItems(int count)
                {
                    return new int[count];
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("return {}", lua);
        Assert.DoesNotContain("unsupported expr: ArrayCreationExpression", lua);
    }
}
