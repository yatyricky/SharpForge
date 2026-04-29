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

        var csharpInputOpt = new Option<DirectoryInfo?>(
            aliases: ["--csharp"],
            description: "Optional C# source directory to transpile before packing Lua files.");

        var rootTableOpt = new Option<string>(
            aliases: ["--root-table", "-r"],
            getDefaultValue: () => SharpForge.Transpiler.Pipeline.TranspileOptions.DefaultRootTable,
            description: "Top-level Lua table for transpiled C# when --csharp is used.");

        var verboseOpt = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Enable verbose diagnostics output.");

        var cmd = new Command("pack", "Topologically sort and bundle project Lua files into a single file.")
        {
            inputArg,
            outputOpt,
            csharpInputOpt,
            rootTableOpt,
            verboseOpt,
        };

        cmd.SetHandler(async (input, output, csharpInput, rootTable, verbose) =>
        {
            var packer = new LuaPacker();
            Environment.ExitCode = await packer.RunAsync(
                new PackOptions(input, output, csharpInput, rootTable, verbose),
                CancellationToken.None);
        }, inputArg, outputOpt, csharpInputOpt, rootTableOpt, verboseOpt);

        return cmd;
    }
}
