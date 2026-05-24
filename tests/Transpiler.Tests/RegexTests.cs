using Microsoft.CodeAnalysis;
using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class RegexTests
{
    [Fact]
    public async Task Regex_IsMatch_constant_subset_lowers_to_lua_pattern()
    {
        var src = """
            using System.Text.RegularExpressions;

            public static class RegexChecks
            {
                public static bool Valid(string value)
                {
                    return Regex.IsMatch(value, @"^\d+[A-Z]?\s\w\.$");
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("return (string.find(value, \"^%d+[A-Z]?%s[%w_]%.$\") ~= nil)", lua);
        Assert.DoesNotContain("Regex.IsMatch", lua);
        Assert.DoesNotContain("System.Text.RegularExpressions", lua);
    }

    [Fact]
    public async Task Regex_unsupported_patterns_and_apis_are_diagnostics()
    {
        var src = """
            using System.Text.RegularExpressions;

            public static class RegexChecks
            {
                public static bool Alternation(string value) => Regex.IsMatch(value, "a|b");
                public static bool Counted(string value) => Regex.IsMatch(value, "a{2}");
                public static bool Lookahead(string value) => Regex.IsMatch(value, "(?=a)");
                public static bool Backref(string value) => Regex.IsMatch(value, @"\1");
                public static bool Dynamic(string value, string pattern) => Regex.IsMatch(value, pattern);
                public static bool Options(string value) => Regex.IsMatch(value, "a", RegexOptions.IgnoreCase);
                public static string Replace(string value) => Regex.Replace(value, "a", "b");
                public static bool Instance(string value)
                {
                    var regex = new Regex("a");
                    return regex.IsMatch(value);
                }
            }
            """;

        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "RegexDiagnostics.cs");
        await File.WriteAllTextAsync(file, src);

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, "C# compile errors:\n" + string.Join("\n", errors.Select(error => error.ToString())));

        var module = new IRLowering().Lower(compilation, CancellationToken.None);

        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("regex alternation is not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("regex counted quantifiers are not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("regex grouping and lookaround are not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("regex backreferences are not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("Regex patterns must be compile-time constant strings", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("unsupported Regex.IsMatch overload", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("unsupported Regex API 'Replace'", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("Regex constructors are not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("unsupported Regex API 'IsMatch'", StringComparison.Ordinal));
    }
}
