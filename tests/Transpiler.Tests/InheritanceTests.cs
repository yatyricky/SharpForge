using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class InheritanceTests
{
    [Fact]
    public async Task Inheritance_emits_base_metatable_constructor_and_base_method_calls()
    {
        var src = """
            namespace Game
            {
                public class Unit
                {
                    public int Hp;

                    public Unit(int hp)
                    {
                        Hp = hp;
                    }

                    public virtual string Label()
                    {
                        return "unit";
                    }
                }

                public class Hero : Unit
                {
                    public int Mana = 10;

                    public Hero(int hp) : base(hp)
                    {
                    }

                    public override string Label()
                    {
                        return base.Label();
                    }
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Inheritance.cs");

        Assert.True(lua.IndexOf("-- Game.Unit", StringComparison.Ordinal) < lua.IndexOf("-- Game.Hero", StringComparison.Ordinal));
        Assert.Contains("setmetatable(SF__.Game.Hero, { __index = SF__.Game.Unit })", lua);
        Assert.Contains("function SF__.Game.Unit.__Init(self, hp)", lua);
        Assert.Matches(@"function SF__\.Game\.Hero\.__Init\(self, hp\d*\)", lua);
        Assert.Matches(@"SF__\.Game\.Unit\.__Init\(self, hp\d*\)", lua);
        Assert.Contains("self.Mana = 10", lua);
        Assert.Contains("function SF__.Game.Unit:Label()", lua);
        Assert.Contains("function SF__.Game.Hero:Label()", lua);
        Assert.Contains("SF__.Game.Unit.Label(self)", lua);
    }

    [Fact]
    public async Task LuaObject_subclass_with_class_attribute_is_transpiled()
    {
        var src = """
            using System;
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                public class LuaObject { }

                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public string? Class { get; set; }
                    public string? Module { get; set; }
                }
            }

            [Lua(Class = "MyBuff")]
            public class MyBuff : LuaObject
            {
                public float duration;

                public MyBuff(float duration)
                {
                    this.duration = duration;
                }

                public void Activate() { }
            }
            """;

        var (lua, diagnostics, warnings) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["MyBuff.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Empty(warnings);
        Assert.Contains("SF__.MyBuff", lua);
        Assert.Contains("function SF__.MyBuff.New(duration)", lua);
        Assert.Contains("function SF__.MyBuff:Activate()", lua);
    }

    [Fact]
    public async Task LuaClass_subclass_with_base_arguments_passes_args_to_new()
    {
        var src = """
            using System;
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                public class LuaObject { }

                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public string? Class { get; set; }
                    public string? Module { get; set; }
                    public bool TableLiteral { get; set; }
                }
            }

            [Lua(Module = "Objects.BuffBase")]
            public class BuffBase : LuaObject
            {
                public object caster;
                public object target;
                public BuffBase(object caster, object target, float duration) => throw null!;
            }

            [Lua(Class = "MyDebuff")]
            public class MyDebuff : BuffBase
            {
                private float _spec;
                public MyDebuff(object caster, object target, float duration) : base(caster, target, duration)
                {
                    _spec = 15;
                }
            }
            """;

        var (lua, _, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["MyDebuff.cs"] = src });

        // .new() must pass base constructor arguments
        Assert.Contains("SF__.MyDebuff.New(caster, target, duration)", lua);
        Assert.Contains("SF__.MyDebuff.new(caster, target, duration)", lua);
        Assert.DoesNotContain("SF__.MyDebuff.new()", lua);
    }

    [Fact]
    public async Task LuaObject_subclass_without_attribute_produces_warning()
    {
        var src = """
            using System;
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                public class LuaObject { }

                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public string? Class { get; set; }
                    public string? Module { get; set; }
                    public bool TableLiteral { get; set; }
                }
            }

            public class MissingAttr : LuaObject
            {
                public float value;

                public MissingAttr(float v)
                {
                    value = v;
                }
            }
            """;

        var (lua, diagnostics, warnings) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["MissingAttr.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Contains(warnings, w => w.Contains("MissingAttr"));
        Assert.DoesNotContain("SF__.MissingAttr", lua);
    }

    [Fact]
    public async Task External_LuaObject_base_ctor_not_emitted_in_init()
    {
        var src = """
            using System;
            using SFLib.Interop;

            namespace SFLib.Interop
            {
                public class LuaObject { }

                [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                public sealed class LuaAttribute : Attribute
                {
                    public string? Class { get; set; }
                    public string? Module { get; set; }
                }
            }

            [Lua(Module = "Objects.BuffBase")]
            public class BuffBase : LuaObject
            {
                public object caster;
                public object target;
                public BuffBase(object caster, object target, float duration) => throw null!;
            }

            [Lua(Class = "MyDebuff")]
            public class MyDebuff : BuffBase
            {
                private float _spec;
                public MyDebuff(object caster, object target, float duration) : base(caster, target, duration)
                {
                    _spec = 15;
                }
            }
            """;

        var (lua, diagnostics, warnings) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["MyDebuff.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Empty(warnings);

        // External Lua object types are initialized by class() runtime via clone(super) + ctor.
        // The transpiler must NOT emit an explicit base constructor call in __Init.
        Assert.DoesNotContain("BuffBase.new(", lua);
        Assert.DoesNotContain("BuffBase.ctor(", lua);
    }
}
