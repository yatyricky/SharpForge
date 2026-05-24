using Xunit;

namespace SharpForge.Transpiler.Tests;

public class StringsTests
{
    [Fact]
    public async Task String_concat_uses_nil_safe_polyfill()
    {
        var src = """
            public static class Strings
            {
                public static string Interp(string? name, int hp)
                {
                    return $"{name} - HP: {hp}";
                }

                public static string Plus(string? prefix, int value)
                {
                    return prefix + ":" + value;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("function SF__.StrConcat__(...)", lua);
        Assert.Contains("if part ~= nil then", lua);
        Assert.Contains("return SF__.StrConcat__(name, \" - HP: \", hp)", lua);
        Assert.Contains("return SF__.StrConcat__(prefix, \":\", value)", lua);
        Assert.DoesNotContain("return ((name ..", lua);
        Assert.DoesNotContain("return ((prefix ..", lua);
    }

    [Fact]
    public async Task String_add_assignment_uses_nil_safe_polyfill()
    {
        var src = """
            public static class Strings
            {
                public static string Append(string text, string inspectorText)
                {
                    text += "\n" + inspectorText;
                    return text;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("text = SF__.StrConcat__(text, \"\\n\", inspectorText)", lua);
        Assert.DoesNotContain("text = (text +", lua);
    }

    [Fact]
    public async Task Interpolated_string_format_clauses_use_lua_string_format()
    {
        var src = """
            public static class Strings
            {
                public static string Format(float scale, int count)
                {
                    return $"scale:{scale:F0} count:{count:D3} hex:{count:X2}";
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("return SF__.StrConcat__(\"scale:\", string.format(\"%.0f\", scale), \" count:\", string.format(\"%03d\", count), \" hex:\", string.format(\"%02X\", count))", lua);
    }

    [Fact]
    public async Task String_concat_and_interpolation_use_object_tostring_override()
    {
        var src = """
            public class UnitName
            {
                private readonly string value;

                public UnitName(string value)
                {
                    this.value = value;
                }

                public override string ToString()
                {
                    return "unit:" + value;
                }
            }

            public static class Strings
            {
                public static string Plus(UnitName? name)
                {
                    return "[" + name + "]";
                }

                public static string Interp(UnitName? name)
                {
                    return $"<{name}>";
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("local strPart = name", lua);
        Assert.Contains("if (strPart ~= nil) then", lua.Replace("\r\n", "\n"));
        Assert.Contains("return strPart:ToString()", lua);
        Assert.Contains("return SF__.StrConcat__(\"[\", (function()", lua);
        Assert.Contains("return SF__.StrConcat__(\"<\", (function()", lua);
    }

    [Fact]
    public async Task String_concat_and_interpolation_use_struct_tostring_override()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public Vector2(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }

                public override string ToString()
                {
                    return $"({x},{y})";
                }
            }

            public static class Strings
            {
                public static string Plus(Vector2 value)
                {
                    return "[" + value + "]";
                }

                public static string Interp(Vector2 value)
                {
                    return $"<{value}>";
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("function SF__.Vector2.ToString(self__x, self__y)", lua);
        Assert.Contains("return SF__.StrConcat__(\"[\", SF__.Vector2.ToString(value__x, value__y), \"]\")", lua);
        Assert.Contains("return SF__.StrConcat__(\"<\", SF__.Vector2.ToString(", lua);
        Assert.Contains(", \">\")", lua);
        Assert.DoesNotContain("value:ToString()", lua);
    }

    [Fact]
    public async Task String_interpolation_emits_exception_as_is_without_ToString()
    {
        var src = """
            public static class Demo
            {
                public static string Run()
                {
                    try
                    {
                        throw new System.Exception("boom");
                    }
                    catch (System.Exception ex)
                    {
                        return $"Error: {ex}";
                    }
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("return SF__.StrConcat__(\"Error: \", ex)", lua);
        Assert.DoesNotContain("ex:ToString()", lua);
    }
}
