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
    public async Task Flags_enums_emit_numeric_constants_and_bitwise_operations()
    {
        var src = """
            using System;

            [Flags]
            public enum Bits
            {
                None = 0,
                A = 1,
                B = 2,
                All = A | B,
            }

            public static class Program
            {
                public static bool HasA(Bits value)
                {
                    return (value & Bits.A) != 0;
                }

                public static Bits Both()
                {
                    return Bits.A | Bits.B;
                }
            }
            """;

        var (lua, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["FlagsEnum.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Contains("SF__.Bits.None = 0", lua);
        Assert.Contains("SF__.Bits.A = 1", lua);
        Assert.Contains("SF__.Bits.B = 2", lua);
        Assert.Contains("SF__.Bits.All = 3", lua);
        Assert.Contains("return ((value & SF__.Bits.A) ~= 0)", lua);
        Assert.Contains("return (SF__.Bits.A | SF__.Bits.B)", lua);
    }
}
