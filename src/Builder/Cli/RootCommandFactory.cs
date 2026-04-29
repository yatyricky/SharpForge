using System.CommandLine;
using SharpForge.Builder.Pack;

namespace SharpForge.Builder.Cli;

/// <summary>
/// Builds the root <c>sf-build</c> command and its subcommands.
/// </summary>
internal static class RootCommandFactory
{
    public static RootCommand Create()
    {
        var root = new RootCommand(
            "SharpForge Builder — packs Lua files and injects war3map.lua into a .w3x map.");

        var inputArg = new Argument<FileInfo>(
            name: "input",
            description: "Entry Lua script to bundle.");

        var outputOpt = new Option<FileInfo?>(
            aliases: ["--output", "-o"],
            description: "Optional output folder or .w3x target. Defaults to bundle.lua next to the entry script.");

        var includeOpt = new Option<string?>(
            aliases: ["--include"],
            description: "Semicolon-separated Lua files to include for dynamic dependencies.");

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

        root.AddArgument(inputArg);
        root.AddOption(outputOpt);
        root.AddOption(includeOpt);
        root.AddOption(csharpInputOpt);
        root.AddOption(rootTableOpt);
        root.AddOption(verboseOpt);

        root.SetHandler(async (input, output, include, csharpInput, rootTable, verbose) =>
        {
            var packer = new LuaPacker();
            Environment.ExitCode = await packer.RunAsync(
                new PackOptions(input, output, SplitIncludes(include), csharpInput, rootTable, verbose),
                CancellationToken.None);
        }, inputArg, outputOpt, includeOpt, csharpInputOpt, rootTableOpt, verboseOpt);

        return root;
    }

    private static IReadOnlyList<string> SplitIncludes(string? include)
        => string.IsNullOrWhiteSpace(include)
            ? Array.Empty<string>()
            : include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
