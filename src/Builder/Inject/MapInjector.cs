namespace SharpForge.Builder.Inject;

using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using War3Net.IO.Mpq;

/// <summary>
/// Injects a bundled Lua script into a map's <c>war3map.lua</c>.
/// </summary>
public sealed class MapInjector
{
    private const string War3MapLua = "war3map.lua";
    private const string ScriptsWar3MapLua = "scripts\\war3map.lua";
    private const string MarkerPrefix = "--sf-builder:";
    private const string LegacyMarkerPrefix = "--lua-bundler:";
    private const int LengthDigits = 9;
    private const int ChecksumChars = 16;

    public async Task<int> InjectBundleAsync(string mapPath, string bundle, CancellationToken cancellationToken)
    {
        if (Directory.Exists(mapPath))
        {
            if (!IsW3xPath(mapPath))
            {
                Console.Error.WriteLine($"[sf-build] target folder is not a .w3x map folder: {mapPath}");
                return 2;
            }

            return await InjectFolderAsync(mapPath, bundle, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(mapPath))
        {
            if (IsWar3MapLuaPath(mapPath))
            {
                return await InjectScriptFileAsync(mapPath, bundle, cancellationToken).ConfigureAwait(false);
            }

            if (!IsW3xPath(mapPath))
            {
                Console.Error.WriteLine($"[sf-build] target file is not a .w3x map: {mapPath}");
                return 2;
            }

            return await InjectArchiveAsync(mapPath, bundle, cancellationToken).ConfigureAwait(false);
        }

        if (IsWar3MapLuaPath(mapPath))
        {
            Console.Error.WriteLine($"[sf-build] war3map.lua not found: {mapPath}");
            return 2;
        }

        Console.Error.WriteLine($"[sf-build] target map not found: {mapPath}");
        return 2;
    }

    internal static string InjectIntoMain(string war3MapLua, string bundle)
    {
        var newline = war3MapLua.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var (cleaned, hadOurWrapper, hadLegacyWrapper) = StripLeadingWrapper(war3MapLua);
        var wrapper = WrapBundle(bundle, newline);

        if (hadOurWrapper)
        {
            // Re-injection: only swap the top wrapper, leave main() alone.
            return wrapper + cleaned;
        }

        // Clean file or migration from lua-bundler. Walk lines and splice
        // pcall(SF__Bundle) before main()'s closing `end`. When migrating,
        // also drop the existing pcall(RunBundle) block from main().
        var modified = ProcessMainFunction(cleaned, removeLegacyPcall: hadLegacyWrapper, newline);
        return wrapper + modified;
    }

    private static string ProcessMainFunction(string source, bool removeLegacyPcall, string newline)
    {
        // Split on LF only and preserve any trailing CR on each line so mixed
        // line endings (common in war3map.lua produced by other toolchains)
        // don't break exact-line matching.
        var lines = source.Split('\n');
        var sb = new StringBuilder(source.Length + 128);
        var insideMain = false;
        var foundMain = false;
        var i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\r');
            var isLast = i == lines.Length - 1;

            if (!insideMain && line == "function main()")
            {
                insideMain = true;
                foundMain = true;
                AppendRaw(sb, raw, isLast);
                i++;
                continue;
            }

            if (insideMain && removeLegacyPcall && IsLegacyPcallStart(line))
            {
                var skipped = TrySkipLegacyPcallBlock(lines, i);
                if (skipped > 0)
                {
                    i += skipped;
                    continue;
                }
            }

            if (insideMain && line == "end")
            {
                sb.Append("    local s, m = pcall(SF__Bundle)").Append(newline);
                sb.Append("    if not s then").Append(newline);
                sb.Append("        print(m)").Append(newline);
                sb.Append("    end").Append(newline);
                insideMain = false;
                AppendRaw(sb, raw, isLast);
                i++;
                continue;
            }

            AppendRaw(sb, raw, isLast);
            i++;
        }

        if (!foundMain)
        {
            throw new InvalidOperationException("[sf-build] function main() not found in war3map.lua.");
        }

        if (insideMain)
        {
            throw new InvalidOperationException("[sf-build] function main() was not closed in war3map.lua.");
        }

        return sb.ToString();
    }

    private static void AppendRaw(StringBuilder sb, string raw, bool isLast)
    {
        sb.Append(raw);
        if (!isLast)
        {
            sb.Append('\n');
        }
    }

    private static bool IsLegacyPcallStart(string line)
        => Regex.IsMatch(line, @"^\s*local\s+s\s*,\s*m\s*=\s*pcall\(RunBundle\)\s*$", RegexOptions.CultureInvariant);

