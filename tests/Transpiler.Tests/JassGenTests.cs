using System.CommandLine;
using SharpForge.JassGen.Cli;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public sealed class JassGenTests
{
    [Fact]
    public async Task Cli_writes_native_ext_with_fourcc()
    {
        var inputDir = Directory.CreateTempSubdirectory("sf-jassgen-input-");
        var outputDir = Directory.CreateTempSubdirectory("sf-jassgen-output-");
        outputDir.Delete();

        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "common.j"), "native GetHandleId takes handle h returns integer\n");

        var exitCode = await RootCommandFactory.Create().InvokeAsync([
            inputDir.FullName,
            "-o",
            outputDir.FullName,
        ]);

        Assert.Equal(0, exitCode);

        var nativeExt = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "NativeExt.g.cs"));
        Assert.Contains("public static partial class JASS", nativeExt);
        Assert.Contains("public static int FourCC(string val) => throw null!;", nativeExt);
    }

    [Fact]
    public async Task Cli_cleans_existing_output_directory_before_writing()
    {
        var inputDir = Directory.CreateTempSubdirectory("sf-jassgen-input-");
        var outputDir = Directory.CreateTempSubdirectory("sf-jassgen-output-");
        var staleSubdir = Path.Combine(outputDir.FullName, "stale");
        Directory.CreateDirectory(staleSubdir);

        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "common.j"), "native GetHandleId takes handle h returns integer\n");
        await File.WriteAllTextAsync(Path.Combine(outputDir.FullName, "Old.g.cs"), "stale");
        await File.WriteAllTextAsync(Path.Combine(staleSubdir, "Nested.g.cs"), "stale");

        var exitCode = await RootCommandFactory.Create().InvokeAsync([
            inputDir.FullName,
            "-o",
            outputDir.FullName,
        ]);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "Old.g.cs")));
        Assert.False(Directory.Exists(staleSubdir));
        Assert.True(File.Exists(Path.Combine(outputDir.FullName, "Handles.g.cs")));
        Assert.True(File.Exists(Path.Combine(outputDir.FullName, "NativeExt.g.cs")));
    }

    [Fact]
    public async Task Cli_maps_filter_and_condition_code_parameters_to_boolean_delegates()
    {
        var inputDir = Directory.CreateTempSubdirectory("sf-jassgen-input-");
        var outputDir = Directory.CreateTempSubdirectory("sf-jassgen-output-");
        outputDir.Delete();

        await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "common.j"), """
            type boolexpr extends agent
            type conditionfunc extends boolexpr
            type filterfunc extends boolexpr
            native Condition takes code func returns conditionfunc
            native Filter takes code func returns filterfunc
            native TimerStart takes timer whichTimer, real timeout, boolean periodic, code handlerFunc returns nothing
            """);

        var exitCode = await RootCommandFactory.Create().InvokeAsync([
            inputDir.FullName,
            "-o",
            outputDir.FullName,
        ]);

        Assert.Equal(0, exitCode);

        var natives = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "Natives.g.cs"));
        Assert.Contains("public static conditionfunc Condition(global::System.Func<bool> func) => throw null!;", natives);
        Assert.Contains("public static filterfunc Filter(global::System.Func<bool> func) => throw null!;", natives);
        Assert.Contains("public static void TimerStart(timer whichTimer, float timeout, bool periodic, global::System.Action handlerFunc) => throw null!;", natives);
    }
}