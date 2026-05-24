using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class EnumsTests
{
    [Fact]
    public async Task Enums_emit_numeric_constants_and_work_in_switches()
    {
        var src = """
            namespace Game
            {
                public enum Status
                {
                    Idle,
                    Active = 3,
                    Done,
                    Negative = -1,
                }

                public class UnitState
                {
                    public Status Current;
                    public static Status Default = Status.Active;

                    public static int Score(Status status)
                    {
                        var result = 0;
                        switch (status)
                        {
                            case Status.Idle:
                                result = 1;
                                break;
                            case Status.Active:
                                result = 2;
                                break;
                            default:
                                result = (int)Status.Done;
                                break;
                        }
                        return result;
                    }
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("-- Game.Status", lua);
        Assert.Contains("SF__.Game.Status = SF__.Game.Status or {}", lua);
        Assert.Contains("SF__.Game.Status.Idle = 0", lua);
        Assert.Contains("SF__.Game.Status.Active = 3", lua);
        Assert.Contains("SF__.Game.Status.Done = 4", lua);
        Assert.Contains("SF__.Game.Status.Negative = -1", lua);
        Assert.True(lua.IndexOf("-- Game.Status", StringComparison.Ordinal) < lua.IndexOf("-- Game.UnitState", StringComparison.Ordinal), lua);
        Assert.Contains("self.Current = 0", lua);
        Assert.Contains("SF__.Game.UnitState.Default = SF__.Game.Status.Active", lua);
        Assert.Contains("local switchValue = status", lua);
        Assert.Contains("if (switchValue == SF__.Game.Status.Idle) then", lua);
        Assert.Contains("elseif (switchValue == SF__.Game.Status.Active) then", lua);
        Assert.Contains("result = SF__.Game.Status.Done", lua);
    }

    [Fact]
    public async Task Flags_enums_return_pipeline_error_for_first_pass()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "FlagsEnum.cs"), """
            using System;

            [Flags]
            public enum Bits
            {
                A = 1,
                B = 2,
            }
            """);

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