    private static int TrySkipLegacyPcallBlock(string[] lines, int start)
    {
        // Block: local s, m = pcall(RunBundle) / if not s then / print(m) / end
        if (start + 3 >= lines.Length)
        {
            return 0;
        }

        if (!Regex.IsMatch(lines[start + 1].TrimEnd('\r'), @"^\s*if\s+not\s+s\s+then\s*$", RegexOptions.CultureInvariant)) return 0;
        if (!Regex.IsMatch(lines[start + 2].TrimEnd('\r'), @"^\s*print\(m\)\s*$", RegexOptions.CultureInvariant)) return 0;
        if (!Regex.IsMatch(lines[start + 3].TrimEnd('\r'), @"^\s*end\s*$", RegexOptions.CultureInvariant)) return 0;
        return 4;
    }

    private static (string cleaned, bool hadOurs, bool hadLegacy) StripLeadingWrapper(string source)
    {
        if (TryStripWrapper(source, MarkerPrefix, out var stripped))
        {
            return (stripped, true, false);
        }

        if (TryStripWrapper(source, LegacyMarkerPrefix, out stripped))
        {
            return (stripped, false, true);
        }

        return (source, false, false);
    }

    private static bool TryStripWrapper(string source, string prefix, out string stripped)
    {
        stripped = source;
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var tagStart = prefix.Length;
        if (source.Length < tagStart + LengthDigits + 1 + ChecksumChars)
        {
            return false;
        }

        var lengthSpan = source.AsSpan(tagStart, LengthDigits);
        if (!int.TryParse(lengthSpan, out var length) || length <= 0 || length > source.Length)
        {
            return false;
        }

        if (source[tagStart + LengthDigits] != '/')
        {
            return false;
        }

        stripped = source.Substring(length);
        return true;
    }


    private static async Task<int> InjectFolderAsync(string mapFolder, string bundle, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(mapFolder, War3MapLua);
        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"[sf-build] war3map.lua not found: {scriptPath}");
            return 2;
        }

        try
        {
            var original = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
            var injected = InjectIntoMain(original, bundle);
            await File.WriteAllTextAsync(scriptPath, injected, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<int> InjectScriptFileAsync(string scriptPath, string bundle, CancellationToken cancellationToken)
    {
        try
        {
            var original = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
            var injected = InjectIntoMain(original, bundle);
            await File.WriteAllTextAsync(scriptPath, injected, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<int> InjectArchiveAsync(string mapFile, string bundle, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = MpqArchive.Open(mapFile, loadListFile: true);
            var scriptName = TryReadArchiveFile(archive, War3MapLua, out var original)
                ? War3MapLua
                : TryReadArchiveFile(archive, ScriptsWar3MapLua, out original)
                    ? ScriptsWar3MapLua
                    : null;

            if (scriptName is null)
            {
                Console.Error.WriteLine("[sf-build] war3map.lua not found in .w3x archive.");
                return 2;
            }

            var injected = InjectIntoMain(original, bundle);
            var bytes = Encoding.UTF8.GetBytes(injected);
            await using var scriptStream = new MemoryStream(bytes);
            using var newFile = MpqFile.New(scriptStream, scriptName);
            newFile.TargetFlags = MpqFileFlags.Exists | MpqFileFlags.CompressedMulti;

            var builder = new MpqArchiveBuilder(archive);
            builder.RemoveFile(scriptName);
            builder.AddFile(newFile);

            var tempPath = mapFile + ".sf-tmp";
            builder.SaveTo(tempPath);
            archive.Dispose();
            File.Copy(tempPath, mapFile, overwrite: true);
            File.Delete(tempPath);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static bool TryReadArchiveFile(MpqArchive archive, string name, out string contents)
    {
        try
        {
            using var stream = archive.OpenFile(name);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            contents = reader.ReadToEnd();
            return true;
        }
        catch (FileNotFoundException)
        {
            contents = string.Empty;
            return false;
        }
    }

    internal static string WrapBundle(string bundle, string newline)
    {
        var body = $"function SF__Bundle(){newline}{bundle.TrimEnd()}{newline}end";
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bundle))).ToLowerInvariant()[..ChecksumChars];
        var placeholderTag = new string('0', LengthDigits) + "/" + checksum;
        var placeholder = $"{MarkerPrefix}{placeholderTag}{newline}{body}{newline}{MarkerPrefix}{placeholderTag}{newline}";
        var length = Encoding.UTF8.GetByteCount(placeholder);
        var tag = length.ToString().PadLeft(LengthDigits, '0') + "/" + checksum;
        return $"{MarkerPrefix}{tag}{newline}{body}{newline}{MarkerPrefix}{tag}{newline}";
    }

    private static bool IsW3xPath(string path)
        => Path.GetExtension(path).Equals(".w3x", StringComparison.OrdinalIgnoreCase);

    private static bool IsWar3MapLuaPath(string path)
        => Path.GetFileName(path).Equals(War3MapLua, StringComparison.OrdinalIgnoreCase);
}
