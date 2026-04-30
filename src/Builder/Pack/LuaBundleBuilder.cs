namespace SharpForge.Builder.Pack;

using System.Text;
using System.Text.RegularExpressions;

internal sealed record LuaBundleOptions(
    FileInfo EntryScript,
    IReadOnlyList<FileInfo> IncludeFiles,
    IReadOnlyList<FileInfo> StartupFiles);

internal sealed record LuaBundle(
    string Text,
    IReadOnlyList<string> ModuleKeys);

internal sealed class LuaBundleBuilder
{
    private readonly Dictionary<string, LuaScriptNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LuaScriptNode> _ordered = [];
    private DirectoryInfo _root = null!;

    public async Task<LuaBundle> BuildAsync(LuaBundleOptions options, CancellationToken cancellationToken)
    {
        _root = options.EntryScript.Directory ?? throw new InvalidOperationException("Entry script must have a directory.");
        _nodes.Clear();
        _ordered.Clear();

        foreach (var file in options.StartupFiles)
        {
            await VisitFileAsync(file, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in options.IncludeFiles)
        {
            await VisitFileAsync(file, cancellationToken).ConfigureAwait(false);
        }

        await VisitFileAsync(options.EntryScript, cancellationToken).ConfigureAwait(false);

        return new LuaBundle(EmitBundle(GetModuleKey(options.EntryScript)), _ordered.Select(n => n.Key).ToArray());
    }

    private async Task VisitFileAsync(FileInfo file, CancellationToken cancellationToken)
    {
        if (!file.Exists)
        {
            throw new FileNotFoundException($"[sf-build] Lua file not found: {file.FullName}", file.FullName);
        }

        if (!file.Extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"[sf-build] Lua file must use .lua extension: {file.FullName}");
        }

        var fullPath = Path.GetFullPath(file.FullName);
        if (_nodes.TryGetValue(fullPath, out var existing))
        {
            if (existing.State == VisitState.Visiting)
            {
                throw new InvalidOperationException($"[sf-build] cyclic Lua dependency detected at {existing.Key}");
            }

            return;
        }

        var source = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var node = new LuaScriptNode(new FileInfo(fullPath), GetModuleKey(file), source);
        _nodes.Add(fullPath, node);
        node.State = VisitState.Visiting;

        foreach (var dependency in ExtractDependencies(source))
        {
            var dependencyFile = ResolveDependency(dependency, Path.GetDirectoryName(fullPath)!);
            await VisitFileAsync(dependencyFile, cancellationToken).ConfigureAwait(false);
        }

        node.State = VisitState.Visited;
        _ordered.Add(node);
    }

    private IEnumerable<string> ExtractDependencies(string source)
    {
        foreach (var dependency in LuaSourceScanner.ExtractDependencies(source))
        {
            yield return dependency;
        }
    }

    private FileInfo ResolveDependency(string dependency, string currentDirectory)
    {
        var normalized = dependency.Replace('\\', '/');
        string path;
        if (Path.IsPathRooted(dependency))
        {
            path = dependency;
        }
        else if (normalized.StartsWith("./", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal))
        {
            path = Path.Combine(currentDirectory, normalized);
        }
        else
        {
            var module = normalized.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : normalized.Replace('.', '/') + ".lua";
            path = Path.Combine(_root.FullName, module);
        }

        if (!path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            path += ".lua";
        }

        return new FileInfo(Path.GetFullPath(path));
    }

    private string GetModuleKey(FileInfo file)
    {
        var fullPath = Path.GetFullPath(file.FullName);
        var rootPath = Path.GetFullPath(_root.FullName);
        if (!rootPath.EndsWith(Path.DirectorySeparatorChar))
        {
            rootPath += Path.DirectorySeparatorChar;
        }

        string relative;
        if (fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            relative = Path.GetRelativePath(_root.FullName, fullPath);
        }
        else
        {
            relative = file.Name;
        }

        // Lua-bundler-compatible key: drop .lua extension, slashes → dots.
        if (relative.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            relative = relative[..^4];
        }
        return relative.Replace('\\', '.').Replace('/', '.');
    }

    private string EmitBundle(string entryModuleKey)
    {
        // Polyfill block: lua-bundler-compatible loader-table layout so module
        // bodies match byte-for-byte across toolchains. Differences vs lua-bundler
        // are limited to this polyfill block + module-table name.
        var sb = new StringBuilder();
        sb.AppendLine("local __sf_modules = {}");
        sb.AppendLine("local require = function(path)");
        sb.AppendLine("    local module = __sf_modules[path]");
        sb.AppendLine("    if module == nil then");
        sb.AppendLine("        local dotPath = string.gsub(path, \"/\", \".\")");
        sb.AppendLine("        module = __sf_modules[dotPath]");
        sb.AppendLine("        __sf_modules[path] = module");
        sb.AppendLine("    end");
        sb.AppendLine("    if module ~= nil then");
        sb.AppendLine("        if not module.inited then");
        sb.AppendLine("            module.cached = module.loader()");
        sb.AppendLine("            module.inited = true");
        sb.AppendLine("        end");
        sb.AppendLine("        return module.cached");
        sb.AppendLine("    else");
        sb.AppendLine("        error(\"module not found \" .. path)");
        sb.AppendLine("        return nil");
        sb.AppendLine("    end");
        sb.AppendLine("end");
        sb.AppendLine();

        // All discovered Lua files (entry + startup + transitive deps) are
        // registered as modules first, then the entry module is loaded.
        foreach (var node in _ordered)
        {
            sb.Append("__sf_modules[").Append(ToLuaString(node.Key)).AppendLine("]={loader=function()");
            sb.AppendLine(node.Source.TrimEnd());
            sb.AppendLine("end}");
            sb.AppendLine();
        }

        sb.Append("require(").Append(ToLuaString(entryModuleKey)).AppendLine(")");

        return sb.ToString();
    }

    private static string ToLuaString(string value)
        => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private sealed class LuaScriptNode(FileInfo file, string key, string source)
    {
        public FileInfo File { get; } = file;
        public string Key { get; } = key;
        public string Source { get; } = source;
        public VisitState State { get; set; }
    }

    private enum VisitState
    {
        Visiting,
        Visited,
    }
}
