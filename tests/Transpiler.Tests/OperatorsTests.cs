using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class OperatorsTests
{
    [Fact]
    public async Task Operator_overloads_get_signature_lua_names()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public Vector3(float x, float y, float z)
                {
                    this.x = x;
                    this.y = y;
                    this.z = z;
                }

                public static Vector3 operator +(Vector3 left, Vector3 right)
                {
                    return new Vector3(left.x + right.x, left.y + right.y, left.z + right.z);
                }

                public static Vector3 operator *(Vector3 v, float f)
                {
                    return new Vector3(v.x * f, v.y * f, v.z * f);
                }

                public static Vector3 operator *(float f, Vector3 v)
                {
                    return new Vector3(v.x * f, v.y * f, v.z * f);
                }
            }

            public static class Demo
            {
                public static Vector3 Run(Vector3 value)
                {
                    return 2f * value + value * 3f;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "OperatorOverloads.cs");

        Assert.Contains("function SF__.Vector3.op_Multiply__osef(v__x, v__y, v__z, f)", lua);
        Assert.Contains("function SF__.Vector3.op_Multiply__fose(", lua);
        Assert.Contains("SF__.Vector3.op_Multiply__fose(2,", lua);
        Assert.Contains("SF__.Vector3.op_Multiply__osef(value__x", lua);
    }

    [Fact]
    public async Task Basic_binary_operators_lower_to_lua_equivalents()
    {
        var src = """
            public static class MathOps
            {
                public static int Compute(int a, int b)
                {
                    int sum = a + b;
                    int diff = a - b;
                    int prod = a * b;
                    int quot = a / b;
                    bool eq = a == b;
                    bool neq = a != b;
                    bool lt = a < b;
                    bool gt = a > b;
                    bool both = eq && neq;
                    bool either = lt || gt;
                    if (both || either)
                    {
                        return sum + diff + prod + quot;
                    }
                    return 0;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "BasicBinaryOps.cs");

        Assert.Contains("local sum = (a + b)", lua);
        Assert.Contains("local diff = (a - b)", lua);
        Assert.Contains("local prod = (a * b)", lua);
        Assert.Contains("local quot = (a / b)", lua);
        Assert.Contains("local eq = (a == b)", lua);
        Assert.Contains("local neq = (a ~= b)", lua);
        Assert.Contains("local lt = (a < b)", lua);
        Assert.Contains("local gt = (a > b)", lua);
        Assert.Contains("local both = (eq and neq)", lua);
        Assert.Contains("local either = (lt or gt)", lua);
    }
}
