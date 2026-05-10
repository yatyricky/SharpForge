using SharpForge.Transpiler.Emitter;
using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class EmitSmokeTests
{
    [Fact]
    public async Task Static_method_with_simple_return_emits_lua()
    {
        var src = """
            public static class Demo
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """;

        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "Demo.cs");
        await File.WriteAllTextAsync(file, src);

        var frontend = new RoslynFrontend(Array.Empty<string>());
        var compilation = await frontend.CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);

        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        var lua = new LuaEmitter(TranspileOptions.DefaultRootTable).Emit(module);

        Assert.Contains("SF__ = SF__ or {}", lua);
        Assert.Contains("SF__.Demo = SF__.Demo or {}", lua);
        Assert.Contains("function SF__.Demo.Add(a, b)", lua);
        Assert.Matches(@"return\s*\(?\s*a\s*\+\s*b\s*\)?", lua);
        Assert.DoesNotContain("function SF__.__is", lua);
        Assert.DoesNotContain("function SF__.__as", lua);
    }

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
    public async Task Static_main_with_args_is_invoked_at_end_of_lua()
    {
        var src = """
            public class Program
            {
                public static void Main(string[] args)
                {
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Program.cs");

        Assert.Contains("function SF__.Program.Main(args)", lua);
        Assert.EndsWith("\nSF__.Program.Main()\n", lua);
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

        var lua = await TranspileSourceAsync(src, "Program.cs");

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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(output.FullName));
    }

    [Fact]
    public async Task Instance_field_initializers_emit_in_constructor()
    {
        var src = """
            public class UnitState
            {
                public int Hp = 100;
                public bool Alive;
                public double Scale;
                public string? Name;
            }
            """;

        var lua = await TranspileSourceAsync(src, "UnitState.cs");

        Assert.Contains("function SF__.UnitState.New()", lua);
        Assert.Contains("self.Hp = 100", lua);
        Assert.Contains("self.Alive = false", lua);
        Assert.Contains("self.Scale = 0", lua);
        Assert.Contains("self.Name = nil", lua);
        Assert.Contains("return self", lua);
    }

    [Fact]
    public async Task Static_field_defaults_and_initializers_emit_on_type_table()
    {
        var src = """
            public static class GameState
            {
                public static int Count;
                public static bool Ready = true;
            }
            """;

        var lua = await TranspileSourceAsync(src, "GameState.cs");

        Assert.Contains("SF__.GameState.Count = 0", lua);
        Assert.Contains("SF__.GameState.Ready = true", lua);
    }

    [Fact]
    public async Task String_concat_uses_nil_safe_polyfill()
    {
        var src = """
            public static class Strings
            {
                public static string Interp(string? name, int hp)
                {
                    return $"{name} - HP: {hp}";
                }

                public static string Plus(string? prefix, int value)
                {
                    return prefix + ":" + value;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Strings.cs");

        Assert.Contains("function SF__.StrConcat__(...)", lua);
        Assert.Contains("if part ~= nil then", lua);
        Assert.Contains("return SF__.StrConcat__(name, \" - HP: \", hp)", lua);
        Assert.Contains("return SF__.StrConcat__(prefix, \":\", value)", lua);
        Assert.DoesNotContain("return ((name ..", lua);
        Assert.DoesNotContain("return ((prefix ..", lua);
    }

    [Fact]
    public async Task Interpolated_string_format_clauses_use_lua_string_format()
    {
        var src = """
            public static class Strings
            {
                public static string Format(float scale, int count)
                {
                    return $"scale:{scale:F0} count:{count:D3} hex:{count:X2}";
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "FormattedInterpolation.cs");

        Assert.Contains("return SF__.StrConcat__(\"scale:\", string.format(\"%.0f\", scale), \" count:\", string.format(\"%03d\", count), \" hex:\", string.format(\"%02X\", count))", lua);
    }

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

        var lua = await TranspileSourceAsync(src, "DebuggerProbe.cs");

        Assert.Contains("BJDebugMsg(SF__.StrConcat__(\"{Demo.Run step 1} {\", \"wave=\", wave, \" name=\", name, \" count=\", count, \"}\"))", lua);
        Assert.Contains("BJDebugMsg(SF__.StrConcat__(\"{Demo.Run step 2} {\", \"wave=\", wave, \" name=\", name, \" count=\", count, \"}\"))", lua);
        Assert.Contains("BJDebugMsg(SF__.StrConcat__(\"{Demo.Run step 3} {\", \"wave=\", wave, \" name=\", name, \" count=\", count, \"}\"))", lua);
        Assert.DoesNotContain("{Demo.Run step 4}", lua);
        Assert.DoesNotContain("ignored=", lua);
        Assert.DoesNotContain("{Demo.Quiet step", lua);
    }

    [Fact]
    public async Task Task_delay_emits_coroutine_timer_runtime_call()
    {
        var src = """
            using System.Threading.Tasks;

            public static class AsyncDemo
            {
                public static async Task Tick()
                {
                    await Task.Delay(1000);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "AsyncDemo.cs");

        Assert.Contains("SF__.CorTimerPool__ = SF__.CorTimerPool__ or {}", lua);
        Assert.Contains("function SF__.CorAcquireTimer__()", lua);
        Assert.Contains("function SF__.CorReleaseTimer__(timer)", lua);
        Assert.Contains("TimerStart(timer, milliseconds / 1000, false, function()", lua);
        Assert.Contains("DestroyTimer(timer)", lua);
        Assert.Contains("return SF__.CorRun__(function()", lua);
        Assert.Contains("SF__.CorWait__(1000)", lua);
        Assert.DoesNotContain("Task.Delay", lua);
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("FourCC(\"A000\")", lua);
        Assert.Contains("CustomFunc()", lua);
    }

    [Fact]
    public async Task Frontend_user_partial_custom_binding_host_does_not_hide_generated_bindings()
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

        var bindingsDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "Generated"));
        await File.WriteAllTextAsync(Path.Combine(bindingsDir.FullName, "GlobalUsings.g.cs"),
            "global using static WC3;");
        await File.WriteAllTextAsync(Path.Combine(bindingsDir.FullName, "NativeExt.g.cs"),
            "public static partial class WC3 { public static int FourCC(string val) => throw null!; }");

        var wrapperDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "LuaWrapper"));
        await File.WriteAllTextAsync(Path.Combine(wrapperDir.FullName, "GlobalFunc.cs"),
            "public static partial class WC3 { public static int CustomFunc() => throw null!; }");

        var sourceFiles = dir.EnumerateFiles("*.cs", SearchOption.AllDirectories).ToArray();
        var compilation = await new RoslynFrontend(Array.Empty<string>(), new[] { "WC3" })
            .CompileAsync(sourceFiles, CancellationToken.None);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(errors.Length == 0, "C# compile errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));

        var module = new IRLowering(new[] { "WC3" }, dir, new[] { TranspileOptions.DefaultLibraryFolder })
            .Lower(compilation, CancellationToken.None);
        var lua = new LuaEmitter(TranspileOptions.DefaultRootTable).Emit(module);

        Assert.Contains("FourCC(\"A000\")", lua);
        Assert.Contains("CustomFunc()", lua);
        Assert.DoesNotContain("WC3", lua);
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
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
    public async Task Pipeline_preserves_existing_intellisense_project_file()
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
        var projectFile = Path.Combine(dir.FullName, dir.Name + ".csproj");
        var existingProject = "<Project><PropertyGroup><SharpForgeUserFile>true</SharpForgeUserFile></PropertyGroup></Project>";
        await File.WriteAllTextAsync(projectFile, existingProject);

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(Path.Combine(dir.FullName, "out.lua")),
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(existingProject, await File.ReadAllTextAsync(projectFile));
    }

    [Fact]
    public async Task Pipeline_init_only_copies_assets_and_skips_transpile_options()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(dir.FullName),
            PreprocessorSymbols: new[] { "BROKEN_IF_USED" },
            RootTable: "CUSTOM_ROOT",
            IgnoredClasses: Array.Empty<string>(),
            LibraryFolders: new[] { "not-libs" },
            CheckOnly: true,
            Verbose: false,
            InitOnly: true), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(dir.FullName, dir.Name + ".csproj")));
        Assert.True(File.Exists(Path.Combine(dir.FullName, "libs", "Jass-2.0.4", "Natives.g.cs")));
        Assert.False(File.Exists(Path.Combine(dir.FullName, TranspileOptions.DefaultOutputFileName)));
    }

    [Fact]
    public async Task Pipeline_overwrites_bundled_library_stubs_but_preserves_other_user_files()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            public static class Program
            {
                public static void Main()
                {
                    FourCC("A000");
                }
            }
            """);

        var jassDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "Jass-2.0.4"));
        var userOwnedNativeStub = "public static partial class JASS { public static void UserOwned() { } }";
        await File.WriteAllTextAsync(Path.Combine(jassDir.FullName, "Natives.g.cs"), userOwnedNativeStub);

        var customDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "User"));
        var userLibrary = "namespace UserLib; public static class KeepMe { }";
        var userLibraryPath = Path.Combine(customDir.FullName, "KeepMe.cs");
        await File.WriteAllTextAsync(userLibraryPath, userLibrary);

        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: output,
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var copiedNatives = await File.ReadAllTextAsync(Path.Combine(jassDir.FullName, "Natives.g.cs"));
        Assert.NotEqual(userOwnedNativeStub, copiedNatives);
        Assert.Contains("BJDebugMsg", copiedNatives);
        Assert.Equal(userLibrary, await File.ReadAllTextAsync(userLibraryPath));
        Assert.True(File.Exists(Path.Combine(jassDir.FullName, "NativeExt.g.cs")));
        Assert.True(File.Exists(Path.Combine(jassDir.FullName, "GlobalUsings.g.cs")));
    }

    [Fact]
    public async Task For_loop_and_else_if_emit_lua_control_flow()
    {
        var src = """
            public static class Loops
            {
                public static int Sum(int limit)
                {
                    var total = 0;
                    for (int i = 0; i < limit; i++)
                    {
                        if (i == 1)
                        {
                            continue;
                        }
                        else if (i == 3)
                        {
                            break;
                        }
                        total += i;
                    }
                    return total;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Loops.cs");

        Assert.Contains("local i = 0", lua);
        Assert.Matches(@"while\s+\(?\s*i\s*<\s*limit\s*\)?\s+do", lua);
        Assert.Contains("elseif (i == 3) then", lua);
        Assert.Contains("::continue::", lua);
        Assert.Matches(@"i\s*=\s*\(?\s*i\s*\+\s*1\s*\)?", lua);
    }

    [Fact]
    public async Task Auto_properties_and_static_constructor_emit_as_members()
    {
        var src = """
            public class Config
            {
                public int Hp { get; set; } = 5;
                public static bool Ready { get; set; }

                static Config()
                {
                    Ready = true;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Config.cs");

        Assert.Contains("SF__.Config.Ready = false", lua);
        Assert.Contains("SF__.Config.Ready = true", lua);
        Assert.Contains("self.Hp = 5", lua);
    }

    [Fact]
    public async Task Constructor_and_method_overloads_get_stable_lua_names()
    {
        var src = """
            public class Overloaded
            {
                public int Value;

                public Overloaded()
                {
                }

                public Overloaded(int value)
                {
                    Value = value;
                }

                public int Pick()
                {
                    return Value;
                }

                public int Pick(int fallback)
                {
                    return fallback;
                }

                public static int Run()
                {
                    var item = new Overloaded(7);
                    return item.Pick(3);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Overloaded.cs");

        Assert.Contains("function SF__.Overloaded.New_0()", lua);
        Assert.Contains("function SF__.Overloaded.New_1(value)", lua);
        Assert.Contains("function SF__.Overloaded:Pick_0()", lua);
        Assert.Contains("function SF__.Overloaded:Pick_1(fallback)", lua);
        Assert.Contains("SF__.Overloaded.New_1(7)", lua);
        Assert.Contains("item:Pick_1(3)", lua);
    }

    [Fact]
    public async Task Types_emit_in_stable_name_order()
    {
        var src = """
            namespace Zed { public static class Last { public static int F() { return 1; } } }
            namespace Alpha { public static class First { public static int F() { return 1; } } }
            """;

        var lua = await TranspileSourceAsync(src, "Ordering.cs");

        Assert.True(lua.IndexOf("-- Alpha.First", StringComparison.Ordinal) < lua.IndexOf("-- Zed.Last", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Static_initialization_dependencies_emit_before_dependents()
    {
        var src = """
            public static class CrusaderStrike
            {
                public static readonly int Id = Utils.Ability("A000");

                static CrusaderStrike()
                {
                    Utils.Register(Id);
                }
            }

            public class Utils
            {
                public static int Ability(string raw) { return 1; }
                public static void Register(int id) { }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Ordering.cs");

        Assert.True(
            lua.IndexOf("-- Utils", StringComparison.Ordinal) < lua.IndexOf("-- CrusaderStrike", StringComparison.Ordinal),
            lua);
        Assert.Contains("SF__.CrusaderStrike.Id = SF__.Utils.Ability(\"A000\")", lua);
        Assert.Contains("SF__.Utils.Register(SF__.CrusaderStrike.Id)", lua);
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

        var lua = await TranspileSourceAsync(src, "Comments.cs");

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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
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

        var lua = await TranspileSourceAsync(src, "Switches.cs");

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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

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

        var lua = await TranspileSourceAsync(src, "Enums.cs");

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

        var lua = await TranspileSourceAsync(src, "RegexChecks.cs");

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
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
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

    [Fact]
    public async Task Inheritance_emits_base_metatable_constructor_and_base_method_calls()
    {
        var src = """
            namespace Game
            {
                public class Unit
                {
                    public int Hp;

                    public Unit(int hp)
                    {
                        Hp = hp;
                    }

                    public virtual string Label()
                    {
                        return "unit";
                    }
                }

                public class Hero : Unit
                {
                    public int Mana = 10;

                    public Hero(int hp) : base(hp)
                    {
                    }

                    public override string Label()
                    {
                        return base.Label();
                    }
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Inheritance.cs");

        Assert.True(lua.IndexOf("-- Game.Unit", StringComparison.Ordinal) < lua.IndexOf("-- Game.Hero", StringComparison.Ordinal));
        Assert.Contains("setmetatable(SF__.Game.Hero, { __index = SF__.Game.Unit })", lua);
        Assert.Contains("function SF__.Game.Unit.__Init(self, hp)", lua);
        Assert.Matches(@"function SF__\.Game\.Hero\.__Init\(self, hp\d*\)", lua);
        Assert.Matches(@"SF__\.Game\.Unit\.__Init\(self, hp\d*\)", lua);
        Assert.Contains("self.Mana = 10", lua);
        Assert.Contains("function SF__.Game.Unit:Label()", lua);
        Assert.Contains("function SF__.Game.Hero:Label()", lua);
        Assert.Contains("SF__.Game.Unit.Label(self)", lua);
    }

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

        var lua = await TranspileSourceAsync(src, "Exceptions.cs");

        Assert.Contains("local __sf_ok, __sf_err = pcall(function()", lua);
        Assert.Contains("error(SF__.System.Exception.New_1(\"boom\"))", lua);
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

        var lua = await TranspileSourceAsync(src, "FinallyOnly.cs");

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

        var lua = await TranspileSourceAsync(src, "Interfaces.cs");

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

    [Fact]
    public async Task Struct_dictionary_keys_use_linear_typed_equals_lookup()
    {
        var src = """
            namespace SFLib.Collections
            {
                public class Dictionary<K, V>
                {
                    public int Count => 0;
                    public List<K> Keys => default!;
                    public List<V> Values => default!;
                    public V this[K key] { get => default!; set { } }
                    public void Add(K key, V value) { }
                    public bool ContainsKey(K key) { return false; }
                    public bool Remove(K key) { return false; }
                    public void Clear() { }
                    public Enumerator GetEnumerator() => default!;
                    public class Enumerator
                    {
                        public KeyValue<K, V> Current => default!;
                        public bool MoveNext() { return false; }
                    }
                }

                public class List<T>
                {
                }

                public class KeyValue<K, V>
                {
                    public K Key => default!;
                    public V Value => default!;
                }
            }

            public struct Cell
            {
                public int X;
                public int Y;

                public bool Equals(Cell other)
                {
                    return X == other.X && Y == other.Y;
                }
            }

            public static class Demo
            {
                public static int Run()
                {
                    var cells = new SFLib.Collections.Dictionary<Cell, int>();
                    var a = new Cell { X = 1, Y = 2 };
                    var b = new Cell { X = 1, Y = 2 };
                    cells.Add(a, 7);
                    cells[b] = 8;
                    var hasB = cells.ContainsKey(b);
                    var value = cells[b];
                    var keys = cells.Keys;
                    var values = cells.Values;
                    foreach (var kv in cells)
                    {
                        value = value + kv.Value;
                    }
                    cells.Remove(b);
                    cells.Clear();
                    return hasB ? value : 0;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructDictionaryKeys.cs");

        Assert.Contains("function SF__.DictLinearNew__(keyEquals)", lua);
        Assert.Contains("function SF__.DictLinearFind__(dict, key)", lua);
        Assert.Contains("if dict.keyEquals(storedKey, key) then return i end", lua);
        Assert.Contains("local cells = SF__.DictLinearNew__(function(left, right)", lua);
        Assert.Contains("return SF__.Cell.Equals(left.X, left.Y, right.X, right.Y)", lua);
        Assert.Contains("SF__.DictLinearAdd__(cells", lua);
        Assert.Contains("SF__.DictLinearSet__(cells", lua);
        Assert.Contains("local hasB = SF__.DictLinearContainsKey__(cells", lua);
        Assert.Contains("local value = SF__.DictLinearGet__(cells", lua);
        Assert.Contains("local keys = SF__.DictLinearKeys__(cells)", lua);
        Assert.Contains("local values = SF__.DictLinearValues__(cells)", lua);
        Assert.Contains("in SF__.DictLinearIterate__(dict", lua);
        Assert.Contains("SF__.DictLinearRemove__(cells", lua);
        Assert.Contains("SF__.DictLinearClear__(cells)", lua);
        Assert.DoesNotContain("function SF__.DictNew__()", lua);
        Assert.DoesNotContain("function SF__.DictAdd__(dict, key, value)", lua);
        Assert.DoesNotContain("function SF__.DictSet__(dict, key, value)", lua);
        Assert.DoesNotContain("SF__.DictAdd__(cells", lua);
        Assert.DoesNotContain("SF__.DictSet__(cells", lua);
        Assert.DoesNotContain("GetHashCode", lua);
    }

    [Fact]
    public async Task Struct_hash_set_values_use_linear_typed_equals_lookup()
    {
        var src = """
            namespace SFLib.Collections
            {
                public class HashSet<T>
                {
                    public int Count => 0;
                    public bool Add(T item) { return false; }
                    public bool Contains(T item) { return false; }
                    public bool Remove(T item) { return false; }
                    public void Clear() { }
                    public Enumerator GetEnumerator() => default!;
                    public class Enumerator
                    {
                        public T Current => default!;
                        public bool MoveNext() { return false; }
                    }
                }
            }

            public struct Cell
            {
                public int X;
                public int Y;

                public bool Equals(Cell other)
                {
                    return X == other.X && Y == other.Y;
                }
            }

            public static class Demo
            {
                public static int Run()
                {
                    var cells = new SFLib.Collections.HashSet<Cell>();
                    var a = new Cell { X = 1, Y = 2 };
                    var b = new Cell { X = 1, Y = 2 };
                    var added = cells.Add(a);
                    var duplicate = cells.Add(b);
                    var hasB = cells.Contains(b);
                    var count = cells.Count;
                    foreach (var cell in cells)
                    {
                        count = count + cell.X;
                    }
                    var removed = cells.Remove(b);
                    cells.Clear();
                    return added && !duplicate && hasB && removed ? count : 0;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructHashSetValues.cs");

        Assert.Contains("function SF__.HashSetLinearNew__(equals)", lua);
        Assert.Contains("function SF__.HashSetLinearFind__(set, value)", lua);
        Assert.Contains("if set.equals(storedValue, value) then return i end", lua);
        Assert.Contains("local cells = SF__.HashSetLinearNew__(function(left, right)", lua);
        Assert.Contains("return SF__.Cell.Equals(left.X, left.Y, right.X, right.Y)", lua);
        Assert.Contains("local added = SF__.HashSetLinearAdd__(cells", lua);
        Assert.Contains("local duplicate = SF__.HashSetLinearAdd__(cells", lua);
        Assert.Contains("local hasB = SF__.HashSetLinearContains__(cells", lua);
        Assert.Contains("local count = SF__.HashSetLinearCount__(cells)", lua);
        Assert.Contains("in SF__.HashSetLinearIterate__(set)", lua);
        Assert.Contains("local removed = SF__.HashSetLinearRemove__(cells", lua);
        Assert.Contains("SF__.HashSetLinearClear__(cells)", lua);
        Assert.DoesNotContain("function SF__.HashSetNew__()", lua);
    }

    [Fact]
    public async Task Struct_list_equality_operations_use_typed_equals()
    {
        var src = """
            namespace SFLib.Collections
            {
                public class List<T>
                {
                    public void Add(T item) { }
                    public bool Contains(T item) { return false; }
                    public int IndexOf(T item) { return -1; }
                    public bool Remove(T item) { return false; }
                }
            }

            public struct Cell
            {
                public int X;
                public int Y;

                public bool Equals(Cell other)
                {
                    return X == other.X && Y == other.Y;
                }
            }

            public static class Demo
            {
                public static int Run()
                {
                    var cells = new SFLib.Collections.List<Cell>();
                    var a = new Cell { X = 1, Y = 2 };
                    var b = new Cell { X = 1, Y = 2 };
                    cells.Add(a);
                    var hasB = cells.Contains(b);
                    var index = cells.IndexOf(b);
                    var removed = cells.Remove(b);
                    return hasB && removed ? index : -1;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructListEquality.cs");

        Assert.Contains("function SF__.ListIndexOf__(list, value, equals)", lua);
        Assert.Contains("if equals ~= nil then", lua);
        Assert.Contains("if equals(SF__.ListUnwrap__(item), value) then return i - 1 end", lua);
        Assert.Contains("local hasB = SF__.ListContains__(cells", lua);
        Assert.Contains("local index = SF__.ListIndexOf__(cells", lua);
        Assert.Contains("local removed = SF__.ListRemove__(cells", lua);
        Assert.Contains("function(left, right)", lua);
        Assert.Contains("return SF__.Cell.Equals(left.X, left.Y, right.X, right.Y)", lua);
    }

    [Fact]
    public async Task Struct_list_items_use_parallel_field_arrays_for_supported_local_operations()
    {
        var src = """
            namespace SFLib.Collections
            {
                public class List<T>
                {
                    public void Add(T item) { }
                    public T this[int index] { get { return default!; } set { } }
                }
            }

            public struct AbilityData
            {
                public float DamageScaling;
                public float ArtOfWarChance;
            }

            public static class Demo
            {
                public static AbilityData GetAbilityData(int level)
                {
                    return new AbilityData
                    {
                        DamageScaling = level * 1.5f,
                        ArtOfWarChance = level * 0.1f,
                    };
                }

                public static string Run()
                {
                    var datas = new SFLib.Collections.List<AbilityData>();
                    datas.Add(GetAbilityData(1));
                    var data = datas[0];
                    return $"{datas[0].DamageScaling:F0}:{data.ArtOfWarChance:F0}";
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructListItems.cs");

        Assert.Contains("local datas__DamageScaling, datas__ArtOfWarChance = {}, {}", lua);
        Assert.Contains("local item__DamageScaling, item__ArtOfWarChance = SF__.Demo.GetAbilityData(1)", lua);
        Assert.Contains("table.insert(datas__DamageScaling, item__DamageScaling)", lua);
        Assert.Contains("table.insert(datas__ArtOfWarChance, item__ArtOfWarChance)", lua);
        Assert.Contains("local data__DamageScaling, data__ArtOfWarChance = datas__DamageScaling[(0 + 1)], datas__ArtOfWarChance[(0 + 1)]", lua);
        Assert.Contains("return SF__.StrConcat__(string.format(\"%.0f\", datas__DamageScaling[(0 + 1)]), \":\", string.format(\"%.0f\", data__ArtOfWarChance))", lua);
        Assert.DoesNotContain("SF__.ListAdd__(datas", lua);
        Assert.DoesNotContain("SF__.ListGet__(datas", lua);
    }

    [Fact]
    public async Task Struct_collection_equality_without_typed_equals_is_diagnostic()
    {
        var src = """
            namespace SFLib.Collections
            {
                public class List<T>
                {
                    public bool Contains(T item) { return false; }
                }

                public class Dictionary<K, V>
                {
                    public V this[K key] { get => default!; set { } }
                }
            }

            public struct Cell
            {
                public int X;
            }

            public static class Demo
            {
                public static void Run()
                {
                    var list = new SFLib.Collections.List<Cell>();
                    var hasCell = list.Contains(new Cell { X = 1 });
                    var cells = new SFLib.Collections.Dictionary<Cell, int>();
                    cells[new Cell { X = 1 }] = 1;
                }
            }
            """;

        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "StructCollectionMissingEquals.cs");
        await File.WriteAllTextAsync(file, src);

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var module = new IRLowering().Lower(compilation, CancellationToken.None);

        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("struct collection equality for 'Cell' requires public bool Equals(Cell other)", StringComparison.Ordinal));
        Assert.Contains(module.Diagnostics, diagnostic => diagnostic.Contains("hashing and boxed Equals(object) are not used", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Struct_interfaces_are_not_emitted_as_runtime_metadata()
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
            }

            public static class Checks
            {
                public static bool Same(AbilityData left, AbilityData right)
                {
                    return left.Equals(right);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructInterfaces.cs");

        Assert.Contains("function SF__.AbilityData.Equals(self__DamageScaling, other__DamageScaling)", lua);
        Assert.DoesNotContain("SF__.AbilityData.__sf_interfaces", lua);
        Assert.DoesNotContain("IEquatable", lua);
    }

    [Fact]
    public async Task Ternary_expressions_emit_helper_call_not_and_or()
    {
        var src = """
            public static class Ternary
            {
                public static int Max(int a, int b) => a > b ? a : b;

                public static string Label(bool flag) => flag ? "yes" : "no";

                public static int FalsyBranch(bool flag) => flag ? 0 : 1;
            }
            """;

        var lua = await TranspileSourceAsync(src, "Ternary.cs");

        // Helper must be emitted exactly once.
        Assert.Contains("function SF__.Ternary__(cond, a, b)", lua);
        Assert.Contains("if cond then return a else return b end", lua);
        // All three uses must route through the helper, never the unsafe `and/or` idiom.
        Assert.Contains("SF__.Ternary__(", lua);
        Assert.DoesNotContain("and a or b", lua);
        // The falsy-branch case (value1 == 0) is safe because the helper is used.
        Assert.Contains("SF__.Ternary__((a > b), a, b)", lua);
        Assert.Contains("SF__.Ternary__(flag, \"yes\", \"no\")", lua);
        Assert.Matches(@"SF__\.Ternary__\(flag\d*, 0, 1\)", lua);
    }

    [Fact]
    public async Task Float_literals_emit_source_text_not_double_expanded_value()
    {
        var src = "public static class Numbers { public static float F() => 0.65f; public static double D() => 0.65; }";
        var lua = await TranspileSourceAsync(src, "Numbers.cs");
        // float 0.65f must not be widened to the double-precision expansion 0.6499999761581421
        Assert.DoesNotContain("0.6499", lua);
        Assert.Contains("0.65", lua);
    }

    [Fact]
    public async Task Arrays_lists_indexing_and_foreach_emit_table_backed_collections()
    {
        var src = """
            using System.Collections.Generic;

            public static class Collections
            {
                public static int SumArray()
                {
                    var values = new[] { 1, 2, 3 };
                    var total = 0;
                    foreach (var value in values)
                    {
                        total += value;
                    }
                    return total + values[0] + values.Length;
                }

                public static int SumList()
                {
                    var values = new List<int> { 4, 5 };
                    values.Add(6);
                    return values[1] + values.Count;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Collections.cs");

        Assert.Contains("local values = {1, 2, 3}", lua);
        Assert.Contains("local collection = values", lua);
        Assert.Contains("for i, value in ipairs(collection) do", lua);
        Assert.Matches(@"values\[\(?\s*0\s*\+\s*1\s*\)?\]", lua);
        Assert.Contains("#values", lua);
        Assert.Matches(@"local values\d* = SF__\.ListNew__\(\{4, 5\}\)", lua);
        Assert.Matches(@"SF__\.ListAdd__\(values\d*, 6\)", lua);
        Assert.Matches(@"SF__\.ListGet__\(values\d*, 1\)", lua);
        Assert.Matches(@"SF__\.ListCount__\(values\d*\)", lua);
    }

    [Fact]
    public async Task List_usage_emits_only_required_helpers()
    {
        var src = """
            using System.Collections.Generic;

            public static class Demo
            {
                public static int Run()
                {
                    var values = new List<int>();
                    values.Add(1);
                    return values.Count;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "MinimalListHelpers.cs");

        Assert.Contains("SF__.ListNil__ = SF__.ListNil__ or {}", lua);
        Assert.Contains("function SF__.ListWrap__(value)", lua);
        Assert.Contains("function SF__.ListNew__(items)", lua);
        Assert.Contains("function SF__.ListAdd__(list, value)", lua);
        Assert.Contains("function SF__.ListCount__(list)", lua);
        Assert.DoesNotContain("function SF__.ListUnwrap__(value)", lua);
        Assert.DoesNotContain("function SF__.ListGet__(list, index)", lua);
        Assert.DoesNotContain("function SF__.ListSort__(list, less)", lua);
        Assert.DoesNotContain("function SF__.ListRemove__(list, value, equals)", lua);
        Assert.DoesNotContain("function SF__.ListIterate__(list)", lua);
        Assert.DoesNotContain("function SF__.ListToArray__(list)", lua);
    }

    [Fact]
    public async Task Dictionary_usage_emits_only_required_helpers()
    {
        var src = """
            using SFLib.Collections;

            namespace SFLib.Collections
            {
                public class Dictionary<K, V>
                {
                    public int Count => 0;
                    public void Add(K key, V value) { }
                    public bool ContainsKey(K key) { return false; }
                }
            }

            public static class Demo
            {
                public static int Run()
                {
                    var table = new Dictionary<string, int>();
                    table.Add("a", 1);
                    var hasA = table.ContainsKey("a");
                    return table.Count;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "MinimalDictionaryHelpers.cs");

        Assert.Contains("SF__.DictNil__ = SF__.DictNil__ or {}", lua);
        Assert.Contains("function SF__.DictNew__()", lua);
        Assert.Contains("function SF__.DictAdd__(dict, key, value)", lua);
        Assert.Contains("function SF__.DictContainsKey__(dict, key)", lua);
        Assert.Contains("function SF__.DictCount__(dict)", lua);
        Assert.DoesNotContain("function SF__.DictGet__(dict, key)", lua);
        Assert.DoesNotContain("function SF__.DictRemove__(dict, key)", lua);
        Assert.DoesNotContain("function SF__.DictKeys__(dict)", lua);
        Assert.DoesNotContain("function SF__.DictValues__(dict)", lua);
        Assert.DoesNotContain("function SF__.DictIterate__(dict)", lua);
        Assert.DoesNotContain("function SF__.DictLinear", lua);
        Assert.DoesNotContain("function SF__.List", lua);
    }

    [Fact]
    public async Task Dictionary_keys_emit_minimal_list_dependencies()
    {
        var src = """
            using SFLib.Collections;

            namespace SFLib.Collections
            {
                public class List<T>
                {
                }

                public class Dictionary<K, V>
                {
                    public List<K> Keys => default!;
                }
            }

            public static class Demo
            {
                public static List<string> Run()
                {
                    var table = new Dictionary<string, int>();
                    return table.Keys;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "DictionaryKeysListDependencies.cs");

        Assert.Contains("function SF__.DictNew__()", lua);
        Assert.Contains("function SF__.DictKeys__(dict)", lua);
        Assert.Contains("function SF__.ListNew__(items)", lua);
        Assert.Contains("function SF__.ListWrap__(value)", lua);
        Assert.Contains("SF__.ListNil__ = SF__.ListNil__ or {}", lua);
        Assert.DoesNotContain("function SF__.DictValues__(dict)", lua);
        Assert.DoesNotContain("function SF__.DictIterate__(dict)", lua);
        Assert.DoesNotContain("function SF__.ListAdd__(list, value)", lua);
        Assert.DoesNotContain("function SF__.ListGet__(list, index)", lua);
        Assert.DoesNotContain("function SF__.ListIterate__(list)", lua);
    }

    [Fact]
    public async Task SFLib_collections_emit_stub_backed_lua_helpers()
    {
        var src = """
            using SFLib.Collections;

            namespace SFLib.Collections
            {
                public class List<T>
                {
                    public int Count => 0;
                    public T this[int index] { get => default!; set { } }
                    public void Add(T item) { }
                    public void AddRange(List<T> items) { }
                    public void AddRange(T[] items) { }
                    public void Clear() { }
                    public bool Contains(T item) { return false; }
                    public int IndexOf(T item) { return -1; }
                    public void Insert(int index, T item) { }
                    public bool Remove(T item) { return false; }
                    public void RemoveAt(int index) { }
                    public void Reverse() { }
                    public void Sort() { }
                    public void Sort(global::System.Func<T, T, bool> less) { }
                    public T[] ToArray() { return default!; }
                    public Enumerator GetEnumerator() => default!;
                    public class Enumerator
                    {
                        public T Current => default!;
                        public bool MoveNext() { return false; }
                    }
                }

                public class Dictionary<K, V>
                {
                    public int Count => 0;
                    public List<K> Keys => default!;
                    public List<V> Values => default!;
                    public V this[K key] { get => default!; set { } }
                    public void Add(K key, V value) { }
                    public void Clear() { }
                    public bool ContainsKey(K key) { return false; }
                    public V? Get(K key) { return default; }
                    public void Set(K key, V value) { }
                    public bool Remove(K key) { return false; }
                    public Enumerator GetEnumerator() => default!;
                    public class Enumerator
                    {
                        public KeyValue<K, V> Current => default!;
                        public bool MoveNext() { return false; }
                    }
                }

                public class KeyValue<K, V>
                {
                    public K Key => default!;
                    public V Value => default!;
                }
            }

            public static class Demo
            {
                public static int Run()
                {
                    var values = new List<int>();
                    values.Add(1);
                    values.Insert(1, 3);
                    var hasThree = values.Contains(3);
                    var index = values.IndexOf(3);
                    values.RemoveAt(0);
                    var removed = values.Remove(3);
                    values.AddRange(new[] { 4, 5 });
                    var more = new List<int>();
                    more.Add(6);
                    values.AddRange(more);
                    values.Reverse();
                    values.Sort();
                    values.Sort((a, b) => a > b);
                    foreach (var item in values)
                    {
                        index += item;
                    }
                    var array = values.ToArray();
                    values.Clear();

                    var table = new Dictionary<string, int>();
                    table["key"] = 1;
                    table.Add("k2", 2);
                    var hasKey = table.ContainsKey("key");
                    var keys = table.Keys;
                    var dictValues = table.Values;
                    var value = table["key"];
                    var nullable = new Dictionary<string, int?>();
                    nullable["gone"] = null;
                    nullable.Remove("gone");
                    table.Clear();
                    foreach (var kv in table)
                    {
                        var text = kv.Key + " = " + kv.Value;
                    }

                    return value + array[0] + index;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "SFLibCollections.cs");

        Assert.Contains("function SF__.DictNew__()", lua);
        Assert.Contains("function SF__.DictGet__(dict, key)", lua);
        Assert.Contains("function SF__.DictAdd__(dict, key, value)", lua);
        Assert.Contains("function SF__.DictSet__(dict, key, value)", lua);
        Assert.Contains("function SF__.DictRemove__(dict, key)", lua);
        Assert.Contains("function SF__.DictContainsKey__(dict, key)", lua);
        Assert.Contains("function SF__.DictClear__(dict)", lua);
        Assert.Contains("function SF__.DictKeys__(dict)", lua);
        Assert.Contains("function SF__.DictValues__(dict)", lua);
        Assert.Contains("function SF__.DictIterate__(dict)", lua);
        Assert.Contains("SF__.DictNil__ = SF__.DictNil__ or {}", lua);
        Assert.Contains("return { data = {}, keys = {}, version = 0 }", lua);
        Assert.Contains("local value = dict.data[key]", lua);
        Assert.Contains("if value == SF__.DictNil__ then return nil end", lua);
        Assert.Contains("if dict.data[key] ~= nil then error(\"duplicate key\") end", lua);
        Assert.Contains("table.insert(dict.keys, key)", lua);
        Assert.Contains("dict.version = dict.version + 1", lua);
        Assert.Contains("return dict.data[key] ~= nil", lua);
        Assert.Contains("dict.data = {}", lua);
        Assert.Contains("dict.keys = {}", lua);
        Assert.Contains("return SF__.ListNew__(items)", lua);
        Assert.Contains("list.items[i] = SF__.ListWrap__(value)", lua);
        Assert.Contains("local version = dict.version", lua);
        Assert.Contains("if dict.version ~= version then error(\"collection was modified during iteration\") end", lua);
        Assert.DoesNotContain("keySet", lua);
        Assert.Contains("SF__.ListNil__ = SF__.ListNil__ or {}", lua);
        Assert.Contains("function SF__.ListWrap__(value)", lua);
        Assert.Contains("function SF__.ListUnwrap__(value)", lua);
        Assert.Contains("function SF__.ListNew__(items)", lua);
        Assert.Contains("local list = { items = {}, version = 0 }", lua);
        Assert.Contains("function SF__.ListAdd__(list, value)", lua);
        Assert.Contains("function SF__.ListAddRange__(list, values)", lua);
        Assert.Contains("function SF__.ListClear__(list)", lua);
        Assert.Contains("function SF__.ListContains__(list, value, equals)", lua);
        Assert.Contains("function SF__.ListIndexOf__(list, value, equals)", lua);
        Assert.Contains("function SF__.ListInsert__(list, index, value)", lua);
        Assert.Contains("function SF__.ListRemove__(list, value, equals)", lua);
        Assert.Contains("function SF__.ListRemoveAt__(list, index)", lua);
        Assert.Contains("function SF__.ListReverse__(list)", lua);
        Assert.Contains("function SF__.ListIterate__(list)", lua);
        Assert.Contains("if list.version ~= version then error(\"collection was modified during iteration\") end", lua);
        Assert.Contains("local values = SF__.ListNew__({})", lua);
        Assert.Contains("SF__.ListAdd__(values, 1)", lua);
        Assert.Contains("SF__.ListInsert__(values, 1, 3)", lua);
        Assert.Contains("local hasThree = SF__.ListContains__(values, 3)", lua);
        Assert.Contains("local index = SF__.ListIndexOf__(values, 3)", lua);
        Assert.Contains("SF__.ListRemoveAt__(values, 0)", lua);
        Assert.Contains("local removed = SF__.ListRemove__(values, 3)", lua);
        Assert.Contains("SF__.ListAddRange__(values, {4, 5})", lua);
        Assert.Contains("SF__.ListAddRange__(values, more)", lua);
        Assert.Contains("SF__.ListReverse__(values)", lua);
        Assert.Contains("function SF__.ListSort__(list, less)", lua);
        Assert.Contains("local items = list.items", lua);
        Assert.Contains("while j >= 1 and compare(SF__.ListUnwrap__(value), SF__.ListUnwrap__(items[j])) do", lua);
        Assert.Contains("SF__.ListSort__(values)", lua);
        Assert.Contains("SF__.ListSort__(values, function(a, b)", lua);
        Assert.Contains("function SF__.ListToArray__(list)", lua);
        Assert.Contains("local array = SF__.ListToArray__(values)", lua);
        Assert.Contains("SF__.ListClear__(values)", lua);
        Assert.Contains("local KW__table = SF__.DictNew__()", lua);
        Assert.Contains("SF__.DictSet__(KW__table, \"key\", 1)", lua);
        Assert.Contains("SF__.DictAdd__(KW__table, \"k2\", 2)", lua);
        Assert.Contains("local hasKey = SF__.DictContainsKey__(KW__table, \"key\")", lua);
        Assert.Contains("local keys = SF__.DictKeys__(KW__table)", lua);
        Assert.Contains("local dictValues = SF__.DictValues__(KW__table)", lua);
        Assert.Contains("local value = SF__.DictGet__(KW__table, \"key\")", lua);
        Assert.Contains("SF__.DictSet__(nullable, \"gone\", nil)", lua);
        Assert.Contains("SF__.DictRemove__(nullable, \"gone\")", lua);
        Assert.Contains("SF__.DictClear__(KW__table)", lua);
        Assert.Contains("in SF__.DictIterate__(dict)", lua);
        Assert.Contains("for kv__Key, kv__Value in SF__.DictIterate__(dict) do", lua);
        Assert.DoesNotContain("local kv = {", lua);
        Assert.Contains("local text = SF__.StrConcat__(kv__Key, \" = \", kv__Value)", lua);
        Assert.DoesNotContain("-- SFLib.Collections.List", lua);
        Assert.DoesNotContain("-- SFLib.Collections.Dictionary", lua);
    }

    [Fact]
    public async Task Pipeline_lowers_bundled_SFLib_list_to_lua_table()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using SFLib.Collections;

            public class Program
            {
                public static void Main(string[] args)
                {
                    var list = new List<string>();
                    list.Add("Hello");
                    list.Add("World");

                    foreach (var item in list)
                    {
                        BJDebugMsg(item);
                    }
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("local list = SF__.ListNew__({})", lua);
        Assert.Contains("SF__.ListAdd__(list, \"Hello\")", lua);
        Assert.Contains("SF__.ListAdd__(list, \"World\")", lua);
        Assert.Contains("for i, item in SF__.ListIterate__(collection) do", lua);
        Assert.Contains("if list.version ~= version then error(\"collection was modified during iteration\") end", lua);
        Assert.DoesNotContain("SF__.SFLib.Collections.List.New()", lua);
        Assert.DoesNotContain("list:Add", lua);
        Assert.DoesNotContain("-- SFLib.Collections.List", lua);
    }

    [Fact]
    public async Task Pipeline_lowers_bundled_SFLib_queue_stack_and_hash_set_to_lua_helpers()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using SFLib.Collections;

            public class Program
            {
                public static void Main(string[] args)
                {
                    var queue = new Queue<string>();
                    queue.Enqueue("first");
                    queue.Enqueue("second");
                    var queuePeek = queue.Peek();
                    var queueItem = queue.Dequeue();
                    var queueHasSecond = queue.Contains("second");
                    var queueArray = queue.ToArray();
                    foreach (var item in queue)
                    {
                        BJDebugMsg(item);
                    }

                    var stack = new Stack<string>();
                    stack.Push("bottom");
                    stack.Push("top");
                    var stackPeek = stack.Peek();
                    var stackItem = stack.Pop();
                    var stackHasBottom = stack.Contains("bottom");
                    var stackArray = stack.ToArray();
                    foreach (var item in stack)
                    {
                        BJDebugMsg(item);
                    }

                    var set = new HashSet<string>();
                    var added = set.Add("alpha");
                    var duplicate = set.Add("alpha");
                    var hasAlpha = set.Contains("alpha");
                    var setArray = set.ToArray();
                    foreach (var item in set)
                    {
                        BJDebugMsg(item);
                    }
                    var removed = set.Remove("alpha");
                    set.Clear();
                    BJDebugMsg(queuePeek + queueItem + stackPeek + stackItem + queueHasSecond + stackHasBottom + added + duplicate + hasAlpha + removed + queueArray.Length + stackArray.Length + setArray.Length);
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("local queue = SF__.ListNew__({})", lua);
        Assert.Contains("SF__.ListAdd__(queue, \"first\")", lua);
        Assert.Contains("local queuePeek = SF__.QueuePeek__(queue)", lua);
        Assert.Contains("local queueItem = SF__.QueueDequeue__(queue)", lua);
        Assert.Contains("local queueHasSecond = SF__.ListContains__(queue, \"second\")", lua);
        Assert.Contains("local queueArray = SF__.ListToArray__(queue)", lua);
        Assert.Contains("local stack = SF__.ListNew__({})", lua);
        Assert.Contains("SF__.ListAdd__(stack, \"bottom\")", lua);
        Assert.Contains("local stackPeek = SF__.StackPeek__(stack)", lua);
        Assert.Contains("local stackItem = SF__.StackPop__(stack)", lua);
        Assert.Contains("local stackHasBottom = SF__.ListContains__(stack, \"bottom\")", lua);
        Assert.Contains("local stackArray = SF__.StackToArray__(stack)", lua);
        Assert.Contains("in SF__.StackIterate__(", lua);
        Assert.Contains("local set = SF__.HashSetNew__()", lua);
        Assert.Contains("local added = SF__.HashSetAdd__(set, \"alpha\")", lua);
        Assert.Contains("local duplicate = SF__.HashSetAdd__(set, \"alpha\")", lua);
        Assert.Contains("local hasAlpha = SF__.HashSetContains__(set, \"alpha\")", lua);
        Assert.Contains("local setArray = SF__.HashSetToArray__(set)", lua);
        Assert.Contains("in SF__.HashSetIterate__(", lua);
        Assert.Contains("local removed = SF__.HashSetRemove__(set, \"alpha\")", lua);
        Assert.Contains("SF__.HashSetClear__(set)", lua);
        Assert.DoesNotContain("-- SFLib.Collections.Queue", lua);
        Assert.DoesNotContain("-- SFLib.Collections.Stack", lua);
        Assert.DoesNotContain("-- SFLib.Collections.HashSet", lua);
    }

    [Fact]
    public async Task Pipeline_emits_bundled_SFLib_interface_metadata_for_implementing_classes()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using SFLib.Contracts;

            public class BluntData : IEquatable<BluntData>
            {
                public float BluntDamage;

                public bool Equals(BluntData other)
                {
                    return BluntDamage == other.BluntDamage;
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("-- SFLib.Contracts.IEquatable", lua);
        Assert.Contains("SF__.SFLib.Contracts.IEquatable = SF__.SFLib.Contracts.IEquatable or {}", lua);
        Assert.Contains("SF__.BluntData.__sf_interfaces = {[SF__.SFLib.Contracts.IEquatable] = true}", lua);
    }

    [Fact]
    public async Task Pipeline_lowers_bundled_SFLib_lua_interop_to_raw_lua_access()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using SFLib.Interop;

            public class Program
            {
                public static void Main(string[] args)
                {
                    LuaObject table = LuaInterop.CreateTable();
                    LuaObject frameTimer = LuaInterop.Require("Lib.FrameTimer");
                    LuaObject systems = LuaInterop.Require("System.ItemSystem");
                    LuaObject created = LuaInterop.Call<LuaObject>(systems, "new", 7);
                    int charges = LuaInterop.Get<int>(created, "charges");

                    LuaInterop.Set(created, "charges", charges + 1);
                    LuaInterop.SetGlobal("CurrentItemSystem", created);
                    LuaInterop.Call(frameTimer, "start", created);
                    LuaInterop.CallMethod(created, "Method", 1, "abc");
                    LuaInterop.CallGlobal("BJDebugMsg", "ready");
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("local KW__table = {}", lua);
        Assert.Contains("local frameTimer = require(\"Lib.FrameTimer\")", lua);
        Assert.Contains("local systems = require(\"System.ItemSystem\")", lua);
        Assert.Contains("local created = systems.new(7)", lua);
        Assert.Contains("local charges = created.charges", lua);
        Assert.Contains("created.charges = (charges + 1)", lua);
        Assert.Contains("CurrentItemSystem = created", lua);
        Assert.Contains("frameTimer.start(created)", lua);
        Assert.Contains("created:Method(1, \"abc\")", lua);
        Assert.Contains("BJDebugMsg(\"ready\")", lua);
        Assert.DoesNotContain("-- SFLib.Interop.LuaInterop", lua);
        Assert.DoesNotContain("SF__.SFLib.Interop.LuaInterop", lua);
    }

    [Fact]
    public async Task Pipeline_lowers_lua_object_wrappers_to_bound_lua_module_calls()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var wrapperDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "Lua"));
        await File.WriteAllTextAsync(Path.Combine(wrapperDir.FullName, "FrameTimer.cs"), """
            using System;
            using SFLib.Interop;

            namespace Lua;

            [Lua(Module = "Lib.FrameTimer")]
            public class FrameTimer : LuaObject
            {
                [Lua(StaticMethod = "new")]
                public FrameTimer(Action<float> func, int count, int loops) => throw new NotImplementedException();

                public static LuaObject PauseAll() => throw new NotImplementedException();

                [Lua(StaticMethod = "fromHandle")]
                public static FrameTimer FromHandle(LuaObject handle) => throw new NotImplementedException();

                public void Start() => throw new NotImplementedException();

                [Lua(Method = "resume_now")]
                public void Resume() => throw new NotImplementedException();

                [Lua(Name = "stop_now")]
                public void Stop() => throw new NotImplementedException();
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(wrapperDir.FullName, "Time.cs"), """
            using SFLib.Interop;

            namespace Lua;

            [Lua(Module = "Lib.Time")]
            public class Time : LuaObject
            {
                [Lua(Name = "Time")]
                public static float CurrentTime;
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using Lua;
            using SFLib.Interop;

            public class Program
            {
                public static void Main()
                {
                    LuaInterop.SetGlobal("CLI", LuaInterop.CreateTable());
                    var handle = LuaInterop.CreateTable();
                    var fromHandle = FrameTimer.FromHandle(handle);
                    var paused = FrameTimer.PauseAll();
                    var timer = new FrameTimer(dt => LuaInterop.CallGlobal("Tick", Time.CurrentTime + dt), 1, -1);
                    timer.Start();
                    timer.Resume();
                    timer.Stop();
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.True(lua.IndexOf("CLI = {}", StringComparison.Ordinal) < lua.IndexOf("local FrameTimer = require(\"Lib.FrameTimer\")", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local FrameTimer = require(\"Lib.FrameTimer\")", StringComparison.Ordinal) < lua.IndexOf("local Time = require(\"Lib.Time\")", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local Time = require(\"Lib.Time\")", StringComparison.Ordinal) < lua.IndexOf("local fromHandle = FrameTimer.fromHandle(handle)", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local Time = require(\"Lib.Time\")", StringComparison.Ordinal) < lua.IndexOf("local handle = {}", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local FrameTimer = require(\"Lib.FrameTimer\")", StringComparison.Ordinal) < lua.IndexOf("local fromHandle = FrameTimer.fromHandle(handle)", StringComparison.Ordinal));
        Assert.Contains("local FrameTimer = require(\"Lib.FrameTimer\")", lua);
        Assert.Contains("local Time = require(\"Lib.Time\")", lua);
        Assert.Contains("local fromHandle = FrameTimer.fromHandle(handle)", lua);
        Assert.Contains("local paused = FrameTimer.PauseAll()", lua);
        Assert.Contains("local timer = FrameTimer.new(function(dt)", lua);
        Assert.Contains("Tick((Time.Time + dt))", lua);
        Assert.DoesNotContain("Time.CurrentTime", lua);
        Assert.Contains("timer:Start()", lua);
        Assert.Contains("timer:resume_now()", lua);
        Assert.Contains("timer:stop_now()", lua);
        Assert.DoesNotContain("SF__.Lua.FrameTimer", lua);
        Assert.DoesNotContain("-- Lua.FrameTimer", lua);
    }

    [Fact]
    public async Task Pipeline_emits_lua_class_for_opted_in_lua_object_subclass()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var wrapperDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "LuaWrapper"));
        var systemsDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "Systems"));
        await File.WriteAllTextAsync(Path.Combine(wrapperDir.FullName, "SystemBase.cs"), """
            using SFLib.Interop;

            namespace LuaWrapper;

            [Lua(Module = "System.SystemBase")]
            public class SystemBase : LuaObject
            {
                [Lua(StaticMethod = "new")]
                public SystemBase() { }

                public virtual void Awake() { }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(systemsDir.FullName, "InitAbilitiesSystem.cs"), """
            using LuaWrapper;
            using SFLib.Interop;

            namespace Systems;

            [Lua(Class = "InitAbilitiesSystem")]
            public class InitAbilitiesSystem : SystemBase
            {
                public override void Awake()
                {
                    LuaInterop.Require("Ability.Evasion");
                }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using Systems;

            public class Program
            {
                public static void Main()
                {
                    var system = new InitAbilitiesSystem();
                    system.Awake();
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("local SystemBase = require(\"System.SystemBase\")", lua);
        Assert.Contains("SF__.Systems.InitAbilitiesSystem = SF__.Systems.InitAbilitiesSystem or class(\"InitAbilitiesSystem\", SystemBase)", lua);
        Assert.Contains("SF__.Systems.InitAbilitiesSystem.__sf_base = SystemBase", lua);
        Assert.Contains("function SF__.Systems.InitAbilitiesSystem:Awake()", lua);
        Assert.Contains("require(\"Ability.Evasion\")", lua);
        Assert.Contains("function SF__.Systems.InitAbilitiesSystem.__Init(self)", lua);
        Assert.Contains("function SF__.Systems.InitAbilitiesSystem.New()", lua);
        Assert.Contains("local self = SF__.Systems.InitAbilitiesSystem.new()", lua);
        Assert.Contains("local system = SF__.Systems.InitAbilitiesSystem.New()", lua);
        Assert.Contains("system:Awake()", lua);
        Assert.DoesNotContain("SF__.LuaWrapper.SystemBase", lua);
    }

    [Fact]
    public async Task Pipeline_emits_class_level_lua_requires_before_type_table()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "EntryClass.cs"), """
            using SFLib.Interop;

            [Lua(Require = "Lib.class")]
            [Lua(Require = "Lib.maths")]
            public class EntryClass
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.True(lua.IndexOf("require(\"Lib.class\")", StringComparison.Ordinal) < lua.IndexOf("require(\"Lib.maths\")", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("require(\"Lib.maths\")", StringComparison.Ordinal) < lua.IndexOf("SF__.EntryClass = SF__.EntryClass or {}", StringComparison.Ordinal));
        Assert.Contains("function SF__.EntryClass.Main()", lua);
    }

    [Fact]
    public async Task Nested_types_with_same_name_keep_parent_type_path()
    {
        var src = """
            namespace Demo;

            public class First
            {
                public class Enumerator
                {
                    public int Value() { return 1; }
                }
            }

            public class Second
            {
                public class Enumerator
                {
                    public int Value() { return 2; }
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "NestedEnumerators.cs");

        Assert.Contains("-- Demo.First.Enumerator", lua);
        Assert.Contains("SF__.Demo.First.Enumerator = SF__.Demo.First.Enumerator or {}", lua);
        Assert.Contains("-- Demo.Second.Enumerator", lua);
        Assert.Contains("SF__.Demo.Second.Enumerator = SF__.Demo.Second.Enumerator or {}", lua);
    }

    [Fact]
    public async Task Double_underscore_user_identifiers_are_rejected()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Reserved.cs"),
            "public static class Reserved { public static int Run() { var bad__name = 1; return bad__name; } }");

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

        var lua = await TranspileSourceAsync(src, "Names.cs");

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

        var lua = await TranspileSourceAsync(src, "NativeExt.cs");

        Assert.Contains("return FourCC(\"hfoo\")", lua);
        Assert.DoesNotContain("JASS", lua);
    }

    [Fact]
    public async Task Jass_filter_lambda_emits_lua_function_argument()
    {
        var src = """
            using System;
            using SFLib.Interop;
            using static JASS;

            namespace SFLib.Interop
            {
                public sealed class LuaObject { }

                public static class LuaInterop
                {
                    public static LuaObject CallGlobal(string name, object arg) => throw null!;
                }
            }

            public static partial class JASS
            {
                public static object bj_mapInitialPlayableArea = null!;
                public static object CreateGroup() => throw null!;
                public static object Filter(Func<bool> func) => throw null!;
                public static void GroupEnumUnitsInRect(object group, object rect, object filter) => throw null!;
                public static void DestroyGroup(object group) => throw null!;
                public static object GetFilterUnit() => throw null!;
            }

            public static class Program
            {
                public static void Main()
                {
                    var group = CreateGroup();
                    GroupEnumUnitsInRect(group, bj_mapInitialPlayableArea, Filter(() =>
                    {
                        LuaInterop.CallGlobal("ExTriggerRegisterNewUnitExec", GetFilterUnit());
                        return true;
                    }));
                    DestroyGroup(group);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "FilterLambda.cs");

        Assert.Contains("local group = CreateGroup()", lua);
        Assert.Contains("GroupEnumUnitsInRect(group, bj_mapInitialPlayableArea, Filter(function()", lua);
        Assert.Contains("ExTriggerRegisterNewUnitExec(GetFilterUnit())", lua);
        Assert.Contains("return true", lua);
        Assert.Contains("end))", lua);
        Assert.DoesNotContain("unsupported expression: ParenthesizedLambdaExpression", lua);
    }

    [Fact]
    public async Task Array_allocation_without_initializer_emits_empty_table()
    {
        var src = """
            public static class Arrays
            {
                public static int[] NewItems(int count)
                {
                    return new int[count];
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "ArrayNew.cs");

        Assert.Contains("return {}", lua);
        Assert.DoesNotContain("unsupported expr: ArrayCreationExpression", lua);
    }

    [Fact]
    public async Task Vector_style_structs_emit_constructors_fields_and_operator_calls()
    {
        var src = """
            public struct Vector2
            {
                public int X;
                public int Y;

                public Vector2(int x, int y)
                {
                    X = x;
                    Y = y;
                }

                public static Vector2 operator +(Vector2 a, Vector2 b)
                {
                    return new Vector2(a.X + b.X, a.Y + b.Y);
                }
            }

            public static class MathDemo
            {
                public static int Run()
                {
                    var a = new Vector2(1, 2);
                    var b = new Vector2(3, 4);
                    var c = a + b;
                    return c.X;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Vector2.cs");

        Assert.Contains("-- Vector2", lua);
        Assert.DoesNotContain("System.ValueType", lua);
        Assert.Contains("function SF__.Vector2.op_Addition(a__X, a__Y, b__X, b__Y)", lua);
        Assert.Contains("return (a__X + b__X), (a__Y + b__Y)", lua);
        Assert.Matches(@"local a__X\d*, a__Y\d* = 1, 2", lua);
        Assert.Matches(@"local b__X\d*, b__Y\d* = 3, 4", lua);
        Assert.Matches(@"local c__X\d*, c__Y\d* = SF__\.Vector2\.op_Addition\(a__X\d*, a__Y\d*, b__X\d*, b__Y\d*\)", lua);
        Assert.Contains("return c__X", lua);
        Assert.DoesNotContain("function SF__.Vector2.__Init", lua);
        Assert.DoesNotContain("function SF__.Vector2.New", lua);
        Assert.DoesNotContain("SF__.Vector2.New(", lua);
    }

    [Fact]
    public async Task Struct_locals_used_only_by_fields_are_flattened()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public Vector2(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }
            }

            public static class MathDemo
            {
                public static Vector2 Make(float scale)
                {
                    return new Vector2
                    {
                        x = scale + 1,
                        y = scale + 2,
                    };
                }

                public static string Run()
                {
                    var v = new Vector2(10, 5);
                    v.x = 11;
                    v = new Vector2(12, 6);
                    var data = Make(2);
                    return $"x:{v.x} y:{v.y} data:{data.x}:{data.y}";
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "FlattenedStruct.cs");

        Assert.Contains("local v__x, v__y = 10, 5", lua);
        Assert.Contains("v__x = 11", lua);
        Assert.Contains("v__x, v__y = 12, 6", lua);
        Assert.Contains("return (scale + 1), (scale + 2)", lua);
        Assert.Contains("local data__x, data__y = SF__.MathDemo.Make(2)", lua);
        Assert.Contains("return SF__.StrConcat__(\"x:\", v__x, \" y:\", v__y, \" data:\", data__x, \":\", data__y)", lua);
        Assert.DoesNotContain("local v = SF__.Vector2.New", lua);
        Assert.DoesNotContain("-- Vector2", lua);
        Assert.DoesNotContain("SF__.Vector2 = SF__.Vector2 or {}", lua);
        Assert.DoesNotContain("function SF__.Vector2.__Init", lua);
        Assert.DoesNotContain("function SF__.Vector2.New", lua);
    }

    [Fact]
    public async Task Struct_typed_class_members_are_flattened()
    {
        var src = """
            public struct AbilityData
            {
                public float DamageScaling;
                public float ArtOfWarChance;
            }

            public class CrusaderStrike
            {
                private AbilityData _template;
                private static AbilityData staticProp;

                private void OnInspector()
                {
                    var scale = _template.DamageScaling * 15;
                    var chance = staticProp.ArtOfWarChance;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructMembers.cs");

        Assert.Contains("self._template__DamageScaling = 0", lua);
        Assert.Contains("self._template__ArtOfWarChance = 0", lua);
        Assert.Contains("SF__.CrusaderStrike.staticProp__DamageScaling = 0", lua);
        Assert.Contains("SF__.CrusaderStrike.staticProp__ArtOfWarChance = 0", lua);
        Assert.Contains("local scale = (self._template__DamageScaling * 15)", lua);
        Assert.Contains("local chance = SF__.CrusaderStrike.staticProp__ArtOfWarChance", lua);
        Assert.DoesNotContain("self._template = nil", lua);
        Assert.DoesNotContain("SF__.CrusaderStrike.staticProp = nil", lua);
    }

    [Fact]
    public async Task Struct_parameters_are_flattened()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;
            }

            public static class Sample
            {
                public static float Sum(Vector2 pos)
                {
                    return pos.x + pos.y;
                }

                public static float Run()
                {
                    return Sum(new Vector2 { x = 2, y = 3 });
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructParameter.cs");

        Assert.Contains("function SF__.Sample.Sum(pos__x, pos__y)", lua);
        Assert.Contains("return (pos__x + pos__y)", lua);
        Assert.Contains("return SF__.Sample.Sum(2, 3)", lua);
        Assert.DoesNotContain("pos.x", lua);
        Assert.DoesNotContain("pos.y", lua);
    }

    [Fact]
    public async Task Struct_methods_use_flattened_self_and_parameters()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public static Vector2 operator +(Vector2 left, Vector2 right)
                {
                    return new Vector2 { x = left.x + right.x, y = left.y + right.y };
                }

                public float MagnitudeSquared()
                {
                    return x * x + y * y;
                }
            }

            public static class Sample
            {
                public static float Run()
                {
                    var left = new Vector2 { x = 1, y = 2 };
                    var right = new Vector2 { x = 3, y = 4 };
                    var sum = left + right;
                    return sum.MagnitudeSquared();
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructMethods.cs");

        Assert.Contains("function SF__.Vector2.op_Addition(left__x, left__y, right__x, right__y)", lua);
        Assert.Contains("return (left__x + right__x), (left__y + right__y)", lua);
        Assert.Contains("function SF__.Vector2.MagnitudeSquared(self__x, self__y)", lua);
        Assert.Contains("(self__x * self__x)", lua);
        Assert.Contains("(self__y * self__y)", lua);
        Assert.Matches(@"local sum__x\d*, sum__y\d* = SF__\.Vector2\.op_Addition\(left__x\d*, left__y\d*, right__x\d*, right__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.MagnitudeSquared\(sum__x\d*, sum__y\d*\)", lua);
        Assert.DoesNotContain("function SF__.Vector2:MagnitudeSquared", lua);
    }

    [Fact]
    public async Task Computed_properties_indexers_delegates_and_events_emit_mvp_shapes()
    {
        var src = """
            using System;

            public class Bag
            {
                private int[] values = new[] { 1, 2 };
                public int Total => values[0] + values[1];

                public int this[int index]
                {
                    get { return values[index]; }
                    set { values[index] = value; }
                }
            }

            public static class Signals
            {
                public static event Action? Changed;

                public static int Double(int value)
                {
                    return value * 2;
                }

                public static int Run()
                {
                    Func<int, int> fn = Double;
                    var bag = new Bag();
                    bag[0] = fn(3);
                    return bag.Total + bag[0];
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "Phase7.cs");

        Assert.Contains("function SF__.Bag:get_Total()", lua);
        Assert.Contains("function SF__.Bag:get_Item(index)", lua);
        Assert.Matches(@"function SF__\.Bag:set_Item\(index\d*, value\d*\)", lua);
        Assert.Matches(@"self\.values\[\(index\d* \+ 1\)\] = value\d*", lua);
        Assert.Contains("SF__.Signals.Changed = nil", lua);
        Assert.Contains("local fn = SF__.Signals.Double", lua);
        Assert.Contains("bag:set_Item(0, fn(3))", lua);
        Assert.Contains("return (bag:get_Total() + bag:get_Item(0))", lua);
    }

    [Fact]
    public async Task Async_await_and_simple_iterators_emit_sync_mvp_shapes()
    {
        var src = """
            using System.Collections.Generic;
            using System.Threading.Tasks;

            public static class AsyncIterators
            {
                public static async Task<int> Load()
                {
                    return 5;
                }

                public static async Task<int> RunAsync()
                {
                    var value = await Load();
                    return value + 1;
                }

                public static IEnumerable<int> Values(int start)
                {
                    yield return start;
                    yield return start + 1;
                }

                public static int Sum()
                {
                    var total = 0;
                    foreach (var value in Values(2))
                    {
                        total += value;
                    }
                    return total;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "AsyncIterators.cs");

        Assert.Contains("function SF__.AsyncIterators.Load()", lua);
        Assert.Contains("return 5", lua);
        Assert.Contains("local value = SF__.AsyncIterators.Load()", lua);
        Assert.Contains("function SF__.AsyncIterators.Values(start)", lua);
        Assert.Contains("return {start, (start + 1)}", lua);
        Assert.Contains("local collection = SF__.AsyncIterators.Values(2)", lua);
    }

    [Fact]
    public async Task TableLiteral_class_lowers_constructor_call_to_table_literal()
    {
        var src = """
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                using System;
                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public bool TableLiteral;
                }
            }

            public static partial class JASS
            {
                public static object GetCastingUnit() => throw null!;
                public static int FourCC(string s) => throw null!;
            }

            [Lua(TableLiteral = true)]
            public class Shape
            {
                public object caster;
                public int id;
                public Shape(object caster, int id) { }
            }

            public static class Demo
            {
                public static void Post(object shape) { }

                public static void Main()
                {
                    Post(new Shape(JASS.GetCastingUnit(), JASS.FourCC("A001")));
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "TableLiteral.cs");

        Assert.Contains("{caster = GetCastingUnit(), id = FourCC(\"A001\")}", lua);
        Assert.DoesNotContain("SF__.Shape", lua);
        Assert.DoesNotContain("Shape.New", lua);
    }

    [Fact]
    public async Task TableLiteral_class_lowers_object_initializer_to_table_literal()
    {
        var src = """
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                using System;
                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public bool TableLiteral;
                }

                public class LuaObject
                {
                }
            }

            public static partial class JASS
            {
                public static object GetCastingUnit() => throw null!;
                public static int FourCC(string s) => throw null!;
            }

            [Lua(TableLiteral = true)]
            public class IRegisterSpellEvent : LuaObject
            {
                public int id;
                public object handler;
                public LuaObject ctx;
            }

            public static class Demo
            {
                public static void Emit(object ev) { }

                public static void Main()
                {
                    Emit(new IRegisterSpellEvent
                    {
                        id = JASS.FourCC("A001"),
                        handler = JASS.GetCastingUnit(),
                    });
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "TableLiteralObjectInitializer.cs");

        Assert.Contains("{id = FourCC(\"A001\"), handler = GetCastingUnit()}", lua);
        Assert.DoesNotContain("IRegisterSpellEvent.New", lua);
    }

    private static async Task<string> TranspileSourceAsync(string source, string fileName)
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, fileName);
        await File.WriteAllTextAsync(file, source);

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, "C# compile errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));

        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        return new LuaEmitter(TranspileOptions.DefaultRootTable).Emit(module);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SharpForge.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("SharpForge.sln not found above test base directory.");
    }
}
