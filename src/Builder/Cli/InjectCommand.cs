using System.CommandLine;
using SharpForge.Builder.Inject;

namespace SharpForge.Builder.Cli;

/// <summary>
/// <c>sf-build inject</c> — injects a bundled Lua script into a .w3x map's
/// <c>war3map.lua</c> via StormLib. Stub: implementation deferred.
/// </summary>
internal static class InjectCommand
{
    public static Command Create()
    {
        var mapArg = new Argument<FileInfo>(
            name: "map",
            description: "Path to the target .w3x map file.");

        var scriptOpt = new Option<FileInfo>(
            aliases: ["--script", "-s"],
            description: "Path to the bundled Lua script to inject.")
        {
            IsRequired = true,
        };

        var verboseOpt = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose diagnostics output.");

        var cmd = new Command("inject", "Inject a bundled Lua script into a .w3x map's war3map.lua via StormLib.")
        {
            mapArg,
            scriptOpt,
            verboseOpt,
        };

        cmd.SetHandler(async (map, script, verbose) =>
        {
            var injector = new MapInjector();
            Environment.ExitCode = await injector.RunAsync(
                new InjectOptions(map, script, verbose),
                CancellationToken.None);
        }, mapArg, scriptOpt, verboseOpt);

        return cmd;
    }
}
