namespace SharpForge.Transpiler.Pipeline;

public sealed record TranspileOptions(
    DirectoryInfo InputDirectory,
    FileInfo OutputFile,
    IReadOnlyList<string> PreprocessorSymbols,
    string RootTable,
    IReadOnlyList<string> IgnoredClasses,
    IReadOnlyList<string> LibraryFolders,
    bool CheckOnly,
    bool Verbose,
    bool InitOnly = false)
{
    public const string DefaultRootTable = "SF__";
    public const string DefaultOutputFileName = "sharpforge.lua";
    public const string DefaultIgnoredClass = "JASS";
    public const string DefaultLibraryFolder = "libs";
}
