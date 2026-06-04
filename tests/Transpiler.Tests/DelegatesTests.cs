using SharpForge.Transpiler.Frontend;
using SharpForge.Transpiler.Pipeline;
using Xunit;

namespace SharpForge.Transpiler.Tests;

public class DelegatesTests
{
    [Fact]
    public async Task Jass_filter_lambda_emits_lua_function_argument()
    {
        var src = """
            using System;
            using SFLib.Interop;
            using static JASS;

            namespace SFLib.Interop
            {
                public sealed class LuaObject { }

                public static class LuaInterop
                {
                    public static LuaObject CallGlobal(string name, object arg) => throw null!;
                }
            }

            public static partial class JASS
            {
                public static object bj_mapInitialPlayableArea = null!;
                public static object CreateGroup() => throw null!;
                public static object Filter(Func<bool> func) => throw null!;
                public static void GroupEnumUnitsInRect(object group, object rect, object filter) => throw null!;
                public static void DestroyGroup(object group) => throw null!;
                public static object GetFilterUnit() => throw null!;
            }

            public static class Program
            {
                public static void Main()
                {
                    var group = CreateGroup();
                    GroupEnumUnitsInRect(group, bj_mapInitialPlayableArea, Filter(() =>
                    {
                        LuaInterop.CallGlobal("ExTriggerRegisterNewUnitExec", GetFilterUnit());
                        return true;
                    }));
                    DestroyGroup(group);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "FilterLambda.cs");

        Assert.Contains("local group = CreateGroup()", lua);
        Assert.Contains("GroupEnumUnitsInRect(group, bj_mapInitialPlayableArea, Filter(function()", lua);
        Assert.Contains("ExTriggerRegisterNewUnitExec(GetFilterUnit())", lua);
        Assert.Contains("return true", lua);
        Assert.Contains("end))", lua);
        Assert.DoesNotContain("unsupported expression: ParenthesizedLambdaExpression", lua);
    }

    [Fact]
    public async Task Bound_instance_method_groups_passed_to_action_parameters_lower_through_receiver_capturing_lambdas()
    {
        var src = """
            using System;

            public class unit { }

            public class UnitStore
            {
                public void Add(unit value) { }
                public void Clear() { }
            }

            public static partial class JASS
            {
                public static void ExTriggerRegisterNewUnit(Action<unit> callback) => throw null!;
                public static void ExTriggerRegisterReady(Action callback) => throw null!;
            }

            public class Program
            {
                private readonly UnitStore _units = new();

                public void Init()
                {
                    JASS.ExTriggerRegisterNewUnit(_units.Add);
                    JASS.ExTriggerRegisterReady(_units.Clear);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "BoundMethodGroupAction.cs");

        Assert.Contains("ExTriggerRegisterNewUnit(function", lua);
        Assert.Matches(@"function\((\w+)\)\s+self\._units:Add\(\1\)\s+end", lua);
        Assert.Contains("ExTriggerRegisterReady(function()", lua);
        Assert.Matches(@"function\(\)\s+self\._units:Clear\(\)\s+end", lua);
        Assert.DoesNotContain("ExTriggerRegisterNewUnit(self._units.Add)", lua);
    }

    [Fact]
    public async Task Conditional_delegate_invoke_statements_lower_to_nil_guarded_temp_calls()
    {
        var src = """
            using System;

            public static class Signals
            {
                public static void Raise(Action<int>? handler, int value)
                {
                    handler?.Invoke(value);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "ConditionalDelegateInvoke.cs");

        Assert.Contains("local delegate = handler", lua);
        Assert.Contains("if (delegate ~= nil) then", lua);
        Assert.Contains("delegate(value)", lua);
        Assert.DoesNotContain("unsupported expression: ConditionalAccessExpression", lua);
        Assert.DoesNotContain("delegate.Invoke", lua);
    }

    [Fact]
    public async Task Computed_properties_indexers_delegates_and_events_emit_mvp_shapes()
    {
        var src = """
            using System;

            public class Bag
            {
                private int[] values = new[] { 1, 2 };
                public int Total => values[0] + values[1];

                public int this[int index]
                {
                    get { return values[index]; }
                    set { values[index] = value; }
                }
            }

            public static class Signals
            {
                public static event Action? Changed;

                public static int Double(int value)
                {
                    return value * 2;
                }

                public static int Run()
                {
                    Func<int, int> fn = Double;
                    var bag = new Bag();
                    bag[0] = fn(3);
                    return bag.Total + bag[0];
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Phase7.cs");

        Assert.Contains("function SF__.Bag:get_Total()", lua);
        Assert.Contains("function SF__.Bag:get_Item(index)", lua);
        Assert.Matches(@"function SF__\.Bag:set_Item\(index\d*, value\d*\)", lua);
        Assert.Matches(@"self\.values\[\(index\d* \+ 1\)\] = value\d*", lua);
        Assert.Contains("SF__.Signals.Changed = nil", lua);
        Assert.Contains("local fn = SF__.Signals.Double", lua);
        Assert.Contains("bag:set_Item(0, fn(3))", lua);
        Assert.Contains("return (bag:get_Total() + bag:get_Item(0))", lua);
    }

    [Fact]
    public async Task Delegate_invoke_strips_dot_invoke()
    {
        var src = """
            using System;
            public static class P
            {
                public static void Run(Action action)
                {
                    action.Invoke();
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src);

        // action.Invoke() should become action(), not action:Invoke()
        Assert.DoesNotContain("Invoke", lua);
        Assert.DoesNotContain(":Invoke", lua);
    }
}
