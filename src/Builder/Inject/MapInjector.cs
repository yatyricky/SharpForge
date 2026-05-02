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

            return await InjectArchiveCopyAsync(mapPath, bundle, cancellationToken).ConfigureAwait(false);
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

        var (cleaned, _, hadLegacyWrapper) = StripLeadingWrapper(war3MapLua);
        var wrapper = WrapBundle(bundle, newline);

        // Clean file, re-injection, or migration from lua-bundler. Walk lines
        // and splice exactly one pcall(SF__Bundle) before main()'s closing
        // `end`, dropping any generated pcall blocks already present.
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

            if (insideMain && IsSharpForgePcallStart(line))
            {
                var skipped = TrySkipPcallBlock(lines, i);
                if (skipped > 0)
                {
                    i += skipped;
                    continue;
                }
            }

            if (insideMain && removeLegacyPcall && IsLegacyPcallStart(line))
            {
                var skipped = TrySkipPcallBlock(lines, i);
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

    private static bool IsSharpForgePcallStart(string line)
        => Regex.IsMatch(line, @"^\s*local\s+s\s*,\s*m\s*=\s*pcall\(SF__Bundle\)\s*$", RegexOptions.CultureInvariant);

    private static int TrySkipPcallBlock(string[] lines, int start)
    {
        // Block: local s, m = pcall(...) / if not s then / print(m) / end
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
        var cleaned = source;
        var hadOurs = false;
        var hadLegacy = false;

        while (true)
        {
            if (TryStripWrapper(cleaned, MarkerPrefix, out var stripped))
            {
                cleaned = stripped;
                hadOurs = true;
                continue;
            }

            if (TryStripWrapper(cleaned, LegacyMarkerPrefix, out stripped))
            {
                cleaned = stripped;
                hadLegacy = true;
                continue;
            }

            if (hadOurs || hadLegacy)
            {
                cleaned = StripDanglingInjectedPrefix(cleaned);
            }

            return (cleaned, hadOurs, hadLegacy);
        }
    }

    private static bool TryStripWrapper(string source, string prefix, out string stripped)
    {
        stripped = source;
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var firstLineEnd = IndexOfLineEnding(source, 0);
        if (firstLineEnd < 0)
        {
            return false;
        }

        var markerLine = source[..firstLineEnd].TrimEnd('\r');
        if (!IsValidMarkerLine(markerLine, prefix))
        {
            return false;
        }

        var searchStart = SkipLineEnding(source, firstLineEnd);
        var closingStart = FindMarkerLine(source, markerLine, searchStart);
        if (closingStart < 0)
        {
            return false;
        }

        var closingEnd = SkipLineEnding(source, closingStart + markerLine.Length);
        stripped = source[closingEnd..];
        return true;
    }

    private static bool IsValidMarkerLine(string markerLine, string prefix)
    {
        if (markerLine.Length != prefix.Length + LengthDigits + 1 + ChecksumChars)
        {
            return false;
        }

        if (markerLine[prefix.Length + LengthDigits] != '/')
        {
            return false;
        }

        return int.TryParse(markerLine.AsSpan(prefix.Length, LengthDigits), out _);
    }

    private static int FindMarkerLine(string source, string markerLine, int searchStart)
    {
        var index = searchStart;
        while (index < source.Length)
        {
            var match = source.IndexOf(markerLine, index, StringComparison.Ordinal);
            if (match < 0)
            {
                return -1;
            }

            if (match == 0 || source[match - 1] == '\n')
            {
                var afterMarker = match + markerLine.Length;
                if (afterMarker == source.Length || source[afterMarker] is '\r' or '\n')
                {
                    return match;
                }
            }

            index = match + markerLine.Length;
        }

        return -1;
    }

    private static int IndexOfLineEnding(string source, int start)
    {
        for (var i = start; i < source.Length; i++)
        {
            if (source[i] is '\r' or '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int SkipLineEnding(string source, int index)
    {
        if (index < source.Length && source[index] == '\r')
        {
            index++;
        }

        if (index < source.Length && source[index] == '\n')
        {
            index++;
        }

        return index;
    }

    private static string StripDanglingInjectedPrefix(string source)
    {
        var mainIndex = source.IndexOf("function main()", StringComparison.Ordinal);
        var searchLimit = mainIndex >= 0 ? mainIndex : source.Length;
        var stripEnd = Math.Max(
            FindLastMarkerLineEnd(source, MarkerPrefix, searchLimit),
            FindLastMarkerLineEnd(source, LegacyMarkerPrefix, searchLimit));

        return stripEnd > 0 ? source[stripEnd..] : source;
    }

    private static int FindLastMarkerLineEnd(string source, string prefix, int searchLimit)
    {
        var lastEnd = -1;
        var index = 0;
        while (index < searchLimit)
        {
            var match = source.IndexOf(prefix, index, StringComparison.Ordinal);
            if (match < 0 || match >= searchLimit)
            {
                return lastEnd;
            }

            if (match == 0 || source[match - 1] == '\n')
            {
                var lineEnd = IndexOfLineEnding(source, match);
                if (lineEnd < 0)
                {
                    lineEnd = source.Length;
                }

                var markerLine = source[match..lineEnd].TrimEnd('\r');
                if (IsValidMarkerLine(markerLine, prefix))
                {
                    lastEnd = SkipLineEnding(source, lineEnd);
                }
            }

            index = match + prefix.Length;
        }

        return lastEnd;
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

    private static async Task<int> InjectArchiveCopyAsync(string mapFile, string bundle, CancellationToken cancellationToken)
    {
        var copyPath = GetArchiveCopyPath(mapFile);
        try
        {
            File.Copy(mapFile, copyPath, overwrite: true);
            var exitCode = await InjectArchiveAsync(copyPath, bundle, cancellationToken).ConfigureAwait(false);
            if (exitCode == 0)
            {
                Console.WriteLine($"[sf-build] wrote map copy: {copyPath}");
            }

            return exitCode;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<int> InjectArchiveAsync(string mapFile, string bundle, CancellationToken cancellationToken)
    {
        var tempPath = mapFile + ".sf-tmp";
        try
        {
            var mapBytes = await File.ReadAllBytesAsync(mapFile, cancellationToken).ConfigureAwait(false);
            using (var archiveStream = new MemoryStream(mapBytes, writable: false))
            using (var archive = MpqArchive.Open(archiveStream, loadListFile: true))
            {
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
                builder.AddFile(newFile);

                builder.SaveTo(tempPath);
            }

            File.Copy(tempPath, mapFile, overwrite: true);
            File.Delete(tempPath);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
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
        => Path.GetExtension(Path.TrimEndingDirectorySeparator(path)).Equals(".w3x", StringComparison.OrdinalIgnoreCase);

    private static bool IsWar3MapLuaPath(string path)
        => Path.GetFileName(path).Equals(War3MapLua, StringComparison.OrdinalIgnoreCase);

    internal static string GetArchiveCopyPath(string mapFile)
    {
        var directory = Path.GetDirectoryName(mapFile) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(mapFile);
        var extension = Path.GetExtension(mapFile);
        return Path.Combine(directory, fileName + ".sf-build" + extension);
    }
}
