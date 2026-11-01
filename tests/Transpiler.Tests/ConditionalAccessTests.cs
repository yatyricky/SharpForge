using System.Text.RegularExpressions;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ConditionalAccessTests
{
    [Fact]
    public async Task Return_position_conditional_invocation_uses_statement_guard()
    {
        var src = """
            public class Buff
            {
            }

            public class BuffHolder
            {
                public Buff? Find(string configId)
                {
                    return null;
                }
            }

            public static class BuffSystem
            {
                private static BuffHolder? FindHolderOrNull(object? u)
                {
                    return null;
                }

                public static Buff? Find(object u, string configId)
                {
                    var holder = FindHolderOrNull(u);
                    return holder?.Find(configId);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalReturn.cs");

        Assert.Contains("local __ca = holder", lua);
        Assert.Contains("if (__ca ~= nil) then", lua);
        Assert.Matches(@"return __ca:Find\(configId\d*\)", lua);
        Assert.Contains("return nil", lua);
        Assert.DoesNotContain("(function()", lua);
    }

    [Fact]
    public async Task Local_initializer_conditional_invocation_uses_iife()
    {
        var src = """
            public class Buff
            {
            }

            public class BuffHolder
            {
                public Buff? Find(string configId)
                {
                    return null;
                }
            }

            public static class Program
            {
                public static Buff? Run(BuffHolder? holder, string configId)
                {
                    var buff = holder?.Find(configId);
                    return buff;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalLocal.cs");

        Assert.Contains("local buff = (function()", lua);
        Assert.Contains("local __ca = holder", lua);
        Assert.Contains("if (__ca ~= nil) then", lua);
        Assert.Matches(@"return __ca:Find\(configId\d*\)", lua);
    }

    [Fact]
    public async Task Conditional_invocation_with_coalesce()
    {
        var src = """
            public class BuffHolder
            {
                public int Clear(string reason)
                {
                    return 0;
                }
            }

            public static class Program
            {
                public static int Run(BuffHolder? holder, string reason)
                {
                    return holder?.Clear(reason) ?? 0;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalCoalesce.cs");

        Assert.Contains("(function()", lua);
        Assert.Contains("if (__ca ~= nil) then", lua);
        Assert.Matches(@"return __ca:Clear\(reason\d*\)", lua);
        Assert.Contains("or 0", lua);
    }

    [Fact]
    public async Task Chained_conditional_member_access()
    {
        var src = """
            public class Buff
            {
                public int Stacks { get; private set; } = 1;
            }

            public class BuffHolder
            {
                public Buff? Find(string configId)
                {
                    return null;
                }
            }

            public static class Program
            {
                public static bool Run(BuffHolder holder)
                {
                    return holder.Find("001")?.Stacks == 2;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalChain.cs");

        Assert.Contains("local __ca", lua);
        Assert.Contains("__ca.Stacks", lua);
        Assert.Contains("== 2", lua);
    }

    [Fact]
    public async Task Statement_conditional_invocation()
    {
        var src = """
            public class BuffHolder
            {
                public void Remove()
                {
                }
            }

            public static class Program
            {
                public static void Run(BuffHolder? sameFrame)
                {
                    sameFrame?.Remove();
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalStatement.cs");

        Assert.Contains("(function()", lua);
        Assert.Contains("local __ca = sameFrame", lua);
        Assert.Contains("return __ca:Remove()", lua);
    }

    [Fact]
    public async Task Conditional_delegate_invocation_in_expression_position()
    {
        var src = """
            using System;

            public static class Program
            {
                public static int? Invoke(Func<int, int>? handler)
                {
                    return handler?.Invoke(1);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalDelegateExpression.cs");

        Assert.Contains("local __ca = handler", lua);
        Assert.Contains("return __ca(1)", lua);
        Assert.DoesNotContain(":Invoke", lua);
    }

    [Fact]
    public async Task Receiver_is_evaluated_once_and_arguments_are_lazy()
    {
        var src = """
            public class Buff
            {
            }

            public class BuffHolder
            {
                public Buff? Find(string configId)
                {
                    return null;
                }
            }

            public static class Program
            {
                private static BuffHolder? GetHolder()
                {
                    return null;
                }

                private static string GetId()
                {
                    return "x";
                }

                public static Buff? Run()
                {
                    return GetHolder()?.Find(GetId());
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalReceiver.cs");

        Assert.Contains("local __ca = SF__.Program.GetHolder()", lua);
        Assert.Contains("return __ca:Find(SF__.Program.GetId())", lua);
        Assert.True(
            Regex.Matches(lua, @"= SF__\.Program\.GetHolder\(\)").Count == 1,
            lua);
    }

    [Fact]
    public async Task Nested_conditional_access()
    {
        var src = """
            public class Child
            {
                public int Value;
            }

            public class Parent
            {
                public Child? GetChild()
                {
                    return null;
                }
            }

            public static class Program
            {
                public static int? Run(Parent? parent)
                {
                    return parent?.GetChild()?.Value;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalNested.cs");

        Assert.Contains("local __ca", lua);
        Assert.Contains("GetChild()", lua);
        Assert.Contains(".Value", lua);
        Assert.True(Regex.Matches(lua, @"local __ca\d* = ").Count >= 2, lua);
    }

    [Fact]
    public async Task Nullable_value_type_receiver_is_diagnosed()
    {
        var src = """
            public static class Program
            {
                public static string? Run(int? value)
                {
                    return value?.ToString();
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["ConditionalNullable.cs"] = src });

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Contains("conditional access on value types is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flattenable_struct_result_is_diagnosed()
    {
        var src = """
            public struct Vec
            {
                public int x;
            }

            public class Holder
            {
                public Vec Position;
            }

            public static class Program
            {
                public static Vec? Run(Holder? holder)
                {
                    return holder?.Position;
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["ConditionalStruct.cs"] = src });

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Contains("conditional access producing a flattenable struct is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flattened_struct_member_is_diagnosed()
    {
        var src = """
            public struct Vec
            {
                public int x;
            }

            public class Holder
            {
                public Vec Position;
            }

            public static class Program
            {
                public static int? Run(Holder? holder)
                {
                    return holder?.Position.x;
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["ConditionalStructMember.cs"] = src });

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Contains("conditional access to flattened struct member", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_auto_property_conditional_access_uses_getter()
    {
        var src = """
            public class Holder
            {
                public int Value => 1;
            }

            public static class Program
            {
                public static int? Run(Holder? holder)
                {
                    return holder?.Value;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalProperty.cs");

        Assert.Contains("local __ca = holder", lua);
        Assert.Contains("return __ca:get_Value()", lua);
    }

    [Fact]
    public async Task String_length_conditional_access_uses_hash_operator()
    {
        var src = """
            public static class Program
            {
                public static int? Run(string? text)
                {
                    return text?.Length;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalStringLength.cs");

        Assert.Contains("local __ca = text", lua);
        Assert.Contains("return #__ca", lua);
    }

    [Fact]
    public async Task Conditional_element_access_is_diagnosed()
    {
        var src = """
            public static class Program
            {
                public static int? Run(int[]? values)
                {
                    return values?[0];
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["ConditionalElement.cs"] = src });

        Assert.Contains(
            diagnostics,
            diagnostic => diagnostic.Contains("conditional element access (?[]) is not supported", StringComparison.Ordinal));
    }
}
