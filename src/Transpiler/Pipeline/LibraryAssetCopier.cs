using System.Reflection;

namespace SharpForge.Transpiler.Pipeline;

internal static class LibraryAssetCopier
{
    private const string ResourcePrefix = "SharpForge.Transpiler.Assets.libs/";

    public static async Task CopyBundledLibrariesAsync(DirectoryInfo inputDirectory, CancellationToken cancellationToken)
    {
        var assembly = typeof(LibraryAssetCopier).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = resourceName[ResourcePrefix.Length..]
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
}