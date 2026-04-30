namespace SharpForge.Transpiler.Pipeline;

public sealed record TranspileOptions(
    DirectoryInfo InputDirectory,
    FileInfo OutputFile,
    IReadOnlyList<string> PreprocessorSymbols,
    string RootTable,
    IReadOnlyList<string> IgnoredClasses,
    IReadOnlyList<string> LibraryFolders,
    bool CheckOnly,
    bool Verbose)
{
    public const string DefaultRootTable = "SF__";
    public const string DefaultOutputFileName = "sf-out.lua";
    public const string DefaultIgnoredClass = "JASS";
    public const string DefaultLibraryFolder = "libs";
}
