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

    [Fact]
    public async Task String_replace_and_substring_lower_to_runtime_helpers()
    {
        var src = """
            public static class Log
            {
                public static string Sanitize(string message)
                {
                    var s = message.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
                    if (s.Length > 480)
                    {
                        s = s.Substring(0, 480) + "...";
                    }
                    var tail = s.Substring(3);
                    var magic = "a.b*c".Replace(".", "-").Replace("*", "+");
                    return s + tail + magic;
                }
            }
            """;

        var (lua, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["StringReplace.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Contains("#s > 480", lua);
        Assert.Contains("function SF__.StrReplace__(str, old, new)", lua);
        // gsub pitfalls: magic chars escaped in the search, % escaped in the replacement,
        // and the final gsub parenthesized so only the result (not the count) escapes.
        Assert.Contains("local pattern = string.gsub(old, \"([%^%$%(%)%%%.%[%]%*%+%-%?])\", \"%%%0\")", lua);
        Assert.Contains("local replacement = string.gsub(new, \"%%\", \"%%%%\")", lua);
        Assert.Contains("return (string.gsub(str, pattern, replacement))", lua);
        Assert.Contains("function SF__.StrSubstr__(str, start, length)", lua);
        Assert.Contains("return string.sub(str, start + 1, start + length)", lua);
        Assert.Contains("SF__.StrReplace__(SF__.StrReplace__(SF__.StrReplace__(message, \"\\r\", \" \"), \"\\n\", \" \"), \"\\\"\", \"'\")", lua);
        Assert.Contains("SF__.StrSubstr__(s, 0, 480)", lua);
        Assert.Contains("SF__.StrSubstr__(s, 3, nil)", lua);
        Assert.Contains("SF__.StrReplace__(SF__.StrReplace__(\"a.b*c\", \".\", \"-\"), \"*\", \"+\")", lua);
    }

    [Fact]
    public async Task String_replace_substring_helpers_not_emitted_when_unused()
    {
        var src = """
            public static class Demo
            {
                public static int Run()
                {
                    return 1 + 2;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.DoesNotContain("StrReplace__", lua);
        Assert.DoesNotContain("StrSubstr__", lua);
    }
}
