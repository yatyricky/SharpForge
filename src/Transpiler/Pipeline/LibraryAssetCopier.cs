using System.Reflection;

namespace SharpForge.Transpiler.Pipeline;

internal static class LibraryAssetCopier
{
    private const string LibraryResourcePrefix = "SharpForge.Transpiler.Assets.libs/";
    private const string JassFolderPrefix = "Jass-";
    private const string ProjectTemplateResourceName = "SharpForge.Transpiler.Assets.templates/SharpForge.Project.csproj.template";

    /// <summary>
    /// JASS binding baseline versions bundled as embedded resources, oldest first.
    /// </summary>
    public static IReadOnlyList<string> GetBundledJassVersions()
    {
        var assembly = typeof(LibraryAssetCopier).Assembly;
        var versions = new SortedSet<string>(Comparer<string>.Create(CompareVersions));
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(LibraryResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var topSegment = GetRelativeResourcePath(resourceName).Split('/', '\\')[0];
            if (topSegment.StartsWith(JassFolderPrefix, StringComparison.Ordinal))
            {
                versions.Add(topSegment[JassFolderPrefix.Length..]);
            }
        }

        return versions.ToList();
    }

    public static async Task CopyBundledAssetsAsync(
        DirectoryInfo inputDirectory,
        string jassVersion,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(LibraryAssetCopier).Assembly;
        await CopyProjectTemplateAsync(assembly, inputDirectory, cancellationToken);

        var selectedJassFolder = JassFolderPrefix + jassVersion;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(LibraryResourcePrefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = GetRelativeResourcePath(resourceName);
            var topSegment = relativePath.Split('/', '\\')[0];
            if (topSegment.StartsWith(JassFolderPrefix, StringComparison.Ordinal)
                && !string.Equals(topSegment, selectedJassFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var outputPath = Path.Combine(inputDirectory.FullName, TranspileOptions.DefaultLibraryFolder, relativePath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await using var resource = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Bundled library resource not found: {resourceName}");
            await using var output = File.Create(outputPath);
            await resource.CopyToAsync(output, cancellationToken);
        }

        RemoveStaleJassBaselines(inputDirectory, selectedJassFolder, verbose);
    }

    /// <summary>
    /// Deletes other bundled JASS baseline versions left in <c>&lt;input&gt;/libs</c> by earlier
    /// runs, so two binding sets never coexist (duplicate type definitions break compilation).
    /// Only folders matching a bundled baseline name are removed; custom folders are untouched.
    /// </summary>
    private static void RemoveStaleJassBaselines(DirectoryInfo inputDirectory, string selectedJassFolder, bool verbose)
    {
        var libsRoot = Path.Combine(inputDirectory.FullName, TranspileOptions.DefaultLibraryFolder);
        foreach (var version in GetBundledJassVersions())
        {
            var folderName = JassFolderPrefix + version;
            if (string.Equals(folderName, selectedJassFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = Path.Combine(libsRoot, folderName);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                if (verbose)
                {
                    Console.WriteLine($"[sf-transpile] Removed stale JASS bindings {version}: {path}");
                }
            }
        }
    }

    private static int CompareVersions(string? x, string? y)
    {
        var xParsed = Version.TryParse(x, out var xVersion);
        var yParsed = Version.TryParse(y, out var yVersion);
        if (xParsed && yParsed)
        {
            return xVersion!.CompareTo(yVersion);
        }

        return string.CompareOrdinal(x, y);
    }

    private static string GetRelativeResourcePath(string resourceName)
        => resourceName[LibraryResourcePrefix.Length..]
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

    private static async Task CopyProjectTemplateAsync(
        Assembly assembly,
        DirectoryInfo inputDirectory,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(inputDirectory.FullName, inputDirectory.Name + ".csproj");
        if (File.Exists(outputPath))
        {
            return;
        }

        await using var resource = assembly.GetManifestResourceStream(ProjectTemplateResourceName)
            ?? throw new InvalidOperationException($"Bundled project template resource not found: {ProjectTemplateResourceName}");
        await using var output = File.Create(outputPath);
        await resource.CopyToAsync(output, cancellationToken);
    }
}
