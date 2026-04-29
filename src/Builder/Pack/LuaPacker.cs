namespace SharpForge.Builder.Pack;

using System.Text;
using SharpForge.Transpiler.Pipeline;

public sealed record PackOptions(
    DirectoryInfo InputDirectory,
    FileInfo OutputFile,
    DirectoryInfo? CSharpInputDirectory,
    string RootTable,
    bool Verbose);

/// <summary>
/// Bundles a directory of Lua files into a single file in dependency order.
/// </summary>
public sealed class LuaPacker
{
    public async Task<int> RunAsync(PackOptions options, CancellationToken cancellationToken)
    {
        if (!options.InputDirectory.Exists)
        {
            Console.Error.WriteLine($"[sf-build] input directory not found: {options.InputDirectory.FullName}");
            return 2;
        }

        if (options.OutputFile.Exists && (options.OutputFile.Attributes & FileAttributes.Directory) != 0)
        {
            Console.Error.WriteLine($"[sf-build] output path is a directory: {options.OutputFile.FullName}");
            return 2;
        }

        var generatedFiles = new List<FileInfo>();
        string? tempDirectory = null;
        try
        {
            if (options.CSharpInputDirectory is not null)
            {
                if (!options.CSharpInputDirectory.Exists)
                {
                    Console.Error.WriteLine($"[sf-build] C# input directory not found: {options.CSharpInputDirectory.FullName}");
                    return 2;
                }

                tempDirectory = Path.Combine(Path.GetTempPath(), "sf-build-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                var transpiledFile = new FileInfo(Path.Combine(tempDirectory, "transpiled.lua"));
                var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
                    InputDirectory: options.CSharpInputDirectory,
                    OutputFile: transpiledFile,
                    PreprocessorSymbols: Array.Empty<string>(),
                    RootTable: options.RootTable,
                    IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
                    CheckOnly: false,
                    Verbose: options.Verbose), cancellationToken);

                if (exitCode != 0)
                {
                    return exitCode;
                }

                generatedFiles.Add(transpiledFile);
            }

            var luaFiles = options.InputDirectory
                .EnumerateFiles("*.lua", SearchOption.AllDirectories)
                .Where(f => !IsSamePath(f.FullName, options.OutputFile.FullName))
                .OrderBy(f => Path.GetRelativePath(options.InputDirectory.FullName, f.FullName), StringComparer.Ordinal)
                .ToArray();

            var allFiles = generatedFiles.Concat(luaFiles).ToArray();
            if (allFiles.Length == 0)
            {
                Console.Error.WriteLine("[sf-build] no Lua files found to pack.");
                return 2;
            }

            if (options.OutputFile.Directory is { } outputDirectory)
            {
                outputDirectory.Create();
            }

            var sb = new StringBuilder();
            foreach (var file in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var label = generatedFiles.Contains(file)
                    ? "<transpiled-csharp>"
                    : Path.GetRelativePath(options.InputDirectory.FullName, file.FullName).Replace('\\', '/');
                var contents = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                sb.Append("--# source: ").Append(label).Append('\n');
                sb.Append(contents.TrimEnd()).Append("\n\n");
            }

            await File.WriteAllTextAsync(options.OutputFile.FullName, sb.ToString().TrimEnd() + "\n", cancellationToken).ConfigureAwait(false);
            if (options.Verbose)
            {
                Console.Error.WriteLine($"[sf-build] packed {allFiles.Length} Lua file(s) -> {options.OutputFile.FullName}");
            }

            return 0;
        }
        finally
        {
            if (tempDirectory is not null && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static bool IsSamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
