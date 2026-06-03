using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace SharpForge.Transpiler.Tests;

/// <summary>
/// End-to-end tests for string.Split: transpile C# → run Lua → verify output.
/// </summary>
public class StringSplitTests
{
    private static readonly string LuaExe = Path.Combine(
        Directory.GetCurrentDirectory(), "..", "..", "..", "..", "..", "lua53", "lua53.exe");

    [Theory]
    // Basic
    [InlineData("a/b/c", "/", "a;b;c")]
    [InlineData("a,,b", ",", "a;;b")]
    [InlineData("hello", "/", "hello")]
    [InlineData("", "/", "")]
    [InlineData("/a/b", "/", ";a;b")]
    [InlineData("a/b/c/", "/", "a;b;c;")]
    // Lua pattern special chars
    [InlineData("a.b.c", ".", "a;b;c")]
    [InlineData("a+b+c", "+", "a;b;c")]
    [InlineData("a-b-c", "-", "a;b;c")]
    [InlineData("a%c%c", "%", "a;c;c")]
    [InlineData("a(b(c", "(", "a;b;c")]
    [InlineData("a)b)c", ")", "a;b;c")]
    [InlineData("a[b[c", "[", "a;b;c")]
    [InlineData("a]b]c", "]", "a;b;c")]
    [InlineData("a^b^c", "^", "a;b;c")]
    [InlineData("a$b$c", "$", "a;b;c")]
    [InlineData("a*b*c", "*", "a;b;c")]
    [InlineData("a?b?c", "?", "a;b;c")]
    // Whitespace
    [InlineData("a b c", " ", "a;b;c")]
    [InlineData("a\tb\tc", "\t", "a;b;c")]
    [InlineData("  hello  ", " ", ";;hello;;")]
    // Multi-char separator
    [InlineData("a::b::c", "::", "a;b;c")]
    [InlineData("a-->b-->c", "-->", "a;b;c")]
    [InlineData("ababcabc", "abc", "ab;;")]
    // Edge cases
    [InlineData("aaa", "a", ";;;")]
    [InlineData("a", "a", ";")]
    [InlineData("a", "b", "a")]
    public async Task Split_matches_csharp_behavior(string input, string separator, string expectedJoined)
    {
        // 1. Transpile C# source — use string.Split(string) for all separators
        var src = $$"""
            public static class Test
            {
                public static string[] Run()
                {
                    return "{{input}}".Split("{{separator}}");
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        // 2. Build Lua harness: transpiled code + print results
        var harness = lua + "\n" +
            "local result = SF__.Test.Run()\n" +
            "local parts = {}\n" +
            "for i = 1, #result do\n" +
            "    parts[i] = tostring(result[i])\n" +
            "end\n" +
            "print(table.concat(parts, \";\"))\n";

        // 3. Write to temp file
        var dir = Directory.CreateTempSubdirectory("sf-lua-test-");
        var luaFile = Path.Combine(dir.FullName, "test.lua");
        await File.WriteAllTextAsync(luaFile, harness);

        // 4. Run with lua53.exe
        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(LuaExe),
            Arguments = luaFile,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.True(proc.ExitCode == 0, $"lua53.exe exited with {proc.ExitCode}:\n{stderr}");

        // 5. Compare output
        Assert.Equal(expectedJoined, output.Trim());
    }

    [Fact]
    public async Task Split_with_pattern_special_chars_in_separator()
    {
        // Regex special chars like . ( ) + should be escaped
        var src = """
            public static class Test
            {
                public static string[] Run()
                {
                    return "a.b.c".Split('.');
                }
            }
            """;

        var result = await RunLua(src);
        Assert.Equal("a;b;c", result);
    }

    [Fact]
    public async Task Split_preserves_empty_fields()
    {
        // C# Split preserves empty strings: "a,,b" → ["a", "", "b"]
        var src = """
            public static class Test
            {
                public static string[] Run()
                {
                    return "a,,b".Split(',');
                }
            }
            """;

        var result = await RunLua(src);
        Assert.Equal("a;;b", result);
    }

    [Fact]
    public async Task Split_empty_string_returns_empty_array()
    {
        var src = """
            public static class Test
            {
                public static string[] Run()
                {
                    return "".Split('/');
                }
            }
            """;

        var result = await RunLua(src);
        Assert.Equal("", result);
    }

    private static async Task<string> RunLua(string csharpSrc)
    {
        var lua = await TranspilerTestHelper.TranspileAsync(csharpSrc);

        var harness = lua + "\n" +
            "local result = SF__.Test.Run()\n" +
            "local parts = {}\n" +
            "for i = 1, #result do\n" +
            "    parts[i] = tostring(result[i])\n" +
            "end\n" +
            "print(table.concat(parts, \";\"))\n";

        var dir = Directory.CreateTempSubdirectory("sf-lua-test-");
        var luaFile = Path.Combine(dir.FullName, "test.lua");
        await File.WriteAllTextAsync(luaFile, harness);

        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(LuaExe),
            Arguments = luaFile,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"lua53.exe exited with {proc.ExitCode}:\n{stderr}");
        }

        return output.Trim();
    }
}
