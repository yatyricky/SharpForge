using System.CommandLine;
using SharpForge.Transpiler.Pipeline;

namespace SharpForge.Transpiler.Cli;

/// <summary>
/// Builds the <c>sf-transpile</c> root command. The transpiler does one thing,
/// so all options live directly on the root — no <c>build</c> subcommand.
/// </summary>
internal static class RootCommandFactory
{
    public static RootCommand Create()
    {
        var inputArg = new Argument<DirectoryInfo>(
            name: "input",
            description: "Directory containing C# source files (recursively scanned for *.cs).");

        var outputOpt = new Option<FileInfo?>(
            aliases: ["--output", "-o"],
            description: $"Output Lua file. Defaults to <input>/{TranspileOptions.DefaultOutputFileName}.");

        var checkOpt = new Option<bool>(
            aliases: ["--check", "-c"],
            description: "Lint only: parse, lower and emit but do not write the output file.");

        var defineOpt = new Option<string[]>(
            aliases: ["--define", "-d"],
            description: "Preprocessor symbols (repeatable).")
        {
            AllowMultipleArgumentsPerToken = true,
        };

        var rootTableOpt = new Option<string>(
            aliases: ["--root-table", "-r"],
            getDefaultValue: () => TranspileOptions.DefaultRootTable,
            description: "Top-level Lua table holding every transpiled namespace and type.");

        var ignoreClassOpt = new Option<string[]>(
            aliases: ["--ignore-class", "-i"],
            getDefaultValue: () => new[] { TranspileOptions.DefaultIgnoredClass },
            description: "Class names to skip during Lua emit (e.g. JASS-binding host classes). Repeatable.")
        {
            AllowMultipleArgumentsPerToken = true,
        };

        var verboseOpt = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Verbose diagnostics.");

        var root = new RootCommand("SharpForge Transpiler — C# to Lua compiler for Warcraft III: Reforged maps.")
        {
            inputArg,
            outputOpt,
            checkOpt,
            defineOpt,
            rootTableOpt,
            ignoreClassOpt,
            verboseOpt,
        };

        root.SetHandler(async (context) =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArg);
            var output = context.ParseResult.GetValueForOption(outputOpt)
                ?? new FileInfo(Path.Combine(input.FullName, TranspileOptions.DefaultOutputFileName));

            var options = new TranspileOptions(
                InputDirectory: input,
                OutputFile: output,
                PreprocessorSymbols: context.ParseResult.GetValueForOption(defineOpt) ?? Array.Empty<string>(),
                RootTable: context.ParseResult.GetValueForOption(rootTableOpt) ?? TranspileOptions.DefaultRootTable,
                IgnoredClasses: context.ParseResult.GetValueForOption(ignoreClassOpt) ?? Array.Empty<string>(),
                CheckOnly: context.ParseResult.GetValueForOption(checkOpt),
                Verbose: context.ParseResult.GetValueForOption(verboseOpt));

            var pipeline = new TranspilePipeline();
            context.ExitCode = await pipeline.RunAsync(options, context.GetCancellationToken());
        });

        return root;
    }
}
