using System.Reflection;

namespace SharpForge.Transpiler.Pipeline;

internal static class LibraryAssetCopier
{
    private const string LibraryResourcePrefix = "SharpForge.Transpiler.Assets.libs/";
    private const string ProjectTemplateResourceName = "SharpForge.Transpiler.Assets.templates/SharpForge.Project.csproj.template";

    public static async Task CopyBundledAssetsAsync(DirectoryInfo inputDirectory, CancellationToken cancellationToken)
    {
        var assembly = typeof(LibraryAssetCopier).Assembly;
        await CopyProjectTemplateAsync(assembly, inputDirectory, cancellationToken);

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(LibraryResourcePrefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = resourceName[LibraryResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
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
    }

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
