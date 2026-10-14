using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class GenericsTests
{
    [Fact]
    public async Task Generic_type_struct_instantiation_specializes_direct_type_parameter_methods()
    {
        var src = """
            using System;
            using StdLib;

            public struct NativeDamageData
            {
                public float damage;
                public unit damageSource;
                public unit damageTarget;
                public bool isAttack;
                public int damageType;
            }

            public class unit { }

            public class EventBase<T>
            {
                protected List<Action<T>> _callbacks = new();

                protected void ResponseCall(T data)
                {
                    foreach (var callback in _callbacks)
                    {
                        callback(data);
                    }
                }

                public void Register(Action<T> callback)
                {
                    if (_callbacks.IndexOf(callback) == -1)
                    {
                        _callbacks.Add(callback);
                    }
                }
            }

            public class DamagedEvent : EventBase<NativeDamageData>
            {
                public void Fire(NativeDamageData data)
                {
                    ResponseCall(data);
                }
            }

            public static class Program
            {
                private static void OnDamage(NativeDamageData data)
                {
                }

                public static void Main()
                {
                    var evt = new DamagedEvent();
                    evt.Register(OnDamage);
                    evt.Fire(new NativeDamageData { damage = 10f, damageSource = new unit(), damageTarget = new unit(), isAttack = true, damageType = 1 });
                }
            }
            """;

        var (exitCode, lua, _) = await TranspilerTestHelper.TranspileViaPipelineAsync(src, "GenericStructEvent.cs");

        Assert.Equal(0, exitCode);
        Assert.Matches(@"function SF__\.EventBase:ResponseCall__NativeDamageData\(data__damage\w*, data__damageSource\w*, data__damageTarget\w*, data__isAttack\w*, data__damageType\w*\)", lua);
        Assert.Matches(@"callback\w*\(data__damage\w*, data__damageSource\w*, data__damageTarget\w*, data__isAttack\w*, data__damageType\w*\)", lua);
        Assert.Matches(@"evt:Fire\(10, .+, .+, true, 1\)", lua);
        Assert.Contains("evt:Register(SF__.Program.OnDamage)", lua);
        Assert.Contains("function SF__.EventBase:Register(callback", lua);
        Assert.DoesNotContain("Register__", lua);
    }

    [Fact]
    public async Task Generic_type_specializations_cover_each_struct_instantiation_and_compose_with_overloads()
    {
        var src = """
            using System;

            public struct VecA
            {
                public int ax;
                public int ay;
            }

            public struct VecB
            {
                public int bx;
            }

            public class Plain { }

            public class Holder<T>
            {
                public void Emit(T data) { }
                public void Emit(int extra, T data) { }

                public void Note(int count) { }
            }

            public class UseA : Holder<VecA>
            {
                public void Go(VecA v)
                {
                    Emit(v);
                    Emit(3, v);
                }
            }

            public class UseB : Holder<VecB>
            {
                public void Go(VecB v)
                {
                    Emit(v);
                }
            }

            public class UsePlain : Holder<Plain>
            {
                public void Go(Plain p)
                {
                    Emit(p);
                }
            }

            public static class Program
            {
                public static void Main()
                {
                    new UseA().Go(new VecA { ax = 1, ay = 2 });
                    new UseB().Go(new VecB { bx = 5 });
                    new UsePlain().Go(new Plain());
                }
            }
            """;

        var (lua, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["GenericMatrix.cs"] = src });

        Assert.Empty(diagnostics);
        Assert.Matches(@"function SF__\.Holder:Emit__\w+\(data\)", lua);
        Assert.Contains("function SF__.Holder:Note(count)", lua);
        Assert.Matches(@"function SF__\.Holder:Emit__\w+__VecA\(data__ax, data__ay\)", lua);
        Assert.Matches(@"function SF__\.Holder:Emit__\w+__VecB\(data__bx\)", lua);
        Assert.Matches(@"function SF__\.Holder:Emit__i\w+__VecA\(extra\w*, data__ax\w*, data__ay\w*\)", lua);
        Assert.Matches(@"self:Emit__\w+__VecA\(v__ax, v__ay\)", lua);
        Assert.Matches(@"self:Emit__i\w+__VecA\(3, v__ax, v__ay\)", lua);
        Assert.Matches(@"self:Emit__\w+__VecB\(v__bx\)", lua);
        Assert.Matches(@"self:Emit__\w+\(p\)", lua);
        Assert.DoesNotContain("__Plain", lua);
    }

    [Fact]
    public async Task Generic_method_struct_type_argument_reports_diagnostic()
    {
        var src = """
            public struct VecA
            {
                public int ax;
            }

            public static class Q
            {
                public static void Consume<T>(T item) { }
            }

            public static class Program
            {
                public static void Main()
                {
                    Q.Consume(new VecA { ax = 1 });
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["GenericMethodStruct.cs"] = src });

        Assert.Contains(diagnostics, d => d.Contains("cannot pass a struct to generic method 'Q.Consume'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generic_constructor_struct_argument_reports_diagnostic()
    {
        var src = """
            public struct VecA
            {
                public int ax;
            }

            public class Box<T>
            {
                public Box(T init) { }
            }

            public static class Program
            {
                public static void Main()
                {
                    var box = new Box<VecA>(new VecA { ax = 3 });
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["GenericCtorStruct.cs"] = src });

        Assert.Contains(diagnostics, d => d.Contains("cannot pass a struct to constructor of 'Box<T>'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generic_struct_specialized_method_as_value_reports_diagnostic()
    {
        var src = """
            using System;

            public struct VecA
            {
                public int ax;
            }

            public class Box<T>
            {
                public void Emit(T data) { }
            }

            public class UseA : Box<VecA>
            {
                public void Go()
                {
                    Action<VecA> f = Emit;
                }
            }
            """;

        var (_, diagnostics, _) = await TranspilerTestHelper.TranspileSourcesWithDiagnosticsAsync(
            new Dictionary<string, string> { ["GenericMethodGroupValue.cs"] = src });

        Assert.Contains(diagnostics, d => d.Contains("cannot use method 'Box<VecA>.Emit' as a value", StringComparison.Ordinal));
    }
}
