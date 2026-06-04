using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class PackStructTests
{
    [Fact]
    public async Task Pack_struct_into_table_on_list_add()
    {
        var src = """
            using SFLib.Interop;
            using StdLib;

            public struct Vec2
            {
                public float x;
                public float y;
                public Vec2(float x, float y) { this.x = x; this.y = y; }
            }

            public static class P
            {
                public static Vec2 Make(float v) => new Vec2(v, v + 1);

                public static void Run()
                {
                    var list = new List<Vec2>();
                    list.Add(Make(1));
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);

        Assert.Equal(0, exitCode);
        // Pack: temp locals for struct fields, then table literal
        Assert.Contains("local __pack_x, __pack_y = SF__.P.Make(1)", lua);
        Assert.Contains("list:Add({x = __pack_x, y = __pack_y})", lua);
    }

    [Fact]
    public async Task Unpack_struct_from_list_get_Item()
    {
        var src = """
            using SFLib.Interop;
            using StdLib;

            public struct Vec2
            {
                public float x;
                public float y;
                public Vec2(float x, float y) { this.x = x; this.y = y; }
            }

            public static class P
            {
                public static void Run()
                {
                    var list = new List<Vec2>();
                    var a = list[0];
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);

        Assert.Equal(0, exitCode);
        // Unpack: temp local for table, then field extraction
        Assert.Contains("local __unpack_tmp = list:get_Item(0)", lua);
        Assert.Contains("local a__x, a__y = __unpack_tmp.x, __unpack_tmp.y", lua);
    }

    [Fact]
    public async Task Inline_field_access_on_pack_struct_return()
    {
        var src = """
            using SFLib.Interop;
            using StdLib;

            public struct Vec2
            {
                public float x;
                public float y;
                public Vec2(float x, float y) { this.x = x; this.y = y; }
            }

            public static class P
            {
                public static void Run()
                {
                    var list = new List<Vec2>();
                    var d = list[0].x;
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);

        Assert.Equal(0, exitCode);
        // Inline access: get_Item returns a table, .x accesses the field
        Assert.Contains("list:get_Item(0).x", lua);
    }

    [Fact]
    public async Task StdLib_types_emitted_before_first_usage()
    {
        var src = """
            using SFLib.Interop;
            using StdLib;

            public struct Vec2
            {
                public float x;
                public float y;
                public Vec2(float x, float y) { this.x = x; this.y = y; }
            }

            public static class P
            {
                public static void Run()
                {
                    var list = new List<Vec2>();
                    list.Add(new Vec2(1, 2));
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);

        Assert.Equal(0, exitCode);

        // StdLib.List definition must appear before its first usage
        var listDef = lua.IndexOf("SF__.StdLib.List = SF__.StdLib.List or {}", StringComparison.Ordinal);
        var listUsage = lua.IndexOf("SF__.StdLib.List.New__0()", StringComparison.Ordinal);
        Assert.True(listDef >= 0, "SF__.StdLib.List definition not found");
        Assert.True(listUsage >= 0, "SF__.StdLib.List.New__0() usage not found");
        Assert.True(listDef < listUsage, "SF__.StdLib.List must be defined before first usage");
    }

    [Fact]
    public async Task Foreach_on_IIpairs_type_uses_IpairsNext()
    {
        var src = """
            using SFLib.Interop;
            using StdLib;

            public static class P
            {
                public static void Run()
                {
                    var list = new List<int>();
                    foreach (var item in list)
                    {
                        print(item);
                    }
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);

        Assert.Equal(0, exitCode);
        // IIpairs: foreach uses IpairsNext, not ipairs
        Assert.Contains("SF__.StdLib.List.IpairsNext", lua);
        Assert.DoesNotContain("ipairs(", lua);
        // IIpairs interface should NOT appear in output
        Assert.DoesNotContain("IIpairs", lua);
    }

    [Fact]
    public async Task LuaRequires_emitted_before_type_declarations()
    {
        var src = """
            using SFLib.Interop;

            [Lua(Require = "Lib.class")]
            public class Base { }

            [Lua(Class = "MyClass", Require = "Lib.utils")]
            public class MyClass : Base { }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src);

        Assert.Equal(0, exitCode);

        // All requires must appear before any type declaration
        var requireClass = lua.IndexOf("require(\"Lib.class\")", StringComparison.Ordinal);
        var requireUtils = lua.IndexOf("require(\"Lib.utils\")", StringComparison.Ordinal);
        var typeDecl = lua.IndexOf("SF__.MyClass", StringComparison.Ordinal);
        Assert.True(requireClass >= 0, "require Lib.class not found");
        Assert.True(requireUtils >= 0, "require Lib.utils not found");
        Assert.True(typeDecl >= 0, "SF__.MyClass not found");
        Assert.True(requireClass < typeDecl, "require must come before type declaration");
        Assert.True(requireUtils < typeDecl, "require must come before type declaration");
    }
}
