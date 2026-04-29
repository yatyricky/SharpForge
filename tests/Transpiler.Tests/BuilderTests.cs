using SharpForge.Builder.Inject;
using SharpForge.Builder.Pack;
using SharpForge.Transpiler.Pipeline;
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
            CSharpInputDirectory: null,
            RootTable: TranspileOptions.DefaultRootTable,
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
        Assert.DoesNotContain("dofile(\"Main\")", bundle);
        Assert.DoesNotContain("__sf_modules[\"Unused\"]", bundle);
        Assert.DoesNotContain("__sf_modules[\"StringOnly\"]", bundle);
        Assert.True(bundle.IndexOf("__sf_modules[\"Lib.B\"]", StringComparison.Ordinal) < bundle.IndexOf("__sf_modules[\"Lib.A\"]", StringComparison.Ordinal));
        Assert.True(bundle.IndexOf("__sf_modules[\"Lib.A\"]", StringComparison.Ordinal) < bundle.IndexOf("__sf_modules[\"Main\"]", StringComparison.Ordinal));
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
            CSharpInputDirectory: null,
            RootTable: TranspileOptions.DefaultRootTable,
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
            CSharpInputDirectory: null,
            RootTable: TranspileOptions.DefaultRootTable,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(target.FullName, "bundle.lua")));
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
            CSharpInputDirectory: null,
            RootTable: TranspileOptions.DefaultRootTable,
            Verbose: false), CancellationToken.None);

        Assert.Equal(2, exitCode);
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
}
