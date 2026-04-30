using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpForge.Transpiler.Emitter;
using SharpForge.Transpiler.Frontend;

namespace SharpForge.Transpiler.Pipeline;

/// <summary>
/// Orchestrates the full transpile pipeline:
/// C# source -> Roslyn -> IR -> LuaEmitter -> .lua file.
/// </summary>
public sealed class TranspilePipeline
{
    public async Task<int> RunAsync(TranspileOptions options, CancellationToken cancellationToken)
    {
        if (!options.InputDirectory.Exists)
        {
            await Console.Error.WriteLineAsync($"Input directory not found: {options.InputDirectory.FullName}");
            return 2;
        }

        if (Directory.Exists(options.OutputFile.FullName))
        {
            await Console.Error.WriteLineAsync(
                $"Output path is a directory, expected a file: {options.OutputFile.FullName}");
            return 2;
        }

        await LibraryAssetCopier.CopyBundledLibrariesAsync(options.InputDirectory, cancellationToken);

        var sourceFiles = EnumerateSourceFiles(options.InputDirectory).ToArray();
        if (sourceFiles.Length == 0)
        {
            await Console.Error.WriteLineAsync("No C# source files found.");
            return 2;
        }

        if (options.Verbose)
        {
            Console.WriteLine($"[sf-transpile] Found {sourceFiles.Length} source file(s).");
        }

        // 1. Roslyn frontend: parse + compile.
        var frontend = new RoslynFrontend(options.PreprocessorSymbols);
        var compilation = await frontend.CompileAsync(sourceFiles, cancellationToken);

        var hasErrors = false;
        foreach (var diag in compilation.GetDiagnostics(cancellationToken))
        {
            if (diag.Severity == DiagnosticSeverity.Error)
            {
                hasErrors = true;
                await Console.Error.WriteLineAsync(diag.ToString());
            }
            else if (options.Verbose)
            {
                Console.WriteLine(diag.ToString());
            }
        }

        if (hasErrors)
        {
            return 1;
        }

        // 2. Lower compilation to custom IR.
        var lowering = new IRLowering(options.IgnoredClasses, options.InputDirectory, options.LibraryFolders);
        var module = lowering.Lower(compilation, cancellationToken);
        if (module.Diagnostics.Count > 0)
        {
            foreach (var diagnostic in module.Diagnostics)
            {
                await Console.Error.WriteLineAsync(diagnostic);
            }
            return 1;
        }

        // 3. Emit Lua.
        var emitter = new LuaEmitter(options.RootTable);
        var lua = emitter.Emit(module);

        if (options.CheckOnly)
        {
            if (options.Verbose)
            {
                Console.WriteLine($"[sf-transpile] Check OK ({lua.Length} chars, output not written).");
            }
            return 0;
        }

        var outputDir = options.OutputFile.DirectoryName;
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        await File.WriteAllTextAsync(options.OutputFile.FullName, lua, cancellationToken);

        if (options.Verbose)
        {
            Console.WriteLine($"[sf-transpile] Wrote {options.OutputFile.FullName} ({lua.Length} chars).");
        }

        return 0;
    }

    private static IEnumerable<FileInfo> EnumerateSourceFiles(DirectoryInfo inputDirectory)
    {
        return inputDirectory
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(file => !IsInBuildOutputDirectory(file, inputDirectory));
    }

    private static bool IsInBuildOutputDirectory(FileInfo file, DirectoryInfo inputDirectory)
    {
        var root = inputDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, file.FullName);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }
}
