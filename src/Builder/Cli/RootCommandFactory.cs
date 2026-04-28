using System.CommandLine;

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
        root.AddCommand(PackCommand.Create());
        root.AddCommand(InjectCommand.Create());
        return root;
    }
}
