using SharpForge.Transpiler.Emitter;
using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class PipelineTests
{
    [Fact]
    public async Task Custom_root_table_is_respected()
    {
        var src = "public static class T { public static int F() { return 1; } }";
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "T.cs");
        await File.WriteAllTextAsync(file, src);

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        var lua = new LuaEmitter("MY_ROOT").Emit(module);

        Assert.Contains("MY_ROOT = MY_ROOT or {}", lua);
        Assert.Contains("MY_ROOT.T", lua);
        Assert.DoesNotContain("SF__", lua);
    }

    [Fact]
    public async Task Static_parameterless_main_is_invoked_at_end_of_lua()
    {
        var src = """
            namespace Game;

            public static class Hello
            {
                public static void Main()
                {
                }
            }
            """;

        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "Hello.cs");
        await File.WriteAllTextAsync(file, src);

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        var lua = new LuaEmitter("MY_ROOT").Emit(module);

        Assert.Contains("function MY_ROOT.Game.Hello.Main()", lua);
        Assert.EndsWith("\nMY_ROOT.Game.Hello.Main()\n", lua);
    }

    [Fact]
    public async Task Static_task_main_is_invoked_at_end_of_lua()
    {
        var src = """
            using System.Threading.Tasks;

            public static class Program
            {
                public static async Task Main()
                {
                    await Task.Delay(1);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Program.cs");

        Assert.Contains("function SF__.Program.Main()", lua);
        Assert.Contains("return SF__.CorRun__(function()", lua);
        Assert.EndsWith("\nSF__.Program.Main()\n", lua);
    }

    [Fact]
    public async Task Multiple_static_main_entry_points_are_diagnostics()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            public static class A
            {
                public static void Main()
                {
                }
            }

            public static class B
            {
                public static void Main()
                {
                }
            }
            """);

        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: output,
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(output.FullName));
    }

    [Fact]
    public async Task Pipeline_ignores_bin_and_obj_sources()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Demo.cs"),
            "public static class Demo { public static int F() { return 1; } }");

        Directory.CreateDirectory(Path.Combine(dir.FullName, "obj"));
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "obj", "Broken.cs"),
            "public static class Broken { public static int F( } }");

        Directory.CreateDirectory(Path.Combine(dir.FullName, "bin"));
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "bin", "AlsoBroken.cs"),
            "public static class AlsoBroken { public static int F( } }");

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(Path.Combine(dir.FullName, "out.lua")),
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Pipeline_copies_bundled_library_stubs_before_compiling()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            public static class Program
            {
                public static void Main()
                {
                    BJDebugMsg("hello");
                }
            }
            """);

        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: output,
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(dir.FullName, "libs", "Jass-2.0.4", "Natives.g.cs")));
        Assert.True(File.Exists(Path.Combine(dir.FullName, "libs", "Jass-2.0.4", "GlobalUsings.g.cs")));
        var luaInterop = await File.ReadAllTextAsync(Path.Combine(dir.FullName, "libs", "SFLib", "Interop", "LuaInterop.cs"));
        Assert.Contains("namespace SFLib.Interop", luaInterop);
        Assert.Contains("public static class LuaInterop", luaInterop);
        Assert.Contains("public class LuaObject", luaInterop);

        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("BJDebugMsg(\"hello\")", lua);
        Assert.DoesNotContain("-- JASS", lua);
    }

    [Fact]
    public async Task Pipeline_user_partial_jass_does_not_hide_generated_jass_bindings()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            public static class Program
            {
                public static int Main()
                {
                    return FourCC("A000") + CustomFunc();
                }
            }
            """);

        var luaWrapperDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "LuaWrapper"));
        await File.WriteAllTextAsync(Path.Combine(luaWrapperDir.FullName, "GlobalFunc.cs"), """
            public static partial class JASS
            {
                public static int CustomFunc() => throw null!;
            }
            """);

        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: output,
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("FourCC(\"A000\")", lua);
        Assert.Contains("CustomFunc()", lua);
    }

    [Fact]
    public async Task Pipeline_creates_intellisense_project_file_when_missing()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            public static class Program
            {
                public static int Main()
                {
                    return 0;
                }
            }
            """);

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(Path.Combine(dir.FullName, "out.lua")),
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var projectFile = Path.Combine(dir.FullName, dir.Name + ".csproj");
        Assert.True(File.Exists(projectFile));
        var project = await File.ReadAllTextAsync(projectFile);
        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk\">", project);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project);
        Assert.Contains("<ImplicitUsings>false</ImplicitUsings>", project);
        Assert.DoesNotContain("<OutputType>Exe</OutputType>", project);
    }

    [Fact]
    public async Task Pipeline_emits_type_methods_before_static_initialization_that_uses_current_type_constructor()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            public class Singleton
            {
                public static Singleton Instance = new();
                public static Singleton Backup;
                public int Value = 3;

                static Singleton()
                {
                    Backup = new();
                }

                public static void Main()
                {
                    BJDebugMsg(I2S(Instance.Value + Backup.Value));
                }
            }
            """);

        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: output,
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        var newIndex = lua.IndexOf("function SF__.Singleton.New()", StringComparison.Ordinal);
        var instanceIndex = lua.IndexOf("SF__.Singleton.Instance = SF__.Singleton.New()", StringComparison.Ordinal);
        var backupIndex = lua.IndexOf("SF__.Singleton.Backup = SF__.Singleton.New()", StringComparison.Ordinal);

        Assert.True(newIndex >= 0, "expected Singleton.New to be emitted");
        Assert.True(instanceIndex > newIndex, "expected static field initialization to run after Singleton.New is defined");
        Assert.True(backupIndex > newIndex, "expected static constructor body to run after Singleton.New is defined");
        Assert.Contains("BJDebugMsg(I2S((SF__.Singleton.Instance.Value + SF__.Singleton.Backup.Value)))", lua);
    }

    [Fact]
    public async Task Types_emit_in_stable_name_order()
    {
        var src = """
            namespace Zed { public static class Last { public static int F() { return 1; } } }
            namespace Alpha { public static class First { public static int F() { return 1; } } }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Ordering.cs");

        Assert.True(lua.IndexOf("-- Alpha.First", StringComparison.Ordinal) < lua.IndexOf("-- Zed.Last", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_comments_emit_as_lua_comments()
    {
        var src = """
            /// <summary>
            /// Demo type docs
            /// </summary>
            public class Commented
            {
                // Hit points field
                public int Hp = 1;

                /* Constructor docs */
                public Commented()
                {
                    // Before assignment
                    Hp = 2; // After assignment
                    /* Block body comment */
                }

                /// <summary>
                /// Run docs
                /// </summary>
                public int Run()
                {
                    return Hp;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Comments.cs");

        Assert.Contains("-- <summary>", lua);
        Assert.Contains("-- Demo type docs", lua);
        Assert.Contains("-- Hit points field", lua);
        Assert.Contains("-- Constructor docs", lua);
        Assert.Contains("-- Before assignment", lua);
        Assert.Contains("-- After assignment", lua);
        Assert.Contains("-- Block body comment", lua);
        Assert.Contains("-- Run docs", lua);
        Assert.True(lua.IndexOf("-- Demo type docs", StringComparison.Ordinal) < lua.IndexOf("-- Commented", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("-- Constructor docs", StringComparison.Ordinal) < lua.IndexOf("function SF__.Commented.__Init", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("-- Run docs", StringComparison.Ordinal) < lua.IndexOf("function SF__.Commented:Run()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsupported_syntax_returns_pipeline_error()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Unsupported.cs"),
            "public static class Unsupported { public static void F() { lock (new object()) { } } }");

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(Path.Combine(dir.FullName, "out.lua")),
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Switch_statements_emit_scoped_lua_branch_chain()
    {
        var src = """
            public static class Switches
            {
                public static int Classify(int value)
                {
                    var result = 0;
                    switch (value)
                    {
                        case 1:
                        case 2:
                            result = 10;
                            break;
                        case 3:
                            return 30;
                        default:
                            result = 99;
                            break;
                    }
                    return result;
                }

                public static int LoopBreak()
                {
                    var total = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        switch (i)
                        {
                            case 1:
                                break;
                            default:
                                total += i;
                                break;
                        }
                        total += 10;
                    }
                    return total;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Switches.cs");

        Assert.Contains("repeat", lua);
        Assert.Contains("local switchValue = value", lua);
        Assert.Contains("if (switchValue == 1) or (switchValue == 2) then", lua);
        Assert.Contains("elseif (switchValue == 3) then", lua);
        Assert.Contains("return 30", lua);
        Assert.Contains("else", lua);
        Assert.Contains("result = 99", lua);
        Assert.Contains("until true", lua);

        var loopIndex = lua.IndexOf("while (i < 3) do", StringComparison.Ordinal);
        Assert.True(loopIndex >= 0, lua);
        var switchIndex = lua.IndexOf("repeat", loopIndex, StringComparison.Ordinal);
        var switchEndIndex = lua.IndexOf("until true", switchIndex, StringComparison.Ordinal);
        var afterSwitchIndex = lua.IndexOf("total = (total + 10)", switchEndIndex, StringComparison.Ordinal);
        Assert.True(switchIndex >= 0 && switchEndIndex > switchIndex && afterSwitchIndex > switchEndIndex, lua);
    }

    [Fact]
    public async Task Switch_goto_case_returns_pipeline_error()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "GotoCase.cs"), """
            public static class GotoCase
            {
                public static int Run(int value)
                {
                    switch (value)
                    {
                        case 1:
                            goto case 2;
                        case 2:
                            return 2;
                        default:
                            return 0;
                    }
                }
            }
            """);

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(Path.Combine(dir.FullName, "out.lua")),
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Generated_identifiers_suffix_user_name_collisions_without_prefixing()
    {
        var src = """
            public static class Names
            {
                public static int Run()
                {
                    var collection = new[] { 1 };
                    var total = 0;
                    foreach (var value in collection)
                    {
                        total += value;
                    }
                    return total;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Names.cs");

        Assert.Contains("local collection = {1}", lua);
        Assert.Contains("local collection1 = collection", lua);
        Assert.Contains("for i, value in ipairs(collection1) do", lua);
    }

    [Fact]
    public async Task FourCc_native_extension_emits_raw_call()
    {
        var src = """
            public static partial class JASS
            {
                public static int FourCC(string val) => throw null!;
            }

            public static class Demo
            {
                public static int Run()
                {
                    return JASS.FourCC("hfoo");
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "NativeExt.cs");

        Assert.Contains("return FourCC(\"hfoo\")", lua);
        Assert.DoesNotContain("JASS", lua);
    }
}
