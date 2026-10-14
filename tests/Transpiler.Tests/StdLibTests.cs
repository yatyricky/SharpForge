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
}
