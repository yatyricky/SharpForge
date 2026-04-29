using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpForge.Transpiler.Frontend;

/// <summary>
/// Roslyn-based front-end: parses C# files into a <see cref="CSharpCompilation"/>.
/// No mscorlib references are emitted into the Lua output; this compilation is used
/// purely for semantic analysis (symbol binding, type info, diagnostics).
///
/// Performance: any <c>*.g.cs</c> source (except <c>GlobalUsings.g.cs</c>, whose
/// <c>global using</c> directives are per-compilation and cannot be referenced) is
/// pre-compiled into a cached DLL and added as a <see cref="MetadataReference"/>.
/// This keeps the user-code compilation small even when the JASS bindings are
/// thousands of stubs.
/// </summary>
public sealed class RoslynFrontend
{
    private static readonly string CacheDir =
        Path.Combine(Path.GetTempPath(), "SharpForge", "BindingsCache");

    private readonly IReadOnlyList<string> _preprocessorSymbols;

    public RoslynFrontend(IReadOnlyList<string> preprocessorSymbols)
    {
        _preprocessorSymbols = preprocessorSymbols;
    }

    public Task<CSharpCompilation> CompileAsync(IEnumerable<FileInfo> sourceFiles, CancellationToken cancellationToken)
    {
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(_preprocessorSymbols);

        var allFiles = sourceFiles.ToArray();
        var bindingFiles = allFiles
            .Where(f => f.Name.EndsWith(".g.cs", StringComparison.Ordinal)
                        && !f.Name.Equals("GlobalUsings.g.cs", StringComparison.Ordinal))
            .ToArray();
        var userFiles = allFiles.Except(bindingFiles).ToArray();

        var trees = userFiles.Select(f =>
        {
            var text = File.ReadAllText(f.FullName);
            return CSharpSyntaxTree.ParseText(text, parseOptions, path: f.FullName, cancellationToken: cancellationToken);
        }).ToArray();

        var references = new List<MetadataReference>(Basic.Reference.Assemblies.Net100.References.All);
        if (bindingFiles.Length > 0)
        {
            var dllPath = GetOrBuildBindingsAssembly(bindingFiles, parseOptions, cancellationToken);
            references.Add(MetadataReference.CreateFromFile(dllPath));
        }

        // Use bundled .NET 10 reference assemblies so semantic queries work even when the
        // transpiler ships as a single-file executable (Assembly.Location returns "").
        // These references exist purely for compile-time analysis; nothing is emitted to Lua.
        var compilation = CSharpCompilation.Create(
            assemblyName: "SharpForgeUserScript",
            syntaxTrees: trees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        return Task.FromResult(compilation);
    }

    private static string GetOrBuildBindingsAssembly(
        FileInfo[] bindingFiles, CSharpParseOptions parseOptions, CancellationToken ct)
    {
        // Sort by path for stable hashing across runs.
        Array.Sort(bindingFiles, (a, b) => string.CompareOrdinal(a.FullName, b.FullName));

        var contents = bindingFiles.Select(f => File.ReadAllText(f.FullName)).ToArray();
        var hash = HashSources(bindingFiles, contents);

        Directory.CreateDirectory(CacheDir);
        var dllPath = Path.Combine(CacheDir, $"bindings-{hash}.dll");
        if (File.Exists(dllPath))
        {
            return dllPath;
        }

        var trees = bindingFiles.Zip(contents, (f, t) =>
            CSharpSyntaxTree.ParseText(t, parseOptions, path: f.FullName, cancellationToken: ct)).ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: $"SharpForgeBindings_{hash}",
            syntaxTrees: trees,
            references: Basic.Reference.Assemblies.Net100.References.All,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));

        var tmp = dllPath + ".tmp";
        using (var fs = File.Create(tmp))
        {
            var emit = compilation.Emit(fs, cancellationToken: ct);
            if (!emit.Success)
            {
                fs.Dispose();
                File.Delete(tmp);
                var first = emit.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
                throw new InvalidOperationException(
                    $"Failed to pre-compile JASS bindings: {first?.ToString() ?? "unknown error"}");
            }
        }
        File.Move(tmp, dllPath, overwrite: true);
        return dllPath;
    }

    private static string HashSources(FileInfo[] files, string[] contents)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        for (int i = 0; i < files.Length; i++)
        {
            sb.Append(files[i].Name).Append('\0').Append(contents[i]).Append('\0');
        }
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 16);
    }
}
