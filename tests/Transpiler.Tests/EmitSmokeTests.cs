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
