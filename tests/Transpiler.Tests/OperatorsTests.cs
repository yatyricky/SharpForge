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

    [Fact]
    public async Task PostIncrement_expression_uses_value_then_increments()
    {
        var src = """
            public class Counter
            {
                public int n;

                public string Next()
                {
                    return $"Item_{n++}";
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Counter.cs");

        // Post-increment: use value first, then increment
        Assert.Matches(@"local __inc\d* = self\.n", lua);
        Assert.Contains("self.n = (self.n + 1)", lua);
    }

    [Fact]
    public async Task PreIncrement_expression_increments_then_uses_value()
    {
        var src = """
            public class Counter
            {
                public int n;

                public string Next()
                {
                    return $"Item_{++n}";
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Counter.cs");

        // Pre-increment: increment first, then use value (no temp needed)
        Assert.Contains("self.n = (self.n + 1)", lua);
        Assert.DoesNotContain("__inc", lua);
    }

    [Fact]
    public async Task Implicit_conversion_operators_emit_flat_signatures_and_expand_struct_results()
    {
        var src = """
            public struct Vec
            {
                public float x;
                public float y;

                public Vec(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }

                public static implicit operator Vec(float f)
                {
                    return new Vec(f, f);
                }

                public static implicit operator float(Vec v)
                {
                    return v.x;
                }
            }

            public static class Demo
            {
                public static Vec Make()
                {
                    return 5f;
                }

                public static Vec FromCast(float f)
                {
                    return (Vec)f;
                }

                public static float Use()
                {
                    Vec v = 5f;
                    return v;
                }

                public static void Pass(Vec v)
                {
                }

                public static void Run()
                {
                    Pass(9f);
                }

                public static void Pass2(Vec v, int n)
                {
                }

                public static void Run2()
                {
                    Pass2(8f, 1);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ImplicitConversions.cs");

        // Conversion operators overload on the result type, so both carry a signature suffix.
        Assert.Matches(@"function SF__\.Vec\.op_Implicit__f__[0-9a-z]+\(f\)", lua);
        Assert.Contains("return f, f", lua);
        Assert.Matches(@"function SF__\.Vec\.op_Implicit__[0-9a-z]+__f\(v__x\d*, v__y\d*\)", lua);
        Assert.Contains("return v__x", lua);

        // Struct results flow through the existing flattening paths.
        Assert.Matches(@"return SF__\.Vec\.op_Implicit__f__[0-9a-z]+\(5\)", lua);
        Assert.Matches(@"return SF__\.Vec\.op_Implicit__f__[0-9a-z]+\(f\d*\)", lua);
        Assert.Matches(@"local v__x\d*, v__y\d* = SF__\.Vec\.op_Implicit__f__[0-9a-z]+\(5\)", lua);
        Assert.Matches(@"return SF__\.Vec\.op_Implicit__[0-9a-z]+__f\(v__x\d*, v__y\d*\)", lua);
        Assert.Matches(@"SF__\.Demo\.Pass\(SF__\.Vec\.op_Implicit__f__[0-9a-z]+\(9\)\)", lua);

        // Non-tail flattened arguments spill into temporaries before the call.
        Assert.Matches(@"local v__x\d*, v__y\d* = SF__\.Vec\.op_Implicit__f__[0-9a-z]+\(8\)", lua);
        Assert.Matches(@"SF__\.Demo\.Pass2\(v__x\d*, v__y\d*, 1\)", lua);
        Assert.DoesNotContain("struct casts are not supported", lua);
    }

    [Fact]
    public async Task Implicit_struct_to_scalar_conversions_apply_in_expression_contexts()
    {
        var src = """
            public struct Vec
            {
                public float x;
                public float y;

                public Vec(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }

                public static implicit operator float(Vec v)
                {
                    return v.x;
                }

                public float Self()
                {
                    return this;
                }
            }

            public static class Demo
            {
                public static Vec MakeVec()
                {
                    return new Vec(1, 2);
                }

                public static float FromCall()
                {
                    return MakeVec();
                }

                public static float FromCreation()
                {
                    return new Vec(3, 4);
                }

                public static float FromLocal()
                {
                    Vec v = new Vec(1, 2);
                    float f = v;
                    return f;
                }

                public static float FromReturn()
                {
                    Vec v = new Vec(1, 2);
                    return v;
                }

                public static float FromArgument()
                {
                    Vec v = new Vec(1, 2);
                    return Consume(v);
                }

                public static float FromCast()
                {
                    Vec v = new Vec(1, 2);
                    return (float)v;
                }

                public static float FromBinary()
                {
                    Vec v = new Vec(1, 2);
                    return v + 1f;
                }

                public static float Consume(float value)
                {
                    return value;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ImplicitStructToScalar.cs");

        Assert.Contains("function SF__.Vec.op_Implicit(v__x, v__y)", lua);
        Assert.Matches(@"return SF__\.Vec\.op_Implicit\(self__x\d*, self__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Vec\.op_Implicit\(SF__\.Demo\.MakeVec\(\)\)", lua);
        Assert.Matches(@"return SF__\.Vec\.op_Implicit\(3, 4\)", lua);
        Assert.Matches(@"local f\d* = SF__\.Vec\.op_Implicit\(v__x\d*, v__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Vec\.op_Implicit\(v__x\d*, v__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Demo\.Consume\(SF__\.Vec\.op_Implicit\(v__x\d*, v__y\d*\)\)", lua);
        Assert.Matches(@"return \(SF__\.Vec\.op_Implicit\(v__x\d*, v__y\d*\) \+ 1\)", lua);

        // The struct local must stay flattened for the scalar conversion, not become a table.
        Assert.Matches(@"local v__x\d*, v__y\d* = 1, 2", lua);
        Assert.DoesNotContain("struct casts are not supported", lua);
    }

    [Fact]
    public async Task Implicit_conversions_between_classes_and_primitives_emit_calls()
    {
        var src = """
            public class Wrapper
            {
                public string value;

                public Wrapper(string value)
                {
                    this.value = value;
                }

                public static implicit operator Wrapper(string s)
                {
                    return new Wrapper(s);
                }

                public static implicit operator string(Wrapper w)
                {
                    return w.value;
                }
            }

            public static class Demo
            {
                public static string Run()
                {
                    Wrapper w = "hi";
                    return w;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "WrapperConversions.cs");

        Assert.Matches(@"function SF__\.Wrapper\.op_Implicit__s__[0-9a-z]+\(s\)", lua);
        Assert.Matches(@"function SF__\.Wrapper\.op_Implicit__[0-9a-z]+__s\(w\)", lua);
        Assert.Matches(@"local w\d* = SF__\.Wrapper\.op_Implicit__s__[0-9a-z]+\(""hi""\)", lua);
        Assert.Matches(@"return SF__\.Wrapper\.op_Implicit__[0-9a-z]+__s\(w\d*\)", lua);
    }

    [Fact]
    public async Task Implicit_conversions_apply_to_user_defined_operator_operands()
    {
        var src = """
            public struct Vec
            {
                public float x;
                public float y;

                public Vec(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }

                public static implicit operator Wrapper(Vec v)
                {
                    return new Wrapper(v.x);
                }

                public static implicit operator float(Vec v)
                {
                    return v.x;
                }
            }

            public class Wrapper
            {
                public float value;

                public Wrapper(float value)
                {
                    this.value = value;
                }

                public static Wrapper operator +(Wrapper a, Wrapper b)
                {
                    return new Wrapper(a.value + b.value);
                }
            }

            public static class Demo
            {
                public static Wrapper Run(Vec a, Wrapper b)
                {
                    return a + b;
                }

                public static float Scalar(Vec v)
                {
                    return v;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "OperatorOperandConversions.cs");

        // Same parameter type, different results: both suffix with parameter + result signature.
        var wrapperConversion = System.Text.RegularExpressions.Regex.Match(
            lua,
            @"SF__\.Vec\.op_Implicit__[0-9a-z]+__(?<result>[0-9a-z]+)\(a__x, a__y\)");
        Assert.True(wrapperConversion.Success, lua);
        Assert.NotEqual("f", wrapperConversion.Groups["result"].Value);
        Assert.Matches(@"return SF__\.Vec\.op_Implicit__[0-9a-z]+__f\(v__x\d*, v__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Wrapper\.op_Addition\(SF__\.Vec\.op_Implicit__[0-9a-z]+__[0-9a-z]+\(a__x, a__y\), b\d*\)", lua);
    }

    [Fact]
    public async Task Implicit_conversion_to_struct_applies_to_assignments_and_object_initializers()
    {
        var src = """
            public struct Vec
            {
                public float x;
                public float y;

                public Vec(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }

                public static implicit operator Vec(float f)
                {
                    return new Vec(f, f);
                }
            }

            public class Holder
            {
                public Vec pos;

                public Holder()
                {
                }
            }

            public static class Demo
            {
                public static Vec Assign()
                {
                    Vec v = new Vec(1, 2);
                    v = 5f;
                    return v;
                }

                public static Holder Init()
                {
                    return new Holder { pos = 6f };
                }

                public static Vec Conditional(bool c, Vec w)
                {
                    Vec v = new Vec(1, 2);
                    v = c ? 5f : w;
                    return v;
                }

                public static Vec WholeConditional(bool c)
                {
                    Vec v = c ? 7f : 8f;
                    return v;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ImplicitStructAssignment.cs");

        Assert.Matches(@"v__x\d*, v__y\d* = SF__\.Vec\.op_Implicit\(5\)", lua);
        Assert.Matches(@"obj\.pos__x, obj\.pos__y = SF__\.Vec\.op_Implicit\(6\)", lua);
        Assert.Contains("if c", lua);
        Assert.Matches(@"ternary__x\d*, ternary__y\d* = SF__\.Vec\.op_Implicit\(5\)", lua);
        Assert.Matches(@"ternary__x\d*, ternary__y\d* = w__x\d*, w__y\d*", lua);

        // A conditional converted as a whole stays one call instead of branching on fields.
        Assert.Matches(@"v__x\d*, v__y\d* = SF__\.Vec\.op_Implicit\(", lua);
        Assert.Contains("then return 7 else return 8", lua);
        Assert.DoesNotContain("ternary__x", lua.Split("WholeConditional")[1]);
    }

    [Fact]
    public async Task Implicit_conversion_to_struct_in_unsupported_expression_positions_is_diagnosed()
    {
        var arraySrc = """
            public struct Vec
            {
                public float x;

                public Vec(float x)
                {
                    this.x = x;
                }

                public static implicit operator Vec(float f)
                {
                    return new Vec(f);
                }
            }

            public static class Demo
            {
                public static Vec Run()
                {
                    Vec[] items = new Vec[] { 5f };
                    return items[0];
                }
            }
            """;

        var (_, arrayDiagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["ArrayProbe.cs"] = arraySrc });
        Assert.Contains(arrayDiagnostics, diagnostic => diagnostic.Contains("cannot be used in this expression position", StringComparison.Ordinal));

        var fieldSrc = """
            public struct Vec
            {
                public float x;

                public Vec(float x)
                {
                    this.x = x;
                }

                public static implicit operator Vec(float f)
                {
                    return new Vec(f);
                }
            }

            public class Holder
            {
                public Vec pos = 1f;
            }
            """;

        var (_, fieldDiagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["FieldProbe.cs"] = fieldSrc });
        Assert.Contains(fieldDiagnostics, diagnostic => diagnostic.Contains("cannot be used in this expression position", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Implicit_conversion_operator_from_untranspiled_type_is_diagnosed()
    {
        var src = """
            namespace Demo
            {
                public struct Vec
                {
                    public float x;

                    public Vec(float x)
                    {
                        this.x = x;
                    }
                }
            }

            namespace SFLib.Interop
            {
                public class External
                {
                    public static implicit operator External(Demo.Vec v)
                    {
                        return new External();
                    }
                }
            }

            namespace Demo
            {
                public static class Use
                {
                    public static SFLib.Interop.External Run()
                    {
                        Vec v = new Vec(1);
                        SFLib.Interop.External e = v;
                        return e;
                    }
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["ExternalOperator.cs"] = src });

        Assert.Contains(diagnostics, diagnostic => diagnostic.Contains("which SharpForge does not transpile", StringComparison.Ordinal));
    }
}
