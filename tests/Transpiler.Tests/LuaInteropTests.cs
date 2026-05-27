using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class LuaInteropTests
{
    [Fact]
    public async Task Pipeline_lowers_bundled_SFLib_lua_interop_to_raw_lua_access()
    {
        var src = """
            using SFLib.Interop;

            public class Program
            {
                public static void Main(string[] args)
                {
                    LuaObject table = LuaInterop.CreateTable();
                    LuaObject frameTimer = LuaInterop.Require("Lib.FrameTimer");
                    LuaObject systems = LuaInterop.Require("System.ItemSystem");
                    LuaObject created = LuaInterop.Call<LuaObject>(systems, "new", 7);
                    int charges = LuaInterop.Get<int>(created, "charges");

                    LuaInterop.Set(created, "charges", charges + 1);
                    LuaInterop.SetGlobal("CurrentItemSystem", created);
                    LuaInterop.Call(frameTimer, "start", created);
                    LuaInterop.CallMethod(created, "Method", 1, "abc");
                    LuaInterop.CallGlobal("BJDebugMsg", "ready");
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "Program.cs");

        Assert.Equal(0, exitCode);
        Assert.Contains("local KW__table = {}", lua);
        Assert.Contains("local frameTimer = require(\"Lib.FrameTimer\")", lua);
        Assert.Contains("local systems = require(\"System.ItemSystem\")", lua);
        Assert.Contains("local created = systems.new(7)", lua);
        Assert.Contains("local charges = created.charges", lua);
        Assert.Contains("created.charges = (charges + 1)", lua);
        Assert.Contains("CurrentItemSystem = created", lua);
        Assert.Contains("frameTimer.start(created)", lua);
        Assert.Contains("created:Method(1, \"abc\")", lua);
        Assert.Contains("BJDebugMsg(\"ready\")", lua);
        Assert.DoesNotContain("-- SFLib.Interop.LuaInterop", lua);
        Assert.DoesNotContain("SF__.SFLib.Interop.LuaInterop", lua);
    }

    [Fact]
    public async Task Pipeline_lowers_lua_object_wrappers_to_bound_lua_module_calls()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var wrapperDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "Lua"));
        await File.WriteAllTextAsync(Path.Combine(wrapperDir.FullName, "FrameTimer.cs"), """
            using System;
            using SFLib.Interop;

            namespace Lua;

            [Lua(Module = "Lib.FrameTimer")]
            public class FrameTimer : LuaObject
            {
                [Lua(StaticMethod = "new")]
                public FrameTimer(Action<float> func, int count, int loops) => throw new NotImplementedException();

                public static LuaObject PauseAll() => throw new NotImplementedException();

                [Lua(StaticMethod = "fromHandle")]
                public static FrameTimer FromHandle(LuaObject handle) => throw new NotImplementedException();

                public void Start() => throw new NotImplementedException();

                [Lua(Method = "resume_now")]
                public void Resume() => throw new NotImplementedException();

                [Lua(Name = "stop_now")]
                public void Stop() => throw new NotImplementedException();
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(wrapperDir.FullName, "Time.cs"), """
            using SFLib.Interop;

            namespace Lua;

            [Lua(Module = "Lib.Time")]
            public class Time : LuaObject
            {
                [Lua(Name = "Time")]
                public static float CurrentTime;
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using Lua;
            using SFLib.Interop;

            public class Program
            {
                public static void Main()
                {
                    LuaInterop.SetGlobal("CLI", LuaInterop.CreateTable());
                    var handle = LuaInterop.CreateTable();
                    var fromHandle = FrameTimer.FromHandle(handle);
                    var paused = FrameTimer.PauseAll();
                    var timer = new FrameTimer(dt => LuaInterop.CallGlobal("Tick", Time.CurrentTime + dt), 1, -1);
                    timer.Start();
                    timer.Resume();
                    timer.Stop();
                }
            }
            """);

        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: output,
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.True(lua.IndexOf("CLI = {}", StringComparison.Ordinal) < lua.IndexOf("local FrameTimer = require(\"Lib.FrameTimer\")", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local FrameTimer = require(\"Lib.FrameTimer\")", StringComparison.Ordinal) < lua.IndexOf("local Time = require(\"Lib.Time\")", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local Time = require(\"Lib.Time\")", StringComparison.Ordinal) < lua.IndexOf("local fromHandle = FrameTimer.fromHandle(handle)", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local Time = require(\"Lib.Time\")", StringComparison.Ordinal) < lua.IndexOf("local handle = {}", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("local FrameTimer = require(\"Lib.FrameTimer\")", StringComparison.Ordinal) < lua.IndexOf("local fromHandle = FrameTimer.fromHandle(handle)", StringComparison.Ordinal));
        Assert.Contains("local FrameTimer = require(\"Lib.FrameTimer\")", lua);
        Assert.Contains("local Time = require(\"Lib.Time\")", lua);
        Assert.Contains("local fromHandle = FrameTimer.fromHandle(handle)", lua);
        Assert.Contains("local paused = FrameTimer.PauseAll()", lua);
        Assert.Contains("local timer = FrameTimer.new(function(dt)", lua);
        Assert.Contains("Tick((Time.Time + dt))", lua);
        Assert.DoesNotContain("Time.CurrentTime", lua);
        Assert.Contains("timer:Start()", lua);
        Assert.Contains("timer:resume_now()", lua);
        Assert.Contains("timer:stop_now()", lua);
        Assert.DoesNotContain("SF__.Lua.FrameTimer", lua);
        Assert.DoesNotContain("-- Lua.FrameTimer", lua);
    }

    [Fact]
    public async Task Pipeline_emits_lua_class_for_opted_in_lua_object_subclass()
    {
        var dir = Directory.CreateTempSubdirectory("sf-test-");
        var wrapperDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "libs", "LuaWrapper"));
        var systemsDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "Systems"));
        await File.WriteAllTextAsync(Path.Combine(wrapperDir.FullName, "SystemBase.cs"), """
            using SFLib.Interop;

            namespace LuaWrapper;

            [Lua(Module = "System.SystemBase")]
            public class SystemBase : LuaObject
            {
                [Lua(StaticMethod = "new")]
                public SystemBase() { }

                public virtual void Awake() { }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(systemsDir.FullName, "InitAbilitiesSystem.cs"), """
            using LuaWrapper;
            using SFLib.Interop;

            namespace Systems;

            [Lua(Class = "InitAbilitiesSystem")]
            public class InitAbilitiesSystem : SystemBase
            {
                public override void Awake()
                {
                    LuaInterop.Require("Ability.Evasion");
                }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(dir.FullName, "Program.cs"), """
            using Systems;

            public class Program
            {
                public static void Main()
                {
                    var system = new InitAbilitiesSystem();
                    system.Awake();
                }
            }
            """);

        var output = new FileInfo(Path.Combine(dir.FullName, "out.lua"));
        var exitCode = await new TranspilePipeline().RunAsync(new TranspileOptions(
            InputDirectory: dir,
            OutputFile: output,
            PreprocessorSymbols: Array.Empty<string>(),
            RootTable: TranspileOptions.DefaultRootTable,
            IgnoredClasses: new[] { TranspileOptions.DefaultIgnoredClass },
            IgnoredNamespaces: new[] { TranspileOptions.DefaultIgnoredNamespace },
            CheckOnly: false,
            Verbose: false), CancellationToken.None);

        Assert.Equal(0, exitCode);
        var lua = await File.ReadAllTextAsync(output.FullName);
        Assert.Contains("local SystemBase = require(\"System.SystemBase\")", lua);
        Assert.Contains("SF__.Systems.InitAbilitiesSystem = SF__.Systems.InitAbilitiesSystem or class(\"InitAbilitiesSystem\", SystemBase)", lua);
        Assert.Contains("SF__.Systems.InitAbilitiesSystem.__sf_base = SystemBase", lua);
        Assert.Contains("function SF__.Systems.InitAbilitiesSystem:Awake()", lua);
        Assert.Contains("require(\"Ability.Evasion\")", lua);
        Assert.Contains("function SF__.Systems.InitAbilitiesSystem.__Init(self)", lua);
        Assert.Contains("function SF__.Systems.InitAbilitiesSystem.New()", lua);
        Assert.Contains("local self = SF__.Systems.InitAbilitiesSystem.new()", lua);
        Assert.Contains("local system = SF__.Systems.InitAbilitiesSystem.New()", lua);
        Assert.Contains("system:Awake()", lua);
        Assert.DoesNotContain("SF__.LuaWrapper.SystemBase", lua);
    }

    [Fact]
    public async Task Pipeline_emits_class_level_lua_requires_before_type_table()
    {
        var src = """
            using SFLib.Interop;

            [Lua(Require = "Lib.class")]
            [Lua(Require = "Lib.maths")]
            public class EntryClass
            {
                public static void Main()
                {
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "EntryClass.cs");

        Assert.Equal(0, exitCode);
        Assert.True(lua.IndexOf("require(\"Lib.class\")", StringComparison.Ordinal) < lua.IndexOf("require(\"Lib.maths\")", StringComparison.Ordinal));
        Assert.True(lua.IndexOf("require(\"Lib.maths\")", StringComparison.Ordinal) < lua.IndexOf("SF__.EntryClass = SF__.EntryClass or {}", StringComparison.Ordinal));
        Assert.Contains("function SF__.EntryClass.Main()", lua);
    }

    [Fact]
    public async Task TableLiteral_class_lowers_constructor_call_to_table_literal()
    {
        var src = """
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                using System;
                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public bool TableLiteral;
                }
            }

            public static partial class JASS
            {
                public static object GetCastingUnit() => throw null!;
                public static int FourCC(string s) => throw null!;
            }

            [Lua(TableLiteral = true)]
            public class Shape
            {
                public object caster;
                public int id;
                public Shape(object caster, int id) { }
            }

            public static class Demo
            {
                public static void Post(object shape) { }

                public static void Main()
                {
                    Post(new Shape(JASS.GetCastingUnit(), JASS.FourCC("A001")));
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "TableLiteral.cs");

        Assert.Contains("{caster = GetCastingUnit(), id = FourCC(\"A001\")}", lua);
        Assert.DoesNotContain("SF__.Shape", lua);
        Assert.DoesNotContain("Shape.New", lua);
    }

    [Fact]
    public async Task TableLiteral_class_lowers_object_initializer_to_table_literal()
    {
        var src = """
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                using System;
                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public bool TableLiteral;
                }

                public class LuaObject
                {
                }
            }

            public static partial class JASS
            {
                public static object GetCastingUnit() => throw null!;
                public static int FourCC(string s) => throw null!;
            }

            [Lua(TableLiteral = true)]
            public class IRegisterSpellEvent : LuaObject
            {
                public int id;
                public object handler;
                public LuaObject ctx;
            }

            public static class Demo
            {
                public static void Emit(object ev) { }

                public static void Main()
                {
                    Emit(new IRegisterSpellEvent
                    {
                        id = JASS.FourCC("A001"),
                        handler = JASS.GetCastingUnit(),
                    });
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "TableLiteralObjectInitializer.cs");

        Assert.Contains("{id = FourCC(\"A001\"), handler = GetCastingUnit()}", lua);
        Assert.DoesNotContain("IRegisterSpellEvent.New", lua);
    }
}
