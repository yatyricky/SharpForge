using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ExceptionsTests
{
    [Fact]
    public async Task Try_catch_finally_and_throw_emit_typed_error_header()
    {
        var src = """
            using System;

            public static class Exceptions
            {
                public static int Run()
                {
                    var value = 0;
                    try
                    {
                        throw new Exception("boom");
                    }
                    catch (Exception ex)
                    {
                        value = 1;
                    }
                    finally
                    {
                        value += 2;
                    }
                    return value;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("local __sf_ok, __sf_err = pcall(function()", lua);
        Assert.Matches("error\\(SF__\\.StrConcat__\\(\\\"SF__E[0-9a-f]{8}\\\", \\\"boom\\\"\\)\\)", lua);
        Assert.Contains("local __sf_err_msg = tostring(__sf_err)", lua);
        Assert.Contains("if string.sub(__sf_err_msg, 1, 4) == \"SF__\" then", lua);
        Assert.Contains("local ex = __sf_err_msg", lua);
        Assert.Contains("value = 1", lua);
        Assert.Matches(@"value\s*=\s*\(?\s*value\s*\+\s*2\s*\)?", lua);
        Assert.Contains("if not __sf_ok and not __sf_handled then error(__sf_err) end", lua);
    }

    [Fact]
    public async Task Finally_without_catch_rethrows_after_finally()
    {
        var src = """
            public static class Exceptions
            {
                public static void Run()
                {
                    try
                    {
                        throw new System.Exception("boom");
                    }
                    finally
                    {
                        var cleanup = 1;
                    }
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("local cleanup = 1", lua);
        Assert.Contains("if not __sf_ok and not __sf_handled then error(__sf_err) end", lua);
        Assert.True(lua.IndexOf("local cleanup = 1", StringComparison.Ordinal) < lua.IndexOf("if not __sf_ok and not __sf_handled then error(__sf_err) end", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multiple_catches_match_compile_time_exception_headers()
    {
        var src = """
            using System;

            public class SpellException : Exception
            {
                public SpellException(string message) : base(message)
                {
                }
            }

            public class FatalSpellException : SpellException
            {
                public FatalSpellException(string message) : base(message)
                {
                }
            }

            public static class MultiCatch
            {
                public static int Run()
                {
                    var value = 0;
                    try
                    {
                        throw new FatalSpellException("fatal");
                    }
                    catch (InvalidOperationException)
                    {
                        value = 1;
                    }
                    catch (SpellException ex)
                    {
                        value = 2;
                    }
                    catch (Exception)
                    {
                        value = 3;
                    }
                    return value;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Matches("error\\(SF__\\.StrConcat__\\(\\\"SF__E[0-9a-f]{8}\\\", \\\"fatal\\\"\\)\\)", lua);
        Assert.Matches("if string\\.sub\\(__sf_err_msg, 1, 13\\) == \\\"SF__E[0-9a-f]{8}\\\" then", lua);
        Assert.Matches("elseif string\\.sub\\(__sf_err_msg, 1, 13\\) == \\\"SF__E[0-9a-f]{8}\\\" or string\\.sub\\(__sf_err_msg, 1, 13\\) == \\\"SF__E[0-9a-f]{8}\\\" then", lua);
        Assert.Contains("local ex = __sf_err_msg", lua);
        Assert.Contains("value = 2", lua);
        Assert.Contains("elseif string.sub(__sf_err_msg, 1, 4) == \"SF__\" then", lua);
    }
}
