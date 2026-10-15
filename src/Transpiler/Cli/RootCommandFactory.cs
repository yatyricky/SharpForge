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
        var inputArg = new Argument<DirectoryInfo?>(
            name: "input",
            description: "Directory containing C# source files (recursively scanned for *.cs). Required unless --list-jass is used.")
        {
            Arity = ArgumentArity.ZeroOrOne,
        };

        var outputOpt = new Option<FileInfo?>(
            aliases: ["--output", "-o"],
            description: $"Output Lua file. Defaults to <input>/{TranspileOptions.DefaultOutputFileName}.");

        var checkOpt = new Option<bool>(
            aliases: ["--check", "-c"],
            description: "Lint only: parse, lower and emit but do not write the output file.");

        var initOpt = new Option<bool>(
            aliases: ["--init"],
            description: "Initialize editor support only: copy the csproj template and bundled libs, then exit.");

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

        var ignoredNamespaceOpt = new Option<string[]>(
            aliases: ["--ignore-namespace"],
            getDefaultValue: () => new[] { TranspileOptions.DefaultIgnoredNamespace },
            description: "Namespaces to skip during Lua lowering (e.g. SFLib.Interop). Repeatable.")
        {
            AllowMultipleArgumentsPerToken = true,
        };

        var verboseOpt = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Verbose diagnostics.");

        var jassVersionOpt = new Option<string>(
            aliases: ["--jass-version"],
            description: "JASS bindings baseline version deployed into <input>/libs (e.g. 3.0.0). Defaults to the latest bundled version.");

        var traceCallsOpt = new Option<bool>(
            aliases: ["--trace-calls"],
            description: "Instrument every method with a shadow call stack (SF__TRACE__) that StdLib assertion errors embed into their messages. Debug builds only.");

        var listJassOpt = new Option<bool>(
            aliases: ["--list-jass"],
            description: "Print the bundled JASS bindings versions (oldest first) and exit.");

        var root = new RootCommand("SharpForge Transpiler — C# to Lua compiler for Warcraft III: Reforged maps.")
        {
            inputArg,
            outputOpt,
            checkOpt,
            initOpt,
            defineOpt,
            rootTableOpt,
            ignoreClassOpt,
            ignoredNamespaceOpt,
            verboseOpt,
            jassVersionOpt,
            traceCallsOpt,
            listJassOpt,
        };

        root.SetHandler(async (context) =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArg);

            if (context.ParseResult.GetValueForOption(listJassOpt))
            {
                foreach (var version in LibraryAssetCopier.GetBundledJassVersions())
                {
                    await Console.Out.WriteLineAsync(version);
                }
                context.ExitCode = 0;
                return;
            }

            if (input is null)
            {
                await Console.Error.WriteLineAsync("Input directory is required.");
                context.ExitCode = 2;
                return;
            }

            var output = context.ParseResult.GetValueForOption(outputOpt)
                ?? new FileInfo(Path.Combine(input.FullName, TranspileOptions.DefaultOutputFileName));

            var options = new TranspileOptions(
                InputDirectory: input,
                OutputFile: output,
                PreprocessorSymbols: context.ParseResult.GetValueForOption(defineOpt) ?? Array.Empty<string>(),
                RootTable: context.ParseResult.GetValueForOption(rootTableOpt) ?? TranspileOptions.DefaultRootTable,
                IgnoredClasses: context.ParseResult.GetValueForOption(ignoreClassOpt) ?? Array.Empty<string>(),
                IgnoredNamespaces: context.ParseResult.GetValueForOption(ignoredNamespaceOpt) ?? Array.Empty<string>(),
                CheckOnly: context.ParseResult.GetValueForOption(checkOpt),
                Verbose: context.ParseResult.GetValueForOption(verboseOpt),
                InitOnly: context.ParseResult.GetValueForOption(initOpt),
                JassVersion: context.ParseResult.GetValueForOption(jassVersionOpt) ?? string.Empty,
                TraceCalls: context.ParseResult.GetValueForOption(traceCallsOpt));

            var pipeline = new TranspilePipeline();
            context.ExitCode = await pipeline.RunAsync(options, context.GetCancellationToken());
        });

        return root;
    }
}
