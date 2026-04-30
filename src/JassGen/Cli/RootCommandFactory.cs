using System.CommandLine;
using SharpForge.JassGen.Emit;
using SharpForge.JassGen.Parser;

namespace SharpForge.JassGen.Cli;

/// <summary>
/// <c>sf-jassgen &lt;input-dir&gt; [-o out-dir]</c> — read every <c>*.j</c> from
/// <c>input-dir</c>, parse it, and emit C# binding stubs into <c>out-dir</c>
/// (default: <c>&lt;input-dir&gt;/generated</c>).
/// </summary>
internal static class RootCommandFactory
{
    public const string DefaultOutputDirName = "generated";

    public static RootCommand Create()
    {
        var inputArg = new Argument<DirectoryInfo>(
            name: "input",
            description: "Directory containing JASS source files (*.j, recursive).");

        var outputOpt = new Option<DirectoryInfo?>(
            aliases: ["--output", "-o"],
            description: $"Output directory for generated .cs files. Defaults to <input>/{DefaultOutputDirName}.");

        var verboseOpt = new Option<bool>(
            aliases: ["--verbose", "-v"],
            description: "Verbose diagnostics.");

        var hostClassOpt = new Option<string>(
            aliases: ["--host-class", "-c"],
            getDefaultValue: () => CSharpEmitter.DefaultHostClass,
            description: "Name of the static partial class that hosts the generated natives and globals.");

        var root = new RootCommand("SharpForge JASS binding generator — common.j/blizzard.j → C# extern stubs.")
        {
            inputArg,
            outputOpt,
            hostClassOpt,
            verboseOpt,
        };

        root.SetHandler(async (context) =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArg);
            var output = context.ParseResult.GetValueForOption(outputOpt)
                ?? new DirectoryInfo(Path.Combine(input.FullName, DefaultOutputDirName));
            bool verbose = context.ParseResult.GetValueForOption(verboseOpt);
            string hostClass = context.ParseResult.GetValueForOption(hostClassOpt) ?? CSharpEmitter.DefaultHostClass;

            context.ExitCode = await RunAsync(input, output, hostClass, verbose, context.GetCancellationToken());
        });

        return root;
    }

    private static async Task<int> RunAsync(
        DirectoryInfo input, DirectoryInfo output, string hostClass, bool verbose, CancellationToken ct)
    {
        if (!input.Exists)
        {
            await Console.Error.WriteLineAsync($"Input directory not found: {input.FullName}");
            return 2;
        }

        if (File.Exists(output.FullName))
        {
            await Console.Error.WriteLineAsync($"Output path is a file, expected a directory: {output.FullName}");
            return 2;
        }

        if (IsSameOrAncestor(output.FullName, input.FullName))
        {
            await Console.Error.WriteLineAsync($"Output directory must not be the input directory or one of its parents: {output.FullName}");
            return 2;
        }

        var sources = input.GetFiles("*.j", SearchOption.AllDirectories);
        if (sources.Length == 0)
        {
            await Console.Error.WriteLineAsync("No *.j source files found.");
            return 2;
        }

        var allNodes = new List<JassNode>();
        foreach (var f in sources)
        {
            string text = await File.ReadAllTextAsync(f.FullName, ct);
            var tokens = JassTokenizer.Tokenize(text);
            var parser = new JassParser(tokens);
            var nodes = parser.Parse();
            allNodes.AddRange(nodes);
            if (verbose)
            {
                Console.WriteLine($"[sf-jassgen] {f.Name}: {tokens.Count} tokens, {nodes.Count} decls, {parser.Errors.Count} parse warnings.");
            }
            foreach (var err in parser.Errors)
            {
                await Console.Error.WriteLineAsync($"{f.Name}: {err}");
            }
        }

        var result = new CSharpEmitter(hostClass).Emit(allNodes);

        if (Directory.Exists(output.FullName))
        {
            Directory.Delete(output.FullName, recursive: true);
        }
        Directory.CreateDirectory(output.FullName);

        await File.WriteAllTextAsync(Path.Combine(output.FullName, "Handles.g.cs"), result.Handles, ct);
        await File.WriteAllTextAsync(Path.Combine(output.FullName, "Natives.g.cs"), result.Natives, ct);
        await File.WriteAllTextAsync(Path.Combine(output.FullName, "Globals.g.cs"), result.Globals, ct);
        await File.WriteAllTextAsync(Path.Combine(output.FullName, "NativeExt.g.cs"), result.NativeExt, ct);
        await File.WriteAllTextAsync(Path.Combine(output.FullName, "GlobalUsings.g.cs"), result.GlobalUsings, ct);

        if (verbose)
        {
            Console.WriteLine($"[sf-jassgen] Wrote 5 files to {output.FullName}.");
        }
        // Parse warnings are non-fatal — recovery already skipped past them.
        return 0;
    }

    private static bool IsSameOrAncestor(string possibleAncestor, string path)
    {
        string ancestor = WithTrailingSeparator(Path.GetFullPath(possibleAncestor));
        string child = WithTrailingSeparator(Path.GetFullPath(path));
        return child.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithTrailingSeparator(string path) =>
        Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
}
