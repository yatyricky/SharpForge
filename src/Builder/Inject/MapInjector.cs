namespace SharpForge.Builder.Inject;

public sealed record InjectOptions(
    FileInfo MapFile,
    FileInfo ScriptFile,
    bool Verbose);

/// <summary>
/// Injects a bundled Lua script into a .w3x map's <c>war3map.lua</c>.
/// </summary>
/// <remarks>
/// Stub: StormLib integration is deferred.
/// Final design (per README):
///   1. Open the .w3x with StormLib (https://github.com/ladislav-zezula/StormLib).
///   2. Read existing <c>war3map.lua</c>.
///   3. Splice the bundled script into the map script (preserving editor-generated code).
///   4. Write back and close the archive.
/// </remarks>
public sealed class MapInjector
{
    public Task<int> RunAsync(InjectOptions options, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("[sf-build] inject: not implemented yet (stub).");
        _ = options;
        _ = cancellationToken;
        return Task.FromResult(2);
    }
}
