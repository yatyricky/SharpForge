using Microsoft.CodeAnalysis;
using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class CastingTests
{
    [Fact]
    public async Task Interfaces_and_is_as_emit_type_metadata_checks()
    {
        var src = """
            namespace Game
            {
                public interface INamed
                {
                    string Name();
                }

                public class Unit : INamed
                {
                    public string Name()
                    {
                        return "unit";
                    }
                }

                public class Hero : Unit
                {
                }

                public static class Checks
                {
                    public static bool IsNamed(object value)
                    {
                        return value is INamed;
                    }

                    public static string GetName(object value)
                    {
                        var named = value as INamed;
                        return named.Name();
                    }

                    public static bool HeroIsNamed()
                    {
                        var hero = new Hero();
                        return hero is INamed;
                    }
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Interfaces.cs");

        Assert.Contains("function SF__.TypeIs__(obj, target)", lua);
        Assert.Contains("function SF__.TypeAs__(obj, target)", lua);
        Assert.Contains("-- Game.INamed", lua);
        Assert.Contains("SF__.Game.Unit.__sf_interfaces = {[SF__.Game.INamed] = true}", lua);
        Assert.Contains("self.__sf_type = SF__.Game.Hero", lua);
        Assert.Contains("return SF__.TypeIs__(value, SF__.Game.INamed)", lua);
        Assert.Matches(@"local named\d* = SF__\.TypeAs__\(value\d*, SF__\.Game\.INamed\)", lua);
        Assert.Contains("return SF__.TypeIs__(hero, SF__.Game.INamed)", lua);
    }

    [Fact]
    public async Task GetType_Name_and_FullName_lower_to_runtime_type_metadata_fields()
    {
        var src = """
            namespace MyName;

            public class Queue
            {
            }

            public class Alias
            {
                public static string Name = "RuntimeName";
                public static string FullName = "Runtime.FullName";
            }

            public static class Checks
            {
                public static object GetRuntimeType()
                {
                    var q = new Queue();
                    var t = q.GetType();
                    return t;
                }

                public static object GetInlineRuntimeType()
                {
                    var q = new Queue();
                    return q.GetType();
                }

                public static string GetLocalTypeName()
                {
                    var q = new Queue();
                    var t = q.GetType();
                    return t.Name;
                }

                public static string GetLocalTypeFullName()
                {
                    var q = new Queue();
                    var t = q.GetType();
                    return t.FullName;
                }

                public static string GetInlineTypeName()
                {
                    var q = new Queue();
                    return q.GetType().Name;
                }

                public static string GetInlineTypeFullName()
                {
                    var q = new Queue();
                    return q.GetType().FullName;
                }

                public static string GetOverwrittenName()
                {
                    var alias = new Alias();
                    return alias.GetType().Name;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "RuntimeTypeName.cs");

        Assert.Contains("SF__.MyName.Queue.Name = \"Queue\"", lua);
        Assert.Contains("SF__.MyName.Queue.FullName = \"MyName.Queue\"", lua);
        Assert.Contains("SF__.MyName.Alias.Name = \"Alias\"", lua);
        Assert.Contains("SF__.MyName.Alias.FullName = \"MyName.Alias\"", lua);
        Assert.Contains("SF__.MyName.Alias.Name = \"RuntimeName\"", lua);
        Assert.Contains("SF__.MyName.Alias.FullName = \"Runtime.FullName\"", lua);
        Assert.Contains("local t = q.__sf_type", lua);
        Assert.Matches(@"return t\d*\.Name", lua);
        Assert.Matches(@"return t\d*\.FullName", lua);
        Assert.Matches(@"return q\d*\.__sf_type\.Name", lua);
        Assert.Matches(@"return q\d*\.__sf_type\.FullName", lua);
        Assert.Matches(@"return alias\d*\.__sf_type\.Name", lua);
        Assert.Contains("q.__sf_type", lua);
        Assert.DoesNotContain(":GetType()", lua);
        Assert.DoesNotContain("get_Name", lua);
        Assert.DoesNotContain("get_FullName", lua);
    }

    [Fact]
    public async Task Nullable_suppression_and_supported_is_patterns_emit_lua()
    {
        var src = """
            namespace Game
            {
                public class Unit
                {
                }

                public class Hero : Unit
                {
                }

                public static class Checks
                {
                    public static bool HasValue(Unit? value)
                    {
                        return value! is not null;
                    }

                    public static bool IsHero(object value)
                    {
                        return value is Hero;
                    }
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "PatternChecks.cs");

        Assert.Contains("return (not (value == nil))", lua);
        Assert.Matches(@"return SF__\.TypeIs__\(value\d*, SF__\.Game\.Hero\)", lua);
        Assert.DoesNotContain("unsupported expression: SuppressNullableWarningExpression", lua);
        Assert.DoesNotContain("unsupported expression: IsPatternExpression", lua);
    }

    [Fact]
    public async Task Declaration_is_patterns_remain_diagnostics()
    {
        var src = """
            namespace Game
            {
                public class Hero
                {
                }

                public static class Checks
                {
                    public static bool IsHero(object value)
                    {
                        return value is Hero hero;
                    }
                }
            }
            """;

        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "PatternDiagnostics.cs");
        await File.WriteAllTextAsync(file, src);

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var module = new IRLowering().Lower(compilation, CancellationToken.None);

        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("declaration patterns are not supported", StringComparison.Ordinal));
        Assert.DoesNotContain(module.Diagnostics, diagnostic => diagnostic.Contains("unsupported expression: IsPatternExpression", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Negated_declaration_pattern_guard_reuses_variable_after_if()
    {
        var src = """
            public class Data
            {
                public int Value;
            }

            public class Source
            {
                public object? GetValue()
                {
                    return null;
                }
            }

            public static class Program
            {
                public static int Run(Source source)
                {
                    if (source.GetValue() is not Data data)
                        throw new System.Exception("bad");

                    return data.Value;
                }
            }
            """;

        var (lua, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["NegatedPattern.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Contains("local data = source:GetValue()", lua);
        Assert.Contains("if (not SF__.TypeIs__(data, SF__.Data)) then", lua);
        Assert.Contains("return data.Value", lua);
        Assert.Equal(1, lua.Split("source:GetValue()", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task Negated_declaration_pattern_guard_with_return_keeps_variable_in_scope()
    {
        var src = """
            public class Data
            {
                public int Value;
            }

            public static class Program
            {
                public static int Run(object? value)
                {
                    if (value is not Data data)
                        return -1;

                    return data.Value;
                }
            }
            """;

        var (lua, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["NegatedPatternReturn.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Contains("local data = value", lua);
        Assert.Contains("if (not SF__.TypeIs__(data, SF__.Data)) then", lua);
        Assert.Contains("return (-1)", lua);
        Assert.Contains("return data.Value", lua);
    }

    [Fact]
    public async Task Struct_runtime_casting_and_boxed_equality_are_diagnostics()
    {
        var src = """
            using System;

            public struct AbilityData : IEquatable<AbilityData>
            {
                public float DamageScaling;

                public bool Equals(AbilityData other)
                {
                    return DamageScaling == other.DamageScaling;
                }

                public override bool Equals(object? obj)
                {
                    return obj is AbilityData && Equals((AbilityData)obj);
                }

                public override int GetHashCode()
                {
                    return 0;
                }
            }

            public static class Checks
            {
                public static bool IsAbilityData(object value)
                {
                    return value is AbilityData;
                }

                public static AbilityData CastAbilityData(object value)
                {
                    return (AbilityData)value;
                }

                public static object BoxAbilityData(AbilityData value)
                {
                    return value;
                }
            }
            """;

        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "StructRuntimeFeatures.cs");
        await File.WriteAllTextAsync(file, src);

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var module = new IRLowering().Lower(compilation, CancellationToken.None);

        Assert.DoesNotContain(module.Diagnostics, diagnostic => diagnostic.Contains("implements IEquatable<T>", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("struct Equals(object) is not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("struct GetHashCode() is not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("struct runtime type checks are not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("struct casts are not supported", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("struct boxing conversions are not supported", StringComparison.Ordinal));
    }
}
