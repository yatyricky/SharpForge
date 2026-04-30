namespace SharpForge.Builder.Pack;

using SharpForge.Builder.Inject;
using SharpForge.Transpiler.Pipeline;

public sealed record PackOptions(
    FileInfo InputScript,
    FileInfo? OutputFile,
    IReadOnlyList<string> IncludePaths,
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
        if (!options.InputScript.Exists)
        {
            Console.Error.WriteLine($"[sf-build] input script not found: {options.InputScript.FullName}");
            return 2;
        }

        if (!options.InputScript.Extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"[sf-build] input script must be a .lua file: {options.InputScript.FullName}");
            return 2;
        }

        var startupFiles = new List<FileInfo>();
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

                startupFiles.Add(transpiledFile);
            }

            var includeFiles = ResolveIncludeFiles(options.InputScript.Directory!, options.IncludePaths).ToArray();
            var bundle = await new LuaBundleBuilder().BuildAsync(
                new LuaBundleOptions(options.InputScript, includeFiles, startupFiles),
                cancellationToken).ConfigureAwait(false);

            var outputExit = await WriteOutputAsync(options, bundle.Text, cancellationToken).ConfigureAwait(false);
            if (outputExit != 0)
            {
                return outputExit;
            }

            if (options.Verbose)
            {
                Console.Error.WriteLine($"[sf-build] packed {bundle.ModuleKeys.Count} Lua file(s)");
            }

            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        finally
        {
            if (tempDirectory is not null && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static IEnumerable<FileInfo> ResolveIncludeFiles(DirectoryInfo root, IReadOnlyList<string> includePaths)
    {
        foreach (var includePath in includePaths)
        {
            var path = Path.IsPathRooted(includePath)
                ? includePath
                : Path.Combine(root.FullName, includePath);
            yield return new FileInfo(path);
        }
    }

    private static async Task<int> WriteOutputAsync(PackOptions options, string bundle, CancellationToken cancellationToken)
    {
        if (options.OutputFile is null)
        {
            var outputFile = new FileInfo(Path.Combine(options.InputScript.Directory!.FullName, "bundle.lua"));
            if (outputFile.Directory is { } outputDirectory)
            {
                outputDirectory.Create();
            }

            await File.WriteAllTextAsync(outputFile.FullName, bundle, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var outputPath = options.OutputFile.FullName;
        if (File.Exists(outputPath))
        {
            if (IsWar3MapLuaPath(outputPath))
            {
                return await new MapInjector().InjectBundleAsync(outputPath, bundle, cancellationToken).ConfigureAwait(false);
            }

            if (!IsW3xPath(outputPath))
            {
                Console.Error.WriteLine($"[sf-build] output file is not a .w3x map: {outputPath}");
                return 2;
            }

            return await new MapInjector().InjectBundleAsync(outputPath, bundle, cancellationToken).ConfigureAwait(false);
        }

        if (Directory.Exists(outputPath))
        {
            if (IsW3xPath(outputPath))
            {
                return await new MapInjector().InjectBundleAsync(outputPath, bundle, cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(Path.Combine(outputPath, "bundle.lua"), bundle, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (IsW3xPath(outputPath))
        {
            Console.Error.WriteLine($"[sf-build] output .w3x target not found: {outputPath}");
            return 2;
        }

        if (IsWar3MapLuaPath(outputPath))
        {
            return await new MapInjector().InjectBundleAsync(outputPath, bundle, cancellationToken).ConfigureAwait(false);
        }

        if (Path.HasExtension(outputPath))
        {
            Console.Error.WriteLine($"[sf-build] output file is not a .w3x map: {outputPath}");
            return 2;
        }

        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "bundle.lua"), bundle, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static bool IsW3xPath(string path)
        => Path.GetExtension(path).Equals(".w3x", StringComparison.OrdinalIgnoreCase);

    private static bool IsWar3MapLuaPath(string path)
        => Path.GetFileName(path).Equals("war3map.lua", StringComparison.OrdinalIgnoreCase);

}
