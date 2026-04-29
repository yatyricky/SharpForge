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
    public async Task Samples_transpile_jass_calls_without_emitting_extern_host()
    {
        var sampleDir = Path.Combine(FindRepoRoot(), "samples");
        var sourceFiles = Directory.GetFiles(sampleDir, "*.cs", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToArray();

        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(sourceFiles, CancellationToken.None);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, "C# compile errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));

        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        var lua = new LuaEmitter(TranspileOptions.DefaultRootTable).Emit(module);

        Assert.Contains("SF__.Game.Hero.New(\"Arthur\", 100)", lua);
        Assert.Contains("hero:LevelUp()", lua);
        Assert.Contains("hero:ToString()", lua);
        Assert.Contains("BJDebugMsg(hero:ToString())", lua);
        Assert.DoesNotContain("unsupported expr: ObjectCreationExpression", lua);
        Assert.DoesNotContain("SF__JASSGEN", lua);
        Assert.DoesNotContain("-- handle", lua);
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
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
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
    public async Task Unsupported_syntax_returns_pipeline_error()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Unsupported.cs"),
            "public static class Unsupported { public static void F() { switch (1) { case 1: break; } } }");

        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: new FileInfo(Path.Combine(dir.FullName, "out.lua")),
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
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
        Assert.Contains("function SF__.Game.Hero.__Init(self, hp)", lua);
        Assert.Contains("SF__.Game.Unit.__Init(self, hp)", lua);
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

        Assert.Contains("function SF__.__is(obj, target)", lua);
        Assert.Contains("function SF__.__as(obj, target)", lua);
        Assert.Contains("-- Game.INamed", lua);
        Assert.Contains("SF__.Game.Unit.__sf_interfaces = {[SF__.Game.INamed] = true}", lua);
        Assert.Contains("self.__sf_type = SF__.Game.Hero", lua);
        Assert.Contains("return SF__.__is(value, SF__.Game.INamed)", lua);
        Assert.Contains("local named = SF__.__as(value, SF__.Game.INamed)", lua);
        Assert.Contains("return SF__.__is(hero, SF__.Game.INamed)", lua);
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
        Assert.Contains("local __sf_collection0 = values", lua);
        Assert.Contains("for __sf_i0 = 1, #__sf_collection0 do", lua);
        Assert.Contains("local value = __sf_collection0[__sf_i0]", lua);
        Assert.Matches(@"values\[\(?\s*0\s*\+\s*1\s*\)?\]", lua);
        Assert.Contains("#values", lua);
        Assert.Contains("local values = {4, 5}", lua);
        Assert.Contains("table.insert(values, 6)", lua);
        Assert.Matches(@"values\[\(?\s*1\s*\+\s*1\s*\)?\]", lua);
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
        Assert.Contains("function SF__.Vector2.__Init(self, x, y)", lua);
        Assert.Contains("self.X = 0", lua);
        Assert.Contains("self.Y = 0", lua);
        Assert.Contains("self.X = x", lua);
        Assert.Contains("self.Y = y", lua);
        Assert.Contains("function SF__.Vector2.op_Addition(a, b)", lua);
        Assert.Contains("SF__.Vector2.New((a.X + b.X), (a.Y + b.Y))", lua);
        Assert.Contains("local c = SF__.Vector2.op_Addition(a, b)", lua);
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
        Assert.Contains("function SF__.Bag:set_Item(index, value)", lua);
        Assert.Contains("values[(index + 1)] = value", lua);
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
        Assert.Contains("local __sf_collection0 = SF__.AsyncIterators.Values(2)", lua);
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
