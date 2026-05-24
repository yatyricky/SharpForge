using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class DiagnosticsTests
{
    [Fact]
    public async Task Debugger_attribute_inserts_step_probes_between_statements()
    {
        var src = """
            using System;
            using SFLib.Diagnostics;

            namespace SFLib.Diagnostics
            {
                [AttributeUsage(AttributeTargets.Method)]
                public class DebuggerAttribute : Attribute
                {
                }
            }

            public static class Demo
            {
                [Debugger]
                public static void Run(int wave, string name)
                {
                    var count = wave + 1;
                    count += 2;
                    var ignored = new object();
                    count += 3;
                }

                public static void Quiet()
                {
                    var count = 1;
                    count += 1;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("BJDebugMsg(SF__.StrConcat__(\"{Demo.Run step 1} {\", \"wave=\", wave, \" name=\", name, \" count=\", count, \"}\"))", lua);
        Assert.Contains("BJDebugMsg(SF__.StrConcat__(\"{Demo.Run step 2} {\", \"wave=\", wave, \" name=\", name, \" count=\", count, \"}\"))", lua);
        Assert.Contains("BJDebugMsg(SF__.StrConcat__(\"{Demo.Run step 3} {\", \"wave=\", wave, \" name=\", name, \" count=\", count, \"}\"))", lua);
        Assert.DoesNotContain("{Demo.Run step 4}", lua);
        Assert.DoesNotContain("ignored=", lua);
        Assert.DoesNotContain("{Demo.Quiet step", lua);
    }
}
