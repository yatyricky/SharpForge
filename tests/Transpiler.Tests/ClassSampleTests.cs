using SharpForge.Transpiler.Emitter;
using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

/// <summary>
/// Structural assertions for samples\Class.
///
/// The expected file (<c>samples/Class/expected/sharp_forge.lua</c>) is a
/// hand-written *reference shape* — not a byte-for-byte contract. These tests
/// verify the qualities that actually matter for correctness and code quality:
/// single root table, namespace nesting, instance-method colon syntax,
/// constructor scaffolding, this-rewriting, and `..` interpolation.
/// </summary>
public class ClassSampleTests
{
    private static async Task<string> TranspileSampleAsync()
    {
        var sourceDir = new DirectoryInfo(Path.Combine(FindRepoRoot(), "samples"));
        var compilation = await new RoslynFrontend(Array.Empty<string>())
            .CompileAsync(new[] { new FileInfo(Path.Combine(sourceDir.FullName, "Hero.cs")) }, CancellationToken.None);

        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, "C# compile errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));

        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        return new LuaEmitter(TranspileOptions.DefaultRootTable).Emit(module);
    }

    [Fact]
    public async Task Root_table_is_declared_once_idempotently()
    {
        var lua = await TranspileSampleAsync();
        Assert.Contains("SF__ = SF__ or {}", lua);
        Assert.Equal(1, CountOccurrences(lua, "SF__ = SF__ or {}"));
    }

    [Fact]
    public async Task Namespace_and_type_are_nested_under_root()
    {
        var lua = await TranspileSampleAsync();
        Assert.Contains("SF__.Game = SF__.Game or {}", lua);
        Assert.Contains("SF__.Game.Hero = SF__.Game.Hero or {}", lua);
    }

    [Fact]
    public async Task Constructor_emits_New_with_self_setmetatable_scaffold()
    {
        var lua = await TranspileSampleAsync();
        Assert.Contains("function SF__.Game.Hero.New(name, hp)", lua);
        Assert.Contains("setmetatable({}, { __index = SF__.Game.Hero })", lua);
        Assert.Contains("return self", lua);
        // Constructor body assigns to fields via self.
        Assert.Contains("self.Name = name", lua);
        Assert.Contains("self.HP = hp", lua);
    }

    [Fact]
    public async Task Instance_methods_use_colon_syntax()
    {
        var lua = await TranspileSampleAsync();
        Assert.Contains("function SF__.Game.Hero:LevelUp()", lua);
        Assert.Contains("function SF__.Game.Hero:ToString()", lua);
    }

    [Fact]
    public async Task Compound_assignment_is_expanded()
    {
        var lua = await TranspileSampleAsync();
        // `HP += 10;` must lower to `self.HP = self.HP + 10` (parenthesization is fine either way).
        Assert.Matches(@"self\.HP\s*=\s*\(?\s*self\.HP\s*\+\s*10\s*\)?", lua);
    }

    [Fact]
    public async Task Interpolated_string_lowers_to_concatenation_chain()
    {
        var lua = await TranspileSampleAsync();
        // ToString returns `$"{Name} - HP: {HP}"` → must contain `..` joining the fields and the literal.
        Assert.Contains("self.Name", lua);
        Assert.Contains("\" - HP: \"", lua);
        Assert.Contains("self.HP", lua);
        Assert.Contains("..", lua);
        // No empty leading "" .. … fragment.
        Assert.DoesNotContain("\"\" ..", lua);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
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
