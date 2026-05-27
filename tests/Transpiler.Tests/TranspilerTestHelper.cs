using Microsoft.CodeAnalysis;
using SharpForge.Transpiler.Emitter;
using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;

namespace SharpForge.Transpiler.Tests;

internal static class TranspilerTestHelper
{
    public static async Task<string> TranspileAsync(string source, string fileName = "Test.cs")
        => await TranspileSourcesAsync(new Dictionary<string, string> { [fileName] = source });

    public static async Task<string> TranspileSourcesAsync(IReadOnlyDictionary<string, string> sources)
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
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException("C# compile errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));
        }

        var module = new IRLowering().Lower(compilation, CancellationToken.None);
        return new LuaEmitter(TranspileOptions.DefaultRootTable).Emit(module);
    }

    public static async Task<(int ExitCode, string Lua, string TempDir)> TranspileViaPipelineAsync(
        string source,
        string fileName = "Test.cs",
        string[]? preprocessorSymbols = null,
        string[]? ignoredClasses = null,
        string[]? ignoredNamespaces = null)
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var file = Path.Combine(dir.FullName, fileName);
        await File.WriteAllTextAsync(file, source);
        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: new DirectoryInfo(dir.FullName),
            OutputFile: output,
            PreprocessorSymbols: preprocessorSymbols ?? Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: ignoredClasses ?? new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: ignoredNamespaces ?? new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        var lua = output.Exists ? await File.ReadAllTextAsync(output.FullName) : string.Empty;
        return (exitCode, lua, dir.FullName);
    }
}
