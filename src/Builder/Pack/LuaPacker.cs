namespace SharpForge.Builder.Pack;

public sealed record PackOptions(
    DirectoryInfo InputDirectory,
    FileInfo OutputFile,
    bool Verbose);

/// <summary>
/// Bundles a directory of Lua files into a single file in dependency order.
/// </summary>
/// <remarks>
/// Stub: dependency analysis and topological sort are deferred.
/// Final design (per README):
///   1. Discover *.lua files recursively under <see cref="PackOptions.InputDirectory"/>.
///   2. Parse <c>require</c>/<c>__SF</c> exports to build a dependency graph.
///   3. Topologically sort; detect cycles.
///   4. Concatenate in order with origin comments to <see cref="PackOptions.OutputFile"/>.
/// </remarks>
public sealed class LuaPacker
{
    public Task<int> RunAsync(PackOptions options, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("[sf-build] pack: not implemented yet (stub).");
        _ = options;
        _ = cancellationToken;
        return Task.FromResult(2);
    }
}
