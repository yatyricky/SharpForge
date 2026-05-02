using System.CommandLine;
using System.Text;
using SharpForge.Builder.Cli;
using SharpForge.Builder.Inject;
using SharpForge.Builder.Pack;
using War3Net.IO.Mpq;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public sealed class BuilderTests
{
    [Fact]
    public async Task Pack_follows_literal_dependencies_and_ignores_plain_comments()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        Directory.CreateDirectory(Path.Combine(dir.FullName, "Lib"));
        Directory.CreateDirectory(Path.Combine(dir.FullName, "Slash"));

        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Main.lua"), """
            -- require('Unused')
            -- !require('Forced')
            local text = "require('StringOnly')"
            local a = require("Lib/A")
            dofile('Side')
            require 'Slash/Module'
            print(a)
            """);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Lib", "A.lua"), """
            local b = require('Lib.B')
            return b
            """);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Lib", "B.lua"), "return 'b'\n");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Side.lua"), "print('side')\n");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Forced.lua"), "print('forced')\n");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Unused.lua"), "print('unused')\n");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "StringOnly.lua"), "print('string only')\n");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Slash", "Module.lua"), "print('slash')\n");

        var exitCode = await new LuaPacker().RunAsync(new PackOptions(
            new FileInfo(Path.Combine(dir.FullName, "Main.lua")),
            OutputFile: null,
            IncludePaths: Array.Empty<string>(),
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var bundle = await File.ReadAllTextAsync(Path.Combine(dir.FullName, "bundle.lua"));
        Assert.Contains("local __sf_modules = {}", bundle);
        Assert.Contains("__sf_modules[\"Forced\"]={loader=function()", bundle);
        Assert.Contains("__sf_modules[\"Lib.B\"]={loader=function()", bundle);
        Assert.Contains("__sf_modules[\"Lib.A\"]={loader=function()", bundle);
        Assert.Contains("__sf_modules[\"Side\"]={loader=function()", bundle);
        Assert.Contains("__sf_modules[\"Slash.Module\"]={loader=function()", bundle);
        Assert.Contains("__sf_modules[\"Main\"]={loader=function()", bundle);
        Assert.Contains("require(\"Main\")", bundle);
        Assert.DoesNotContain("dofile(\"Main\")", bundle);
        Assert.DoesNotContain("__sf_modules[\"Unused\"]", bundle);
        Assert.DoesNotContain("__sf_modules[\"StringOnly\"]", bundle);
        Assert.True(bundle.IndexOf("__sf_modules[\"Lib.B\"]", StringComparison.Ordinal) < bundle.IndexOf("__sf_modules[\"Lib.A\"]", StringComparison.Ordinal));
        Assert.True(bundle.IndexOf("__sf_modules[\"Lib.A\"]", StringComparison.Ordinal) < bundle.IndexOf("__sf_modules[\"Main\"]", StringComparison.Ordinal));
        Assert.True(bundle.IndexOf("__sf_modules[\"Main\"]", StringComparison.Ordinal) < bundle.IndexOf("require(\"Main\")", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pack_include_adds_manual_files_for_dynamic_dependencies()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Main.lua"), "local name = 'Manual'; require(name)\n");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Manual.lua"), "return 1\n");

        var exitCode = await new LuaPacker().RunAsync(new PackOptions(
            new FileInfo(Path.Combine(dir.FullName, "Main.lua")),
            OutputFile: null,
            IncludePaths: new[] { "Manual.lua" },
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var bundle = await File.ReadAllTextAsync(Path.Combine(dir.FullName, "bundle.lua"));
        Assert.Contains("__sf_modules[\"Manual\"]={loader=function()", bundle);
        Assert.True(bundle.IndexOf("__sf_modules[\"Manual\"]", StringComparison.Ordinal) < bundle.IndexOf("__sf_modules[\"Main\"]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pack_writes_bundle_lua_to_non_w3x_target_folder()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        var target = Directory.CreateDirectory(Path.Combine(dir.FullName, "dist"));
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Main.lua"), "print('main')\n");

        var exitCode = await new LuaPacker().RunAsync(new PackOptions(
            new FileInfo(Path.Combine(dir.FullName, "Main.lua")),
            OutputFile: new FileInfo(target.FullName),
            IncludePaths: Array.Empty<string>(),
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(target.FullName, "bundle.lua")));
    }

    [Fact]
    public async Task Pack_injects_w3x_target_folder_with_trailing_separator()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        var target = Directory.CreateDirectory(Path.Combine(dir.FullName, "map.w3x"));
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Main.lua"), "print('main')\n");
        var scriptPath = Path.Combine(target.FullName, "war3map.lua");
        await File.WriteAllTextAsync(scriptPath, """
            function main()
                print('editor')
            end
            """);

        var exitCode = await new LuaPacker().RunAsync(new PackOptions(
            new FileInfo(Path.Combine(dir.FullName, "Main.lua")),
            OutputFile: new FileInfo(target.FullName + Path.DirectorySeparatorChar),
            IncludePaths: Array.Empty<string>(),
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var result = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains("function SF__Bundle()", result);
        Assert.Contains("print('main')", result);
        Assert.Contains("require(\"Main\")", result);
        Assert.Contains("print('editor')", result);
        Assert.Contains("local s, m = pcall(SF__Bundle)", result);
        Assert.True(result.IndexOf("require(\"Main\")", StringComparison.Ordinal) < result.IndexOf("function main()", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(target.FullName, "bundle.lua")));
    }

    [Fact]
    public async Task Pack_rejects_non_w3x_target_file()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Main.lua"), "print('main')\n");
        var target = Path.Combine(dir.FullName, "target.txt");
        await File.WriteAllTextAsync(target, "not a map");

        var exitCode = await new LuaPacker().RunAsync(new PackOptions(
            new FileInfo(Path.Combine(dir.FullName, "Main.lua")),
            OutputFile: new FileInfo(target),
            IncludePaths: Array.Empty<string>(),
            Verbose: false), CancellationToken.None);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Cli_returns_nonzero_when_packer_rejects_target()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Main.lua"), "print('main')\n");
        var missingMap = Path.Combine(dir.FullName, "missing.w3x");

        var exitCode = await RootCommandFactory.Create().InvokeAsync([
            Path.Combine(dir.FullName, "Main.lua"),
            "-o",
            missingMap,
        ]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Pack_injects_existing_war3map_lua_output_file()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Main.lua"), "print('main')\n");
        var scriptPath = Path.Combine(dir.FullName, "war3map.lua");
        await File.WriteAllTextAsync(scriptPath, """
            function main()
                print('editor')
            end
            """);

        var exitCode = await new LuaPacker().RunAsync(new PackOptions(
            new FileInfo(Path.Combine(dir.FullName, "Main.lua")),
            OutputFile: new FileInfo(scriptPath),
            IncludePaths: Array.Empty<string>(),
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var result = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains("function SF__Bundle()", result);
        Assert.Contains("print('main')", result);
        Assert.Contains("require(\"Main\")", result);
        Assert.Contains("print('editor')", result);
        Assert.Contains("local s, m = pcall(SF__Bundle)", result);
        Assert.True(result.IndexOf("require(\"Main\")", StringComparison.Ordinal) < result.IndexOf("function main()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Injector_injects_w3x_archive_copy_without_modifying_original()
    {
        var dir = Directory.CreateTempSubdirectory("sf-build-test-");
        var mapPath = Path.Combine(dir.FullName, "map.w3x");
        await CreateArchiveAsync(mapPath, "war3map.lua", """
            function main()
                print('editor')
            end
            """);
        var originalBytes = await File.ReadAllBytesAsync(mapPath);

        var exitCode = await new MapInjector().InjectBundleAsync(mapPath, "print('bundle')", CancellationToken.None);

        Assert.Equal(0, exitCode);
        var copyPath = MapInjector.GetArchiveCopyPath(mapPath);
        Assert.True(File.Exists(copyPath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(mapPath));
        Assert.NotEqual(originalBytes, await File.ReadAllBytesAsync(copyPath));

        using var archive = MpqArchive.Open(copyPath, loadListFile: true);
        using var stream = archive.OpenFile("war3map.lua");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var injected = await reader.ReadToEndAsync();
        Assert.Contains("function SF__Bundle()", injected);
        Assert.Contains("print('bundle')", injected);
        Assert.Contains("print('editor')", injected);
    }

    [Fact]
    public async Task Injector_splices_bundle_at_end_of_main_and_replaces_previous_bundle()
    {
        var map = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("sf-build-test-").FullName, "map.w3x"));
        var scriptPath = Path.Combine(map.FullName, "war3map.lua");
        await File.WriteAllTextAsync(scriptPath, """
            function helper()
            end

            function main()
                print('editor')
            end
            """);

        var injector = new MapInjector();
        var first = await injector.InjectBundleAsync(map.FullName, "print('first')", CancellationToken.None);
        var second = await injector.InjectBundleAsync(map.FullName, "print('second')", CancellationToken.None);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        var result = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains("print('editor')", result);
        Assert.Contains("print('second')", result);
        Assert.Contains("function SF__Bundle()", result);
        Assert.Contains("local s, m = pcall(SF__Bundle)", result);
        Assert.Matches(@"--sf-builder:\d{9}/[0-9a-f]{16}", result);
        Assert.DoesNotContain("print('first')", result);
        Assert.True(result.IndexOf("function SF__Bundle()", StringComparison.Ordinal) < result.IndexOf("function main()", StringComparison.Ordinal));
        Assert.True(result.IndexOf("print('editor')", StringComparison.Ordinal) < result.IndexOf("pcall(SF__Bundle)", StringComparison.Ordinal));
        Assert.True(result.IndexOf("pcall(SF__Bundle)", StringComparison.Ordinal) < result.LastIndexOf("end", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Injector_replaces_non_ascii_bundle_without_duplicating_wrapper()
    {
        var map = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("sf-build-test-").FullName, "map.w3x"));
        var scriptPath = Path.Combine(map.FullName, "war3map.lua");
        await File.WriteAllTextAsync(scriptPath, """
            function main()
                print('editor')
            end
            """);

        var injector = new MapInjector();
        var first = await injector.InjectBundleAsync(map.FullName, $"print('{new string('界', 64)}')", CancellationToken.None);
        var second = await injector.InjectBundleAsync(map.FullName, "print('second')", CancellationToken.None);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        var result = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains("function main()", result);
        Assert.Contains("print('second')", result);
        Assert.DoesNotContain(new string('界', 64), result);
        Assert.Equal(2, CountOccurrences(result, "--sf-builder:"));
        Assert.Equal(1, CountOccurrences(result, "function SF__Bundle()"));
        Assert.Equal(1, CountOccurrences(result, "pcall(SF__Bundle)"));
    }

    [Fact]
    public async Task Injector_repairs_dangling_generated_prefix_from_previous_reinjection_bug()
    {
        var map = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("sf-build-test-").FullName, "map.w3x"));
        var scriptPath = Path.Combine(map.FullName, "war3map.lua");
        await File.WriteAllTextAsync(scriptPath, """
            --sf-builder:000000001/0123456789abcdef
            function SF__Bundle()
            print('old')
            end
            --sf-builder:000000001/0123456789abcdef
            dangling old wrapper tail
            --sf-builder:000000002/0123456789abcdef
            function InitGlobals()
            end

            function main()
                local s, m = pcall(SF__Bundle)
                if not s then
                    print(m)
                end
                local s, m = pcall(SF__Bundle)
                if not s then
                    print(m)
                end
                print('editor')
            end
            """);

        var exitCode = await new MapInjector().InjectBundleAsync(map.FullName, "print('new')", CancellationToken.None);

        Assert.Equal(0, exitCode);
        var result = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains("print('new')", result);
        Assert.Contains("function InitGlobals()", result);
        Assert.Contains("print('editor')", result);
        Assert.DoesNotContain("dangling old wrapper tail", result);
        Assert.DoesNotContain("print('old')", result);
        Assert.Equal(2, CountOccurrences(result, "--sf-builder:"));
        Assert.Equal(1, CountOccurrences(result, "function SF__Bundle()"));
        Assert.Equal(1, CountOccurrences(result, "pcall(SF__Bundle)"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static async Task CreateArchiveAsync(string mapPath, string scriptName, string script)
    {
        var bytes = Encoding.UTF8.GetBytes(script);
        var listFileBytes = Encoding.UTF8.GetBytes(scriptName + "\r\n");
        await using var scriptStream = new MemoryStream(bytes);
        await using var listFileStream = new MemoryStream(listFileBytes);
        using var file = MpqFile.New(scriptStream, scriptName);
        using var listFile = MpqFile.New(listFileStream, "(listfile)");
        file.TargetFlags = MpqFileFlags.Exists | MpqFileFlags.CompressedMulti;
        listFile.TargetFlags = MpqFileFlags.Exists | MpqFileFlags.CompressedMulti;
        using var archive = MpqArchive.Create(mapPath, [file, listFile], new MpqArchiveCreateOptions());
    }
}
