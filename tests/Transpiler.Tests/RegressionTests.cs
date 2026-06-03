using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

/// <summary>
/// Regression tests for specific bugs. Each test reproduces a bug that was fixed
/// and verifies the fix remains in place.
/// </summary>
public class RegressionTests
{
    [Fact]
    public async Task LuaRequire_emitted_before_class_calls()
    {
        // Bug: require("Lib.class") was emitted after class() calls, causing
        // "attempt to call a nil value (global 'class')" at runtime.
        // Fix: emit all LuaRequires at the top of the output.
        var src = """
            using SFLib.Interop;

            [Lua(Require = "Lib.class")]
            public class Base { }

            [Lua(Class = "MyClass")]
            public class MyClass : Base { }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);
        Assert.Equal(0, exitCode);

        var requirePos = lua.IndexOf("require(\"Lib.class\")", StringComparison.Ordinal);
        var classPos = lua.IndexOf("class(", StringComparison.Ordinal);
        Assert.True(requirePos >= 0, "require not found");
        Assert.True(classPos >= 0, "class() not found");
        Assert.True(requirePos < classPos, "require must come before class()");
    }

    [Fact]
    public async Task Nested_type_parent_emitted_before_child()
    {
        // Bug: nested types (e.g., InspectorSystem.HierarchyRow) were emitted before
        // their parent, causing "table index is nil" when accessing the parent path.
        // Fix: PlaceWithParents ensures parent types are emitted first.
        var src = """
            using SFLib.Interop;

            [Lua(Class = "Outer")]
            public class Outer
            {
                public class Inner { }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);
        Assert.Equal(0, exitCode);

        var outerPos = lua.IndexOf("SF__.Outer = SF__.Outer or class(", StringComparison.Ordinal);
        var innerPos = lua.IndexOf("SF__.Outer.Inner = SF__.Outer.Inner or {}", StringComparison.Ordinal);
        Assert.True(outerPos >= 0, "Outer class() not found");
        Assert.True(innerPos >= 0, "Inner {} not found");
        Assert.True(outerPos < innerPos, "parent class() must come before nested {}");
    }

    [Fact]
    public async Task Indexer_parameter_names_are_consistent()
    {
        // Bug: indexer get_Item parameter got suffix 433 in signature but 434 in body,
        // because GetDeclaredSymbol returned the property parameter symbol while
        // GetSymbolInfo in the body returned the accessor method parameter symbol.
        // Fix: use accessor method's parameter symbols in LowerIndexer.
        var src = """
            using SFLib.Interop;

            public class Container
            {
                private LuaObject _data;
                public Container() { _data = LuaInterop.CreateTable(); }

                public int this[int key]
                {
                    get => LuaInterop.Get<int>(_data, key);
                    set => LuaInterop.Set(_data, key, value);
                }
            }

            public static class P
            {
                public static int Run()
                {
                    var c = new Container();
                    c[0] = 42;
                    return c[0];
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);
        Assert.Equal(0, exitCode);

        // get_Item: parameter name in signature must match body usage
        Assert.Contains("function SF__.Container:get_Item(key)", lua);
        Assert.Contains("self._data[key]", lua);

        // set_Item: parameter names must be consistent
        Assert.Contains("function SF__.Container:set_Item(key", lua);
    }

    [Fact]
    public async Task String_instance_methods_produce_diagnostic()
    {
        // string.Split is now lowered to StrSplit__ helper.
        // Other unsupported string methods should produce diagnostics.
        var src = """
            public static class Demo
            {
                public static string DoReplace(string name)
                {
                    return name.Replace("a", "b");
                }
            }
            """;

        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, "Test.cs");
        await File.WriteAllTextAsync(file, src);

        var compilation = await new SharpForge.Transpiler.Frontend.RoslynFrontend(System.Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(file) }, CancellationToken.None);
        var module = new SharpForge.Transpiler.Frontend.IRLowering().Lower(compilation, CancellationToken.None);

        Assert.Contains(module.Diagnostics, d => d.Contains("string.Replace is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task String_split_lowers_to_helper()
    {
        // string.Split should be lowered to SF__.StrSplit__ helper
        var src = """
            public static class Demo
            {
                public static string[] SplitPath(string name)
                {
                    return name.Split('/');
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        Assert.Contains("SF__.StrSplit__(name, \"/\")", lua);
        Assert.DoesNotContain("Split__k", lua);
    }

    [Fact]
    public async Task Struct_local_used_as_initializer_is_flattened()
    {
        // Bug: var tarPos = globalPos; (local declaration) was not recognized
        // as a supported struct use, so globalPos was not flattened.
        // Fix: Added EqualsValueClause check in CanFlattenStructLocal.
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;
                public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
            }

            public static class P
            {
                public static void Run()
                {
                    var pos = new Vector3(1, 2, 3);
                    var copy = pos;
                    var a = copy.x;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        // pos should be flattened into pos__x, pos__y, pos__z
        Assert.Contains("pos__x", lua);
        Assert.Contains("pos__y", lua);
        Assert.Contains("pos__z", lua);
        // copy should also be flattened (assigned from pos)
        Assert.Contains("copy__x", lua);
        Assert.Contains("copy__y", lua);
        Assert.Contains("copy__z", lua);
    }
}
