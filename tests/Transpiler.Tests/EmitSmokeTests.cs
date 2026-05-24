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
    public async Task Const_fields_emit_as_static_fields()
    {
        var src = """
            public static class GameState
            {
                public const int MaxPlayers = 12;
                public const string Mode = "melee";

                public static int GetMaxPlayers()
                {
                    return MaxPlayers;
                }
            }

            public static class Demo
            {
                public static string GetMode()
                {
                    return GameState.Mode;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "ConstFields.cs");

        Assert.Contains("SF__.GameState.MaxPlayers = 12", lua);
        Assert.Contains("SF__.GameState.Mode = \"melee\"", lua);
        Assert.Contains("return SF__.GameState.MaxPlayers", lua);
        Assert.Contains("return SF__.GameState.Mode", lua);
        Assert.DoesNotContain("self.MaxPlayers", lua);
        Assert.DoesNotContain("self.Mode", lua);
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
    public async Task String_add_assignment_uses_nil_safe_polyfill()
    {
        var src = """
            public static class Strings
            {
                public static string Append(string text, string inspectorText)
                {
                    text += "\n" + inspectorText;
                    return text;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StringAddAssignment.cs");

        Assert.Contains("text = SF__.StrConcat__(text, \"\\n\", inspectorText)", lua);
        Assert.DoesNotContain("text = (text +", lua);
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
    public async Task String_concat_and_interpolation_use_object_tostring_override()
    {
        var src = """
            public class UnitName
            {
                private readonly string value;

                public UnitName(string value)
                {
                    this.value = value;
                }

                public override string ToString()
                {
                    return "unit:" + value;
                }
            }

            public static class Strings
            {
                public static string Plus(UnitName? name)
                {
                    return "[" + name + "]";
                }

                public static string Interp(UnitName? name)
                {
                    return $"<{name}>";
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "ObjectStringConcat.cs");

        Assert.Contains("local strPart = name", lua);
        Assert.Contains("if (strPart ~= nil) then", lua.Replace("\r\n", "\n"));
        Assert.Contains("return strPart:ToString()", lua);
        Assert.Contains("return SF__.StrConcat__(\"[\", (function()", lua);
        Assert.Contains("return SF__.StrConcat__(\"<\", (function()", lua);
    }

    [Fact]
    public async Task String_concat_and_interpolation_use_struct_tostring_override()
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

                public override string ToString()
                {
                    return $"({x},{y})";
                }
            }

            public static class Strings
            {
                public static string Plus(Vector2 value)
                {
                    return "[" + value + "]";
                }

                public static string Interp(Vector2 value)
                {
                    return $"<{value}>";
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructStringConcat.cs");

        Assert.Contains("function SF__.Vector2.ToString(self__x, self__y)", lua);
        Assert.Contains("return SF__.StrConcat__(\"[\", SF__.Vector2.ToString(value__x, value__y), \"]\")", lua);
        Assert.Contains("return SF__.StrConcat__(\"<\", SF__.Vector2.ToString(", lua);
        Assert.Contains(", \">\")", lua);
        Assert.DoesNotContain("value:ToString()", lua);
    }

    [Fact]
    public async Task String_interpolation_emits_exception_as_is_without_ToString()
    {
        var src = """
            public static class Demo
            {
                public static string Run()
                {
                    try
                    {
                        throw new System.Exception("boom");
                    }
                    catch (System.Exception ex)
                    {
                        return $"Error: {ex}";
                    }
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "ExceptionInterpolation.cs");

        Assert.Contains("return SF__.StrConcat__(\"Error: \", ex)", lua);
        Assert.DoesNotContain("ex:ToString()", lua);
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

        Assert.Contains("function SF__.Overloaded.New__0()", lua);
        Assert.Contains("function SF__.Overloaded.New__i(value)", lua);
        Assert.Contains("function SF__.Overloaded:Pick__0()", lua);
        Assert.Contains("function SF__.Overloaded:Pick__i(fallback)", lua);
        Assert.Contains("SF__.Overloaded.New__i(7)", lua);
        Assert.Contains("item:Pick__i(3)", lua);
    }

    [Fact]
    public async Task This_constructor_initializer_calls_chained_constructor_init()
    {
        var src = """
            public class GameObject
            {
                public string Name;
                public GameObject? Parent;

                public GameObject(string name)
                {
                    Name = name;
                }

                public GameObject(string name, GameObject parent) : this(name)
                {
                    Parent = parent;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "ConstructorChain.cs");

        Assert.Contains("function SF__.GameObject.__Init__s(self, name)", lua);
        Assert.Contains("self.Name = name", lua);
        Assert.Matches(@"function SF__\.GameObject\.__Init__sgameobject\(self, name\d*, parent\)", lua);
        Assert.Matches(@"SF__\.GameObject\.__Init__s\(self, name\d*\)", lua);
        Assert.Contains("self.Parent = parent", lua);

        var secondCtorStart = lua.IndexOf("function SF__.GameObject.__Init__sgameobject", StringComparison.Ordinal);
        var secondCtorEnd = lua.IndexOf("function SF__.GameObject.New__sgameobject", StringComparison.Ordinal);
        var secondCtorBody = lua[secondCtorStart..secondCtorEnd];
        var chainedInitIndex = secondCtorBody.IndexOf("SF__.GameObject.__Init__s(self, name", StringComparison.Ordinal);
        var parentAssignmentIndex = secondCtorBody.IndexOf("self.Parent = parent", StringComparison.Ordinal);
        Assert.True(chainedInitIndex >= 0 && parentAssignmentIndex > chainedInitIndex, "expected this(...) init call before chained constructor body");

        Assert.DoesNotContain("self.Name = nil", secondCtorBody);
        Assert.DoesNotContain("self.Parent = nil", secondCtorBody);
    }

    [Fact]
    public async Task Operator_overloads_get_signature_lua_names()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public Vector3(float x, float y, float z)
                {
                    this.x = x;
                    this.y = y;
                    this.z = z;
                }

                public static Vector3 operator +(Vector3 left, Vector3 right)
                {
                    return new Vector3(left.x + right.x, left.y + right.y, left.z + right.z);
                }

                public static Vector3 operator *(Vector3 v, float f)
                {
                    return new Vector3(v.x * f, v.y * f, v.z * f);
                }

                public static Vector3 operator *(float f, Vector3 v)
                {
                    return new Vector3(v.x * f, v.y * f, v.z * f);
                }
            }

            public static class Demo
            {
                public static Vector3 Run(Vector3 value)
                {
                    return 2f * value + value * 3f;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "OperatorOverloads.cs");

        Assert.Contains("function SF__.Vector3.op_Multiply__vector3f(v__x, v__y, v__z, f)", lua);
        Assert.Contains("function SF__.Vector3.op_Multiply__fvector3(", lua);
        Assert.Contains("SF__.Vector3.op_Multiply__fvector3(2,", lua);
        Assert.Contains("SF__.Vector3.op_Multiply__vector3f(value__x", lua);
    }

    [Fact]
    public async Task Optional_parameters_emit_nil_guards()
    {
        var src = """
            public static class Demo
            {
                public static int Pick(int first = 1, int second = 2, int third = 3)
                {
                    return first + second + third;
                }

                public static int Run()
                {
                    return Pick(third: 9);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "OptionalParameters.cs");

        Assert.Contains("function SF__.Demo.Pick(first, second, third)", lua);
        Assert.Contains("if first == nil then first = 1 end", lua);
        Assert.Contains("if second == nil then second = 2 end", lua);
        Assert.Contains("if third == nil then third = 3 end", lua);
        Assert.Contains("SF__.Demo.Pick(nil, nil, 9)", lua);
    }

    [Fact]
    public async Task Optional_enum_parameter_on_struct_method_emits_nil_guard()
    {
        var src = """
            public enum UnitVec3Mode
            {
                ForceFlying,
                ForceGround,
                Auto,
            }

            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public void UnitMoveTo(int unit, UnitVec3Mode mode = UnitVec3Mode.Auto)
                {
                    if (mode == UnitVec3Mode.ForceFlying)
                    {
                        x = y + z;
                    }
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "OptionalEnumStructMethod.cs");

        Assert.Contains("function SF__.Vector3.UnitMoveTo(self__x, self__y, self__z, unit, mode)", lua);
        Assert.Contains("if mode == nil then mode = SF__.UnitVec3Mode.Auto end", lua);
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

        var lua = await TranspileSourceAsync(src, "RuntimeTypeName.cs");

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

        var lua = await TranspileSourceAsync(src, "PatternChecks.cs");

        Assert.Contains("return (not (value == nil))", lua);
        Assert.Matches(@"return SF__\.TypeIs__\(value\d*, SF__\.Game\.Hero\)", lua);
        Assert.DoesNotContain("unsupported expression: SuppressNullableWarningExpression", lua);
        Assert.DoesNotContain("unsupported expression: IsPatternExpression", lua);
    }

    [Fact]
    public async Task Generic_method_type_parameters_emit_runtime_type_arguments()
    {
        var src = """
            public class Component
            {
            }

            public class Transform : Component
            {
            }

            public class GameObject
            {
                private Component _component = new Transform();

                public Transform Transform => GetComponent<Transform>()!;

                public T? GetComponent<T>() where T : Component
                {
                    var comp = _component;
                    if (comp is T tComp)
                    {
                        return tComp;
                    }

                    return null;
                }

                public T AddComponent<T>() where T : Component, new()
                {
                    var comp = new T();
                    _component = comp;
                    return comp;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "GenericComponents.cs");

        Assert.Contains("function SF__.GameObject:GetComponent(T)", lua);
        Assert.Contains("return self:GetComponent(SF__.Transform)", lua);
        Assert.Contains("local tComp = comp", lua);
        Assert.Contains("if SF__.TypeIs__(tComp, T) then", lua);
        Assert.Matches(@"local comp\d* = T\d*\.New\(\)", lua);
        Assert.DoesNotContain("unsupported expression: GenericName", lua);
        Assert.DoesNotContain("unsupported expression: IsPatternExpression", lua);
    }

    [Fact]
    public async Task Component_scene_patterns_from_project_emit_lua()
    {
        var sources = new Dictionary<string, string>
        {
            ["Component.cs"] = """
                public class Component
                {
                    public GameObject gameObject { get; internal set; } = null!;

                    public virtual string GetInspectorText()
                    {
                        return string.Empty;
                    }
                }
                """,
            ["Transform.cs"] = """
                public class Transform : Component
                {
                }
                """,
            ["GameObject.cs"] = """
                public class GameObject
                {
                    public string name { get; private set; }
                    public Transform transform { get; private set; }

                    public GameObject(string name)
                    {
                        this.name = name;
                        transform = AddComponent<Transform>();
                    }

                    public T? GetComponent<T>() where T : Component
                    {
                        return null;
                    }

                    public T AddComponent<T>() where T : Component, new()
                    {
                        var comp = new T
                        {
                            gameObject = this
                        };
                        return comp;
                    }
                }
                """,
            ["Scene.cs"] = """
                public class Scene
                {
                    private static Scene? _instance;
                    public static Scene Instance => _instance ??= new Scene();
                }
                """,
        };

        var lua = await TranspileSourcesAsync(sources);

        Assert.Contains("gameObject = nil", lua);
        Assert.Contains("return \"\"", lua);
        Assert.Contains("self:AddComponent(SF__.Transform)", lua);
        Assert.Contains("function SF__.GameObject:GetComponent(T)", lua);
        Assert.Matches(@"local obj\d* = T\d*\.New\(\)", lua);
        Assert.Matches(@"obj\d*\.gameObject = self", lua);
        Assert.Matches(@"return obj\d*", lua);
        Assert.Contains("if SF__.Scene._instance ~= nil then", lua);
        Assert.Contains("SF__.Scene._instance = SF__.Scene.New()", lua);
        Assert.DoesNotContain("unsupported expression: SuppressNullableWarningExpression", lua);
        Assert.DoesNotContain("unsupported expression: PredefinedType", lua);
        Assert.DoesNotContain("unsupported expression: GenericName", lua);
        Assert.DoesNotContain("unsupported expression: IdentifierName", lua);
        Assert.DoesNotContain("unsupported expression: IsPatternExpression", lua);
        Assert.DoesNotContain("unsupported expression: CoalesceAssignmentExpression", lua);
    }

    [Fact]
    public async Task Object_initializer_assignments_run_after_creation_and_return_object()
    {
        var src = """
            public class Component
            {
                public GameObject gameObject;
            }

            public class Transform : Component
            {
            }

            public class GameObject
            {
                public Transform AddTransform()
                {
                    return new Transform
                    {
                        gameObject = this
                    };
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "ObjectInitializer.cs");

        Assert.Contains("local obj = SF__.Transform.New()", lua);
        Assert.Contains("obj.gameObject = self", lua);
        Assert.Contains("return obj", lua);
        Assert.DoesNotContain("unsupported object initializer", lua);
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
            LibraryFolders: new[] { TranspileOptions.DefaultLibraryFolder },
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
    public async Task Struct_constructor_field_mapping_uses_constructor_syntax_tree_model()
    {
        var lua = await TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["Vector2.cs"] = """
                public struct Vector2
                {
                    public int X;
                    public int Y;

                    public Vector2(int x, int y)
                    {
                        X = x;
                        Y = y;
                    }
                }
                """,
            ["Scale.cs"] = """
                public struct Scale
                {
                    public int Value;

                    public Scale(int value)
                    {
                        Value = value;
                    }

                    public static Scale operator +(Scale left, Scale right)
                    {
                        var pair = new Vector2(left.Value, right.Value);
                        return new Scale(pair.X + pair.Y);
                    }
                }
                """,
        });

        Assert.Contains("function SF__.Scale.op_Addition(left__Value, right__Value)", lua);
        Assert.Matches(@"local pair__X\d*, pair__Y\d* = left__Value, right__Value", lua);
        Assert.Contains("return (pair__X + pair__Y)", lua);
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

                public static float Use(Vector2 value)
                {
                    return value.x + value.y;
                }

                public static string Run()
                {
                    var v = new Vector2(10, 5);
                    v.x = 11;
                    v = new Vector2(12, 6);
                    var data = Make(2);
                    return $"x:{v.x} y:{v.y} data:{data.x}:{data.y}:{Use(data)}";
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "FlattenedStruct.cs");

        Assert.Contains("local v__x, v__y = 10, 5", lua);
        Assert.Contains("v__x = 11", lua);
        Assert.Contains("v__x, v__y = 12, 6", lua);
        Assert.Contains("return (scale + 1), (scale + 2)", lua);
        Assert.Contains("function SF__.MathDemo.Use(value__x, value__y)", lua);
        Assert.Contains("return (value__x + value__y)", lua);
        Assert.Contains("local data__x, data__y = SF__.MathDemo.Make(2)", lua);
        Assert.Contains("return SF__.StrConcat__(\"x:\", v__x, \" y:\", v__y, \" data:\", data__x, \":\", data__y, \":\", SF__.MathDemo.Use(data__x, data__y))", lua);
        Assert.DoesNotContain("local v = SF__.Vector2.New", lua);
        Assert.DoesNotContain("-- Vector2", lua);
        Assert.DoesNotContain("SF__.Vector2 = SF__.Vector2 or {}", lua);
        Assert.DoesNotContain("function SF__.Vector2.__Init", lua);
        Assert.DoesNotContain("function SF__.Vector2.New", lua);
    }

    [Fact]
    public async Task Struct_local_used_in_loop_and_return_is_flattened()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public Vector3(float x, float y, float z)
                {
                    this.x = x;
                    this.y = y;
                    this.z = z;
                }

                public static Vector3 operator +(Vector3 a, Vector3 b)
                {
                    return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
                }
            }

            public static class TransformDemo
            {
                public static Vector3 Compute(Vector3 localPos, Vector3 offset, int count)
                {
                    var pos = localPos;
                    var i = 0;
                    while (i < count)
                    {
                        pos = pos + offset;
                        i++;
                    }
                    return pos;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructReturnFlatten.cs");

        Assert.Contains("local pos__x, pos__y, pos__z = localPos__x, localPos__y, localPos__z", lua);
        Assert.Contains("pos__x, pos__y, pos__z = SF__.Vector3.op_Addition(pos__x, pos__y, pos__z, offset__x, offset__y, offset__z)", lua);
        Assert.Contains("return pos__x, pos__y, pos__z", lua);
        Assert.DoesNotContain("local pos = localPos", lua);
        Assert.DoesNotContain("return pos.x", lua);
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
    public async Task Assignments_to_flattened_struct_members_expand_to_field_assignments()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public Vector3(float x, float y, float z)
                {
                    this.x = x;
                    this.y = y;
                    this.z = z;
                }
            }

            public struct Quaternion
            {
                public float x;
                public float y;
                public float z;
                public float w;

                public static Quaternion Euler(float x, float y, float z)
                {
                    return new Quaternion { x = x, y = y, z = z, w = 1f };
                }

                public static Quaternion operator *(Quaternion left, Quaternion right)
                {
                    return new Quaternion { x = left.x + right.x, y = left.y + right.y, z = left.z + right.z, w = left.w + right.w };
                }
            }

            public class Transform
            {
                public Vector3 position;
                public Quaternion rotation;
                public Vector3 localScale;

                public Transform()
                {
                    position = new Vector3(0f, 0f, 0f);
                    rotation = Quaternion.Euler(0f, 0f, 0f);
                    localScale = new Vector3(1f, 1f, 1f);
                }
            }

            public class GameObject
            {
                public Transform transform = new Transform();
            }

            public static class Motion
            {
                public static void Spin(GameObject boltMis)
                {
                    var trs = boltMis.transform;
                    var rot = Quaternion.Euler(1f, 0f, 0f);
                    trs.rotation = rot * trs.rotation;
                }
            }

            public class ShadowedTransform
            {
                public Quaternion rotation;

                public void Reset()
                {
                    var rotation = Quaternion.Euler(1f, 0f, 0f);
                    rotation = Quaternion.Euler(2f, 0f, 0f);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "TransformStructMembers.cs");

        Assert.Contains("self.position__x, self.position__y, self.position__z = 0, 0, 0", lua);
        Assert.Contains("self.rotation__x, self.rotation__y, self.rotation__z, self.rotation__w = SF__.Quaternion.Euler(0, 0, 0)", lua);
        Assert.Contains("self.localScale__x, self.localScale__y, self.localScale__z = 1, 1, 1", lua);
        Assert.Contains("trs.rotation__x, trs.rotation__y, trs.rotation__z, trs.rotation__w = SF__.Quaternion.op_Multiply(rot__x, rot__y, rot__z, rot__w, trs.rotation__x, trs.rotation__y, trs.rotation__z, trs.rotation__w)", lua);
        Assert.Contains("local rotation__x, rotation__y, rotation__z, rotation__w = SF__.Quaternion.Euler(1, 0, 0)", lua);
        Assert.Contains("rotation__x, rotation__y, rotation__z, rotation__w = SF__.Quaternion.Euler(2, 0, 0)", lua);
        Assert.DoesNotContain("self.position = {", lua);
        Assert.DoesNotContain("self.rotation = SF__.Quaternion.Euler", lua);
        Assert.DoesNotContain("self.localScale = {", lua);
        Assert.DoesNotContain("self.rotation__x, self.rotation__y, self.rotation__z, self.rotation__w = SF__.Quaternion.op_Multiply(rot__x", lua);
        Assert.DoesNotContain("self.rotation__x, self.rotation__y, self.rotation__z, self.rotation__w = SF__.Quaternion.Euler(2", lua);
    }

    [Fact]
    public async Task Nested_flattened_struct_members_expand_across_files_regardless_of_type_order()
    {
        var lua = await TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["AttachEffectComponent.cs"] = """
                public class AttachEffectComponent
                {
                    public GameObject gameObject = new GameObject();

                    public void Update()
                    {
                        var globalPos = gameObject.transform.position;
                        var globalRot = gameObject.transform.rotation;
                        var globalScale = gameObject.transform.localScale;
                        var parent = gameObject.transform.parent;
                        while (parent != null)
                        {
                            globalPos = parent.position + parent.rotation * Vector3.Scale(parent.localScale, globalPos);
                            globalRot = parent.rotation * globalRot;
                            globalScale = Vector3.Scale(parent.localScale, globalScale);
                            parent = parent.parent;
                        }
                    }
                }
                """,
            ["Transform.cs"] = """
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;

                    public static Vector3 Scale(Vector3 left, Vector3 right)
                    {
                        return new Vector3
                        {
                            x = left.x * right.x,
                            y = left.y * right.y,
                            z = left.z * right.z,
                        };
                    }

                    public static Vector3 operator +(Vector3 left, Vector3 right)
                    {
                        return new Vector3
                        {
                            x = left.x + right.x,
                            y = left.y + right.y,
                            z = left.z + right.z,
                        };
                    }
                }

                public struct Quaternion
                {
                    public float x;
                    public float y;
                    public float z;
                    public float w;

                    public static Vector3 operator *(Quaternion left, Vector3 right)
                    {
                        return right;
                    }

                    public static Quaternion operator *(Quaternion left, Quaternion right)
                    {
                        return left;
                    }
                }

                public class Transform
                {
                    public Vector3 position;
                    public Quaternion rotation;
                    public Vector3 localScale;
                    public Transform? parent;
                }

                public class GameObject
                {
                    public Transform transform = new Transform();
                }
                """,
        });

        Assert.Contains("local globalPos__x, globalPos__y, globalPos__z = self.gameObject.transform.position__x, self.gameObject.transform.position__y, self.gameObject.transform.position__z", lua);
        Assert.Contains("local globalRot__x, globalRot__y, globalRot__z, globalRot__w = self.gameObject.transform.rotation__x, self.gameObject.transform.rotation__y, self.gameObject.transform.rotation__z, self.gameObject.transform.rotation__w", lua);
        Assert.Contains("local globalScale__x, globalScale__y, globalScale__z = self.gameObject.transform.localScale__x, self.gameObject.transform.localScale__y, self.gameObject.transform.localScale__z", lua);
        Assert.Contains("globalPos__x, globalPos__y, globalPos__z = SF__.Vector3.op_Addition(parent.position__x, parent.position__y, parent.position__z, SF__.Quaternion.op_Multiply__quaternionvector3(parent.rotation__x, parent.rotation__y, parent.rotation__z, parent.rotation__w, SF__.Vector3.Scale(parent.localScale__x, parent.localScale__y, parent.localScale__z, globalPos__x, globalPos__y, globalPos__z)))", lua);
        Assert.DoesNotContain(".position.x", lua);
        Assert.DoesNotContain(".rotation.x", lua);
        Assert.DoesNotContain(".localScale.x", lua);
    }

    [Fact]
    public async Task Flattened_struct_locals_stay_expanded_when_stringified()
    {
        var lua = await TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["AttachEffectComponent.cs"] = """
                public class AttachEffectComponent
                {
                    public GameObject gameObject = new GameObject();

                    private static void Log(string text)
                    {
                    }

                    public void Update()
                    {
                        var globalPos = gameObject.transform.position;
                        var globalRot = gameObject.transform.rotation;
                        var globalScale = gameObject.transform.localScale;
                        var parent = gameObject.transform.parent;
                        while (parent != null)
                        {
                            Log($"parent.localScale:{parent.localScale}");
                            Log($"globalPos:{globalPos}");
                            Log($"parent.rotation:{parent.rotation}");
                            globalRot = parent.rotation * globalRot;
                            globalScale = Vector3.Scale(parent.localScale, globalScale);
                            parent = parent.parent;
                        }
                    }
                }
                """,
            ["Transform.cs"] = """
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;

                    public override string ToString()
                    {
                        return $"({x},{y},{z})";
                    }

                    public static Vector3 Scale(Vector3 left, Vector3 right)
                    {
                        return new Vector3
                        {
                            x = left.x * right.x,
                            y = left.y * right.y,
                            z = left.z * right.z,
                        };
                    }
                }

                public struct Quaternion
                {
                    public float x;
                    public float y;
                    public float z;
                    public float w;

                    public override string ToString()
                    {
                        return $"({x},{y},{z},{w})";
                    }

                    public static Quaternion operator *(Quaternion left, Quaternion right)
                    {
                        return left;
                    }
                }

                public class Transform
                {
                    public Vector3 position;
                    public Quaternion rotation;
                    public Vector3 localScale;
                    public Transform? parent;
                }

                public class GameObject
                {
                    public Transform transform = new Transform();
                }
                """,
        });

        Assert.Contains("local globalPos__x, globalPos__y, globalPos__z = self.gameObject.transform.position__x, self.gameObject.transform.position__y, self.gameObject.transform.position__z", lua);
        Assert.Contains("SF__.AttachEffectComponent.Log(SF__.StrConcat__(\"globalPos:\", SF__.Vector3.ToString(globalPos__x, globalPos__y, globalPos__z)))", lua);
        Assert.DoesNotContain("local globalPos = self.gameObject.transform.position", lua);
        Assert.DoesNotContain("globalPos.x", lua);
    }

    [Fact]
    public async Task Flattened_struct_locals_stay_expanded_when_assigned_to_struct_members()
    {
        var lua = await TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["DivineToll.cs"] = """
                public static class DivineToll
                {
                    public static void Start(SpellData data)
                    {
                        var pos = Vector3.FromUnit(data.caster);
                        var bolt = new GameObject();
                        bolt.transform.position = pos;
                        Spawn(pos.x, pos.y);
                    }

                    public static void Spawn(float x, float y)
                    {
                    }
                }

                public class SpellData
                {
                    public int caster;
                }
                """,
            ["Transform.cs"] = """
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;

                    public static Vector3 FromUnit(int unit)
                    {
                        return new Vector3
                        {
                            x = 1,
                            y = 2,
                            z = 3,
                        };
                    }
                }

                public class Transform
                {
                    public Vector3 position;
                }

                public class GameObject
                {
                    public Transform transform = new Transform();
                }
                """,
        });

        Assert.Contains("local pos__x, pos__y, pos__z = SF__.Vector3.FromUnit(data.caster)", lua);
        Assert.Contains(".transform.position__x", lua);
        Assert.Contains(".transform.position__y", lua);
        Assert.Contains(".transform.position__z", lua);
        Assert.Contains("= pos__x, pos__y, pos__z", lua);
        Assert.Contains("SF__.DivineToll.Spawn(pos__x, pos__y)", lua);
        Assert.DoesNotContain("local pos = SF__.Vector3.FromUnit", lua);
        Assert.DoesNotContain("position__x, bolt.transform.position__y, bolt.transform.position__z = pos.x, pos.y, pos.z", lua);
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
    public async Task Nested_struct_operator_calls_spill_non_tail_multi_return_arguments()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public static Vector2 operator +(Vector2 left, Vector2 right)
                {
                    return new Vector2
                    {
                        x = left.x + right.x,
                        y = left.y + right.y,
                    };
                }
            }

            public static class Sample
            {
                public static Vector2 Run(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
                {
                    return (a + b) + (c + d);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "NestedStructOperatorCalls.cs");

        Assert.Contains("function SF__.Vector2.op_Addition(left__x, left__y, right__x, right__y)", lua);
        Assert.Matches(@"local left__x\d*, left__y\d* = SF__\.Vector2\.op_Addition\(a__x, a__y, b__x, b__y\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.op_Addition\(left__x\d*, left__y\d*, SF__\.Vector2\.op_Addition\(c__x, c__y, d__x, d__y\)\)", lua);
    }

    [Fact]
    public async Task Struct_properties_use_flattened_self_and_receiver_values()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public static Vector2 operator -(Vector2 left, Vector2 right)
                {
                    return new Vector2 { x = left.x - right.x, y = left.y - right.y };
                }

                public float Magnitude => SqrMagnitude + 1;
                public float SqrMagnitude => x * x + y * y;
            }

            public static class Sample
            {
                public static float Run()
                {
                    var left = new Vector2 { x = 6, y = 8 };
                    var right = new Vector2 { x = 1, y = 2 };
                    return (left - right).Magnitude;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructProperties.cs");

        Assert.Matches(@"function SF__\.Vector2\.get_Magnitude\(self__x\d*, self__y\d*\)", lua);
        Assert.Matches(@"function SF__\.Vector2\.get_SqrMagnitude\(self__x\d*, self__y\d*\)", lua);
        Assert.Matches(@"return \(SF__\.Vector2\.get_SqrMagnitude\(self__x\d*, self__y\d*\) \+ 1\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.get_Magnitude\(SF__\.Vector2\.op_Subtraction\(left__x\d*, left__y\d*, right__x\d*, right__y\d*\)\)", lua);
        Assert.DoesNotContain("function SF__.Vector2:get_Magnitude", lua);
        Assert.DoesNotContain(":get_Magnitude()", lua);
        Assert.DoesNotContain(":get_SqrMagnitude()", lua);
    }

    [Fact]
    public async Task Struct_returning_property_argument_is_not_treated_as_table_fields()
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

                public static Vector2 operator *(Vector2 value, float scale)
                {
                    return new Vector2(value.x * scale, value.y * scale);
                }

                public static Vector2 operator *(float scale, Vector2 value)
                {
                    return new Vector2(value.x * scale, value.y * scale);
                }

                public Vector2 Normalized => new(0, 0);

                public Vector2 ClampMagnitude(float mag)
                {
                    return Normalized * mag;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructPropertyArgument.cs");

        Assert.Matches(@"function SF__\.Vector2\.ClampMagnitude\(self__x\d*, self__y\d*, mag\)", lua);
        Assert.Matches(@"local value__x\d*, value__y\d* = SF__\.Vector2\.get_Normalized\(self__x\d*, self__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.op_Multiply__vector2f\(value__x\d*, value__y\d*, mag\)", lua);
        Assert.DoesNotMatch(@"get_Normalized\(self__x\d*, self__y\d*\)\.x", lua);
        Assert.DoesNotMatch(@"get_Normalized\(self__x\d*, self__y\d*\)\.y", lua);
    }

    [Fact]
    public async Task Struct_returning_parameter_stays_flattened()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public static Vector3 operator -(Vector3 left, Vector3 right)
                {
                    return new Vector3 { x = left.x - right.x, y = left.y - right.y, z = left.z - right.z };
                }

                public static Vector3 operator +(Vector3 left, Vector3 right)
                {
                    return new Vector3 { x = left.x + right.x, y = left.y + right.y, z = left.z + right.z };
                }

                public static Vector3 operator /(Vector3 value, float scale)
                {
                    return new Vector3 { x = value.x / scale, y = value.y / scale, z = value.z / scale };
                }

                public float magnitude => x + y + z;

                public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
                {
                    var toVector = target - current;
                    var dist = toVector.magnitude;
                    if (dist <= maxDistanceDelta || dist == 0f)
                    {
                        return target;
                    }
                    return current + toVector / (dist / maxDistanceDelta);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructReturnParameter.cs");

        Assert.Matches(@"function SF__\.Vector3\.MoveTowards\(current__x\d*, current__y\d*, current__z\d*, target__x\d*, target__y\d*, target__z\d*, maxDistanceDelta\)", lua);
        Assert.Matches(@"return target__x\d*, target__y\d*, target__z\d*", lua);
        Assert.DoesNotContain("return {x = target__x", lua);
    }

    [Fact]
    public async Task Struct_ternary_assignment_spills_branches_to_flattened_fields()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public static Vector3 FromUnit(int unit)
                {
                    return new Vector3 { x = unit, y = unit + 1, z = unit + 2 };
                }
            }

            public class MoveTowardsComponent
            {
                public Vector3 pointTarget;
                private Vector3 _targetPosition;

                public void Update(bool useUnit, int unitTarget)
                {
                    _targetPosition = useUnit ? Vector3.FromUnit(unitTarget) : pointTarget;
                    var selected = useUnit ? Vector3.FromUnit(unitTarget) : pointTarget;
                    _targetPosition = selected;
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructTernaryAssignment.cs");

        Assert.Contains("if useUnit then", lua);
        Assert.Contains("local ternary__x, ternary__y, ternary__z", lua);
        Assert.Matches(@"local ternary__x, ternary__y, ternary__z\s+do\s+if useUnit then", lua);
        Assert.Contains("ternary__x, ternary__y, ternary__z = SF__.Vector3.FromUnit(unitTarget)", lua);
        Assert.Contains("ternary__x, ternary__y, ternary__z = self.pointTarget__x, self.pointTarget__y, self.pointTarget__z", lua);
        Assert.Contains("self._targetPosition__x, self._targetPosition__y, self._targetPosition__z = ternary__x, ternary__y, ternary__z", lua);
        Assert.Contains("local selected__x, selected__y, selected__z", lua);
        Assert.Matches(@"local selected__x, selected__y, selected__z\s+do\s+if useUnit then", lua);
        Assert.Contains("selected__x, selected__y, selected__z = SF__.Vector3.FromUnit(unitTarget)", lua);
        Assert.Contains("selected__x, selected__y, selected__z = self.pointTarget__x, self.pointTarget__y, self.pointTarget__z", lua);
        Assert.DoesNotContain("SF__.Ternary__(useUnit", lua);
    }

    [Fact]
    public async Task Target_typed_struct_creation_in_expression_bodied_returns_uses_multi_return()
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

                public static Vector2 Zero => new(0, 0);
                public static Vector2 One { get => new(1, 1); }
                public static Vector2 Make() => new(2, 3);
            }
            """;

        var lua = await TranspileSourceAsync(src, "StructExpressionBodiedReturns.cs");

        Assert.Contains("function SF__.Vector2.get_Zero()", lua);
        Assert.Contains("function SF__.Vector2.get_One()", lua);
        Assert.Contains("function SF__.Vector2.Make()", lua);
        Assert.Contains("return 0, 0", lua);
        Assert.Contains("return 1, 1", lua);
        Assert.Contains("return 2, 3", lua);
        Assert.DoesNotContain("return {x = 0, y = 0}", lua);
        Assert.DoesNotContain("return {x = 1, y = 1}", lua);
        Assert.DoesNotContain("return {x = 2, y = 3}", lua);
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
    public async Task Bound_instance_method_groups_passed_to_action_parameters_lower_through_receiver_capturing_lambdas()
    {
        var src = """
            using System;

            public class unit { }

            public class UnitStore
            {
                public void Add(unit value) { }
                public void Clear() { }
            }

            public static partial class JASS
            {
                public static void ExTriggerRegisterNewUnit(Action<unit> callback) => throw null!;
                public static void ExTriggerRegisterReady(Action callback) => throw null!;
            }

            public class Program
            {
                private readonly UnitStore _units = new();

                public void Init()
                {
                    JASS.ExTriggerRegisterNewUnit(_units.Add);
                    JASS.ExTriggerRegisterReady(_units.Clear);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "BoundMethodGroupAction.cs");

        Assert.Contains("ExTriggerRegisterNewUnit(function", lua);
        Assert.Matches(@"function\((\w+)\)\s+self\._units:Add\(\1\)\s+end", lua);
        Assert.Contains("ExTriggerRegisterReady(function()", lua);
        Assert.Matches(@"function\(\)\s+self\._units:Clear\(\)\s+end", lua);
        Assert.DoesNotContain("ExTriggerRegisterNewUnit(self._units.Add)", lua);
    }

    [Fact]
    public async Task Conditional_delegate_invoke_statements_lower_to_nil_guarded_temp_calls()
    {
        var src = """
            using System;

            public static class Signals
            {
                public static void Raise(Action<int>? handler, int value)
                {
                    handler?.Invoke(value);
                }
            }
            """;

        var lua = await TranspileSourceAsync(src, "ConditionalDelegateInvoke.cs");

        Assert.Contains("local delegate = handler", lua);
        Assert.Contains("if (delegate ~= nil) then", lua);
        Assert.Contains("delegate(value)", lua);
        Assert.DoesNotContain("unsupported expression: ConditionalAccessExpression", lua);
        Assert.DoesNotContain("delegate.Invoke", lua);
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
        => await TranspileSourcesAsync(new Dictionary<string, string> { [fileName] = source });

    private static async Task<string> TranspileSourcesAsync(IReadOnlyDictionary<string, string> sources)
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var files = new List<FileInfo>(sources.Count);
        foreach (var (fileName, source) in sources)
        {
            var file = Path.Combine(dir.FullName, fileName);
            await File.WriteAllTextAsync(file, source);
            files.Add(new FileInfo(file));
        }

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(files, CancellationToken.None);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, "C# compile errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));

        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        return new LuaEmitter(TranspileOptions.DefaultRootTable).Emit(module);
    }
}
