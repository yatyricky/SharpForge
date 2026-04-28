using System.CommandLine;
using SharpForge.Builder.Pack;

namespace SharpForge.Builder.Cli;

/// <summary>
/// <c>sf-build pack</c> — topologically orders project Lua files into a single bundle.
/// Stub: implementation deferred.
/// </summary>
internal static class PackCommand
{
    public static Command Create()
    {
        var inputArg = new Argument<DirectoryInfo>(
            name: "input",
            description: "Project directory containing *.lua files to bundle.");

        var outputOpt = new Option<FileInfo>(
            aliases: ["--output", "-o"],
            description: "Output bundled Lua file path.")
        {
            IsRequired = true,
        };

        var verboseOpt = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose diagnostics output.");

        var cmd = new Command("pack", "Topologically sort and bundle project Lua files into a single file.")
        {
            inputArg,
            outputOpt,
            verboseOpt,
        };

        cmd.SetHandler(async (input, output, verbose) =>
        {
            var packer = new LuaPacker();
            Environment.ExitCode = await packer.RunAsync(
                new PackOptions(input, output, verbose),
                CancellationToken.None);
        }, inputArg, outputOpt, verboseOpt);

        return cmd;
    }
}
