using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class StdLibTests
{
    [Fact]
    public async Task Dictionary_remove_clears_table_slot_and_keeps_key_list_in_sync()
    {
        var src = """
            using StdLib;

            public static class Program
            {
                public static void Main()
                {
                    var dict = new Dictionary<string, int>();
                    dict["a"] = 1;
                    var removed = dict.Remove("a");
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "DictionaryRemove.cs");

        Assert.Equal(0, exitCode);
        Assert.Contains("function SF__.StdLib.Dictionary:Remove(key", lua);
        Assert.Contains("dict:Remove(\"a\")", lua);
        Assert.Matches(@"self\._table\[\w+\] = nil", lua);
        Assert.Contains("self.Count = (self.Count - 1)", lua);
        Assert.Contains("self._keys:Remove(key", lua);
        Assert.Contains("self._version = (self._version + 1)", lua);
    }

    [Fact]
    public async Task List_contains_delegates_to_index_of()
    {
        var src = """
            using StdLib;

            public static class Program
            {
                public static void Main()
                {
                    var list = new List<int>();
                    list.Add(3);
                    var found = list.Contains(3);
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "ListContains.cs");

        Assert.Equal(0, exitCode);
        Assert.Contains("function SF__.StdLib.List:Contains(item", lua);
        Assert.Matches(@"return \(self:IndexOf\(item\w*\) ~= \(-1\)\)", lua);
        Assert.Contains("list:Contains(3)", lua);
    }

    [Fact]
    public async Task List_collection_initializer_emits_add_calls()
    {
        var src = """
            using StdLib;

            public enum EffectType
            {
                DAMAGE,
            }

            public class EffectConfigEntry
            {
                public string id;
                public EffectType type;
            }

            public class EffectConfig
            {
                public static List<EffectConfigEntry> Data = new List<EffectConfigEntry>
                {
                    new EffectConfigEntry { id = "001", type = EffectType.DAMAGE },
                };

                public static List<int> Numbers = new List<int> { 1, 2, 3 };
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "ListCollectionInitializer.cs");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"obj\w*:Add\(\(function\(\)", lua);
        Assert.Matches(@"obj\w*\.id = ""001""", lua);
        Assert.Matches(@"obj\w*:Add\(1\)", lua);
        Assert.Matches(@"obj\w*:Add\(2\)", lua);
        Assert.Matches(@"obj\w*:Add\(3\)", lua);
        Assert.DoesNotContain("obj:New(", lua);
        Assert.DoesNotContain("System.Collections", lua);
        Assert.DoesNotContain("__sf_interfaces", lua);
    }

    [Fact]
    public async Task List_foreach_after_collection_initializer_keeps_typed_elements()
    {
        var src = """
            using StdLib;

            public static class Program
            {
                public static int Main()
                {
                    var list = new List<int> { 1, 2, 3 };
                    var sum = 0;
                    foreach (var n in list)
                    {
                        sum += n;
                    }
                    return sum;
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "ListForEachTyped.cs");

        Assert.Equal(0, exitCode);
        Assert.Contains("for _, n in (SF__.StdLib.List.IpairsNext)(collection", lua);
        Assert.Contains("sum = (sum + n)", lua);
    }

    [Fact]
    public async Task Dictionary_collection_initializer_emits_add_calls()
    {
        var src = """
            using StdLib;

            public static class Config
            {
                public static Dictionary<string, int> Map = new Dictionary<string, int>
                {
                    { "a", 1 },
                    { "b", 2 },
                };
            }

            public static class Program
            {
                public static void Main()
                {
                    var local = new Dictionary<string, int>
                    {
                        { "x", 7 },
                    };
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "DictionaryCollectionInitializer.cs");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"obj\w*:Add\(""a"", 1\)", lua);
        Assert.Matches(@"obj\w*:Add\(""b"", 2\)", lua);
        Assert.Matches(@"obj\w*:Add\(""x"", 7\)", lua);
        Assert.Contains("function SF__.StdLib.Dictionary:Add(key", lua);
        Assert.Contains("self:set_Item(key", lua);
        Assert.DoesNotContain("System.Collections", lua);
        Assert.DoesNotContain("__sf_interfaces", lua);
    }

    [Fact]
    public async Task Dictionary_foreach_deconstruction_uses_pairs_next_protocol()
    {
        var src = """
            using System;
            using StdLib;

            public static class Program
            {
                public static void Main()
                {
                    var dict = new Dictionary<string, int>
                    {
                        { "a", 1 },
                        { "b", 2 },
                    };

                    foreach (var (key, value) in dict)
                    {
                        Console.WriteLine(key);
                    }

                    foreach (var (k, _) in dict)
                    {
                        Console.WriteLine(k);
                    }
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "DictForeachDeconstruct.cs");

        Assert.Equal(0, exitCode);
        Assert.Contains("for key, value in (SF__.StdLib.Dictionary.PairsNext)(collection", lua);
        Assert.Contains("for k, _ in (SF__.StdLib.Dictionary.PairsNext)(collection", lua);
        // List plain foreach is unchanged.
        Assert.DoesNotContain("for key, value in (SF__.StdLib.List.IpairsNext)", lua);
    }

    [Fact]
    public async Task Dictionary_foreach_plain_form_packs_pair_into_table()
    {
        var src = """
            using System;
            using StdLib;

            public static class Program
            {
                public static void Main()
                {
                    var dict = new Dictionary<string, int>
                    {
                        { "a", 1 },
                    };

                    foreach (var p in dict)
                    {
                        Console.WriteLine(p.key);
                        Console.WriteLine(p.value);
                    }
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "DictForeachPlain.cs");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"for __key\w*, __value\w* in \(SF__\.StdLib\.Dictionary\.PairsNext\)\(collection\w*\) do", lua);
        Assert.Matches(@"local p = \{key = __key\w*, value = __value\w*\}", lua);
        Assert.Contains("p.key", lua);
    }
}
