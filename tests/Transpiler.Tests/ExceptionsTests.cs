using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ExceptionsTests
{
    [Fact]
    public async Task Try_catch_finally_and_throw_emit_pcall_shape()
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
        Assert.Contains("error(SF__.System.Exception.New__s(\"boom\"))", lua);
        Assert.Contains("if not __sf_ok then", lua);
        Assert.Contains("local ex = __sf_err", lua);
        Assert.Contains("value = 1", lua);
        Assert.Matches(@"value\s*=\s*\(?\s*value\s*\+\s*2\s*\)?", lua);
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

        Assert.Contains("if not __sf_ok then error(__sf_err) end", lua);
        Assert.Contains("local cleanup = 1", lua);
    }

    [Fact]
    public async Task Multiple_catches_return_pipeline_error_for_mvp()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "MultiCatch.cs"),
            "using System; public static class MultiCatch { public static void F() { try { } catch (InvalidOperationException) { } catch (Exception) { } } }");

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(Path.Combine(dir.FullName, "out.lua")),
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }
}
