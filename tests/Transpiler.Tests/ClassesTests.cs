using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class ClassesTests
{
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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("SF__.GameState.MaxPlayers = 12", lua);
        Assert.Contains("SF__.GameState.Mode = \"melee\"", lua);
        Assert.Contains("return SF__.GameState.MaxPlayers", lua);
        Assert.Contains("return SF__.GameState.Mode", lua);
        Assert.DoesNotContain("self.MaxPlayers", lua);
        Assert.DoesNotContain("self.Mode", lua);
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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("function SF__.GameObject.__Init__s(self, name)", lua);
        Assert.Contains("self.Name = name", lua);
        Assert.Matches(@"function SF__\.GameObject\.__Init__sx13\(self, name\d*, parent\)", lua);
        Assert.Matches(@"SF__\.GameObject\.__Init__s\(self, name\d*\)", lua);
        Assert.Contains("self.Parent = parent", lua);

        var secondCtorStart = lua.IndexOf("function SF__.GameObject.__Init__sx13", StringComparison.Ordinal);
        var secondCtorEnd = lua.IndexOf("function SF__.GameObject.New__sx13", StringComparison.Ordinal);
        var secondCtorBody = lua[secondCtorStart..secondCtorEnd];
        var chainedInitIndex = secondCtorBody.IndexOf("SF__.GameObject.__Init__s(self, name", StringComparison.Ordinal);
        var parentAssignmentIndex = secondCtorBody.IndexOf("self.Parent = parent", StringComparison.Ordinal);
        Assert.True(chainedInitIndex >= 0 && parentAssignmentIndex > chainedInitIndex, "expected this(...) init call before chained constructor body");

        Assert.DoesNotContain("self.Name = nil", secondCtorBody);
        Assert.DoesNotContain("self.Parent = nil", secondCtorBody);
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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("function SF__.Demo.Pick(first, second, third)", lua);
        Assert.Contains("if first == nil then first = 1 end", lua);
        Assert.Contains("if second == nil then second = 2 end", lua);
        Assert.Contains("if third == nil then third = 3 end", lua);
        Assert.Contains("SF__.Demo.Pick(nil, nil, 9)", lua);
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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("function SF__.GameObject:GetComponent(T)", lua);
        Assert.Contains("return self:GetComponent(SF__.Transform)", lua);
        Assert.Contains("local tComp = comp", lua);
        Assert.Contains("if SF__.TypeIs__(tComp, T) then", lua);
        Assert.Matches(@"local comp\d* = T\d*\.New\(\)", lua);
        Assert.DoesNotContain("unsupported expression: GenericName", lua);
        Assert.DoesNotContain("unsupported expression: IsPatternExpression", lua);
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

        var lua = await TranspilerTestHelper.TranspileAsync(src);

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
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: true,
            Verbose: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
    }
}
