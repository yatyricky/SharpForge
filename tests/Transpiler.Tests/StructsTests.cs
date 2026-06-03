using Xunit;

namespace SharpForge.Transpiler.Tests;

public class StructsTests
{
    [Fact]
    public async Task Vector_style_structs_emit_constructors_fields_and_operator_calls()
    {
        var src = """
            public struct Vector2
            {
                public int X;
                public int Y;

                public Vector2(int x, int y)
                {
                    X = x;
                    Y = y;
                }

                public static Vector2 operator +(Vector2 a, Vector2 b)
                {
                    return new Vector2(a.X + b.X, a.Y + b.Y);
                }
            }

            public static class MathDemo
            {
                public static int Run()
                {
                    var a = new Vector2(1, 2);
                    var b = new Vector2(3, 4);
                    var c = a + b;
                    return c.X;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "Vector2.cs");

        Assert.Contains("-- Vector2", lua);
        Assert.DoesNotContain("System.ValueType", lua);
        Assert.Contains("function SF__.Vector2.op_Addition(a__X, a__Y, b__X, b__Y)", lua);
        Assert.Contains("return (a__X + b__X), (a__Y + b__Y)", lua);
        Assert.Matches(@"local a__X\d*, a__Y\d* = 1, 2", lua);
        Assert.Matches(@"local b__X\d*, b__Y\d* = 3, 4", lua);
        Assert.Matches(@"local c__X\d*, c__Y\d* = SF__\.Vector2\.op_Addition\(a__X\d*, a__Y\d*, b__X\d*, b__Y\d*\)", lua);
        Assert.Contains("return c__X", lua);
        Assert.DoesNotContain("function SF__.Vector2.__Init", lua);
        Assert.DoesNotContain("function SF__.Vector2.New", lua);
        Assert.DoesNotContain("SF__.Vector2.New(", lua);
    }

    [Fact]
    public async Task Struct_constructor_field_mapping_uses_constructor_syntax_tree_model()
    {
        var lua = await TranspilerTestHelper.TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["Vector2.cs"] = """
                public struct Vector2
                {
                    public int X;
                    public int Y;

                    public Vector2(int x, int y)
                    {
                        X = x;
                        Y = y;
                    }
                }
                """,
            ["Scale.cs"] = """
                public struct Scale
                {
                    public int Value;

                    public Scale(int value)
                    {
                        Value = value;
                    }

                    public static Scale operator +(Scale left, Scale right)
                    {
                        var pair = new Vector2(left.Value, right.Value);
                        return new Scale(pair.X + pair.Y);
                    }
                }
                """,
        });

        Assert.Contains("function SF__.Scale.op_Addition(left__Value, right__Value)", lua);
        Assert.Matches(@"local pair__X\d*, pair__Y\d* = left__Value, right__Value", lua);
        Assert.Contains("return (pair__X + pair__Y)", lua);
    }

    [Fact]
    public async Task Struct_locals_used_only_by_fields_are_flattened()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public Vector2(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }
            }

            public static class MathDemo
            {
                public static Vector2 Make(float scale)
                {
                    return new Vector2
                    {
                        x = scale + 1,
                        y = scale + 2,
                    };
                }

                public static float Use(Vector2 value)
                {
                    return value.x + value.y;
                }

                public static string Run()
                {
                    var v = new Vector2(10, 5);
                    v.x = 11;
                    v = new Vector2(12, 6);
                    var data = Make(2);
                    return $"x:{v.x} y:{v.y} data:{data.x}:{data.y}:{Use(data)}";
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "FlattenedStruct.cs");

        Assert.Contains("local v__x, v__y = 10, 5", lua);
        Assert.Contains("v__x = 11", lua);
        Assert.Contains("v__x, v__y = 12, 6", lua);
        Assert.Contains("return (scale + 1), (scale + 2)", lua);
        Assert.Contains("function SF__.MathDemo.Use(value__x, value__y)", lua);
        Assert.Contains("return (value__x + value__y)", lua);
        Assert.Contains("local data__x, data__y = SF__.MathDemo.Make(2)", lua);
        Assert.Contains("return SF__.StrConcat__(\"x:\", v__x, \" y:\", v__y, \" data:\", data__x, \":\", data__y, \":\", SF__.MathDemo.Use(data__x, data__y))", lua);
        Assert.DoesNotContain("local v = SF__.Vector2.New", lua);
        Assert.DoesNotContain("-- Vector2", lua);
        Assert.DoesNotContain("SF__.Vector2 = SF__.Vector2 or {}", lua);
        Assert.DoesNotContain("function SF__.Vector2.__Init", lua);
        Assert.DoesNotContain("function SF__.Vector2.New", lua);
    }

    [Fact]
    public async Task Struct_local_used_in_loop_and_return_is_flattened()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public Vector3(float x, float y, float z)
                {
                    this.x = x;
                    this.y = y;
                    this.z = z;
                }

                public static Vector3 operator +(Vector3 a, Vector3 b)
                {
                    return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
                }
            }

            public static class TransformDemo
            {
                public static Vector3 Compute(Vector3 localPos, Vector3 offset, int count)
                {
                    var pos = localPos;
                    var i = 0;
                    while (i < count)
                    {
                        pos = pos + offset;
                        i++;
                    }
                    return pos;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructReturnFlatten.cs");

        Assert.Contains("local pos__x, pos__y, pos__z = localPos__x, localPos__y, localPos__z", lua);
        Assert.Contains("pos__x, pos__y, pos__z = SF__.Vector3.op_Addition(pos__x, pos__y, pos__z, offset__x, offset__y, offset__z)", lua);
        Assert.Contains("return pos__x, pos__y, pos__z", lua);
        Assert.DoesNotContain("local pos = localPos", lua);
        Assert.DoesNotContain("return pos.x", lua);
    }

    [Fact]
    public async Task Struct_typed_class_members_are_flattened()
    {
        var src = """
            public struct AbilityData
            {
                public float DamageScaling;
                public float ArtOfWarChance;
            }

            public class CrusaderStrike
            {
                private AbilityData _template;
                private static AbilityData staticProp;

                private void OnInspector()
                {
                    var scale = _template.DamageScaling * 15;
                    var chance = staticProp.ArtOfWarChance;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructMembers.cs");

        Assert.Contains("self._template__DamageScaling = 0", lua);
        Assert.Contains("self._template__ArtOfWarChance = 0", lua);
        Assert.Contains("SF__.CrusaderStrike.staticProp__DamageScaling = 0", lua);
        Assert.Contains("SF__.CrusaderStrike.staticProp__ArtOfWarChance = 0", lua);
        Assert.Contains("local scale = (self._template__DamageScaling * 15)", lua);
        Assert.Contains("local chance = SF__.CrusaderStrike.staticProp__ArtOfWarChance", lua);
        Assert.DoesNotContain("self._template = nil", lua);
        Assert.DoesNotContain("SF__.CrusaderStrike.staticProp = nil", lua);
    }

    [Fact]
    public async Task Assignments_to_flattened_struct_members_expand_to_field_assignments()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public Vector3(float x, float y, float z)
                {
                    this.x = x;
                    this.y = y;
                    this.z = z;
                }
            }

            public struct Quaternion
            {
                public float x;
                public float y;
                public float z;
                public float w;

                public static Quaternion Euler(float x, float y, float z)
                {
                    return new Quaternion { x = x, y = y, z = z, w = 1f };
                }

                public static Quaternion operator *(Quaternion left, Quaternion right)
                {
                    return new Quaternion { x = left.x + right.x, y = left.y + right.y, z = left.z + right.z, w = left.w + right.w };
                }
            }

            public class Transform
            {
                public Vector3 position;
                public Quaternion rotation;
                public Vector3 localScale;

                public Transform()
                {
                    position = new Vector3(0f, 0f, 0f);
                    rotation = Quaternion.Euler(0f, 0f, 0f);
                    localScale = new Vector3(1f, 1f, 1f);
                }
            }

            public class GameObject
            {
                public Transform transform = new Transform();
            }

            public static class Motion
            {
                public static void Spin(GameObject boltMis)
                {
                    var trs = boltMis.transform;
                    var rot = Quaternion.Euler(1f, 0f, 0f);
                    trs.rotation = rot * trs.rotation;
                }
            }

            public class ShadowedTransform
            {
                public Quaternion rotation;

                public void Reset()
                {
                    var rotation = Quaternion.Euler(1f, 0f, 0f);
                    rotation = Quaternion.Euler(2f, 0f, 0f);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "TransformStructMembers.cs");

        Assert.Contains("self.position__x, self.position__y, self.position__z = 0, 0, 0", lua);
        Assert.Contains("self.rotation__x, self.rotation__y, self.rotation__z, self.rotation__w = SF__.Quaternion.Euler(0, 0, 0)", lua);
        Assert.Contains("self.localScale__x, self.localScale__y, self.localScale__z = 1, 1, 1", lua);
        Assert.Contains("trs.rotation__x, trs.rotation__y, trs.rotation__z, trs.rotation__w = SF__.Quaternion.op_Multiply(rot__x, rot__y, rot__z, rot__w, trs.rotation__x, trs.rotation__y, trs.rotation__z, trs.rotation__w)", lua);
        Assert.Contains("local rotation__x, rotation__y, rotation__z, rotation__w = SF__.Quaternion.Euler(1, 0, 0)", lua);
        Assert.Contains("rotation__x, rotation__y, rotation__z, rotation__w = SF__.Quaternion.Euler(2, 0, 0)", lua);
        Assert.DoesNotContain("self.position = {", lua);
        Assert.DoesNotContain("self.rotation = SF__.Quaternion.Euler", lua);
        Assert.DoesNotContain("self.localScale = {", lua);
        Assert.DoesNotContain("self.rotation__x, self.rotation__y, self.rotation__z, self.rotation__w = SF__.Quaternion.op_Multiply(rot__x", lua);
        Assert.DoesNotContain("self.rotation__x, self.rotation__y, self.rotation__z, self.rotation__w = SF__.Quaternion.Euler(2", lua);
    }

    [Fact]
    public async Task Nested_flattened_struct_members_expand_across_files_regardless_of_type_order()
    {
        var lua = await TranspilerTestHelper.TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["AttachEffectComponent.cs"] = """
                public class AttachEffectComponent
                {
                    public GameObject gameObject = new GameObject();

                    public void Update()
                    {
                        var globalPos = gameObject.transform.position;
                        var globalRot = gameObject.transform.rotation;
                        var globalScale = gameObject.transform.localScale;
                        var parent = gameObject.transform.parent;
                        while (parent != null)
                        {
                            globalPos = parent.position + parent.rotation * Vector3.Scale(parent.localScale, globalPos);
                            globalRot = parent.rotation * globalRot;
                            globalScale = Vector3.Scale(parent.localScale, globalScale);
                            parent = parent.parent;
                        }
                    }
                }
                """,
            ["Transform.cs"] = """
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;

                    public static Vector3 Scale(Vector3 left, Vector3 right)
                    {
                        return new Vector3
                        {
                            x = left.x * right.x,
                            y = left.y * right.y,
                            z = left.z * right.z,
                        };
                    }

                    public static Vector3 operator +(Vector3 left, Vector3 right)
                    {
                        return new Vector3
                        {
                            x = left.x + right.x,
                            y = left.y + right.y,
                            z = left.z + right.z,
                        };
                    }
                }

                public struct Quaternion
                {
                    public float x;
                    public float y;
                    public float z;
                    public float w;

                    public static Vector3 operator *(Quaternion left, Vector3 right)
                    {
                        return right;
                    }

                    public static Quaternion operator *(Quaternion left, Quaternion right)
                    {
                        return left;
                    }
                }

                public class Transform
                {
                    public Vector3 position;
                    public Quaternion rotation;
                    public Vector3 localScale;
                    public Transform? parent;
                }

                public class GameObject
                {
                    public Transform transform = new Transform();
                }
                """,
        });

        Assert.Contains("local globalPos__x, globalPos__y, globalPos__z = self.gameObject.transform.position__x, self.gameObject.transform.position__y, self.gameObject.transform.position__z", lua);
        Assert.Contains("local globalRot__x, globalRot__y, globalRot__z, globalRot__w = self.gameObject.transform.rotation__x, self.gameObject.transform.rotation__y, self.gameObject.transform.rotation__z, self.gameObject.transform.rotation__w", lua);
        Assert.Contains("local globalScale__x, globalScale__y, globalScale__z = self.gameObject.transform.localScale__x, self.gameObject.transform.localScale__y, self.gameObject.transform.localScale__z", lua);
        Assert.Contains("globalPos__x, globalPos__y, globalPos__z = SF__.Vector3.op_Addition(parent.position__x, parent.position__y, parent.position__z, SF__.Quaternion.op_Multiply__iyiose(parent.rotation__x, parent.rotation__y, parent.rotation__z, parent.rotation__w, SF__.Vector3.Scale(parent.localScale__x, parent.localScale__y, parent.localScale__z, globalPos__x, globalPos__y, globalPos__z)))", lua);
        Assert.DoesNotContain(".position.x", lua);
        Assert.DoesNotContain(".rotation.x", lua);
        Assert.DoesNotContain(".localScale.x", lua);
    }

    [Fact]
    public async Task Flattened_struct_locals_stay_expanded_when_stringified()
    {
        var lua = await TranspilerTestHelper.TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["AttachEffectComponent.cs"] = """
                public class AttachEffectComponent
                {
                    public GameObject gameObject = new GameObject();

                    private static void Log(string text)
                    {
                    }

                    public void Update()
                    {
                        var globalPos = gameObject.transform.position;
                        var globalRot = gameObject.transform.rotation;
                        var globalScale = gameObject.transform.localScale;
                        var parent = gameObject.transform.parent;
                        while (parent != null)
                        {
                            Log($"parent.localScale:{parent.localScale}");
                            Log($"globalPos:{globalPos}");
                            Log($"parent.rotation:{parent.rotation}");
                            globalRot = parent.rotation * globalRot;
                            globalScale = Vector3.Scale(parent.localScale, globalScale);
                            parent = parent.parent;
                        }
                    }
                }
                """,
            ["Transform.cs"] = """
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;

                    public override string ToString()
                    {
                        return $"({x},{y},{z})";
                    }

                    public static Vector3 Scale(Vector3 left, Vector3 right)
                    {
                        return new Vector3
                        {
                            x = left.x * right.x,
                            y = left.y * right.y,
                            z = left.z * right.z,
                        };
                    }
                }

                public struct Quaternion
                {
                    public float x;
                    public float y;
                    public float z;
                    public float w;

                    public override string ToString()
                    {
                        return $"({x},{y},{z},{w})";
                    }

                    public static Quaternion operator *(Quaternion left, Quaternion right)
                    {
                        return left;
                    }
                }

                public class Transform
                {
                    public Vector3 position;
                    public Quaternion rotation;
                    public Vector3 localScale;
                    public Transform? parent;
                }

                public class GameObject
                {
                    public Transform transform = new Transform();
                }
                """,
        });

        Assert.Contains("local globalPos__x, globalPos__y, globalPos__z = self.gameObject.transform.position__x, self.gameObject.transform.position__y, self.gameObject.transform.position__z", lua);
        Assert.Contains("SF__.AttachEffectComponent.Log(SF__.StrConcat__(\"globalPos:\", SF__.Vector3.ToString(globalPos__x, globalPos__y, globalPos__z)))", lua);
        Assert.DoesNotContain("local globalPos = self.gameObject.transform.position", lua);
        Assert.DoesNotContain("globalPos.x", lua);
    }

    [Fact]
    public async Task Flattened_struct_locals_stay_expanded_when_assigned_to_struct_members()
    {
        var lua = await TranspilerTestHelper.TranspileSourcesAsync(new Dictionary<string, string>
        {
            ["DivineToll.cs"] = """
                public static class DivineToll
                {
                    public static void Start(SpellData data)
                    {
                        var pos = Vector3.FromUnit(data.caster);
                        var bolt = new GameObject();
                        bolt.transform.position = pos;
                        Spawn(pos.x, pos.y);
                    }

                    public static void Spawn(float x, float y)
                    {
                    }
                }

                public class SpellData
                {
                    public int caster;
                }
                """,
            ["Transform.cs"] = """
                public struct Vector3
                {
                    public float x;
                    public float y;
                    public float z;

                    public static Vector3 FromUnit(int unit)
                    {
                        return new Vector3
                        {
                            x = 1,
                            y = 2,
                            z = 3,
                        };
                    }
                }

                public class Transform
                {
                    public Vector3 position;
                }

                public class GameObject
                {
                    public Transform transform = new Transform();
                }
                """,
        });

        Assert.Contains("local pos__x, pos__y, pos__z = SF__.Vector3.FromUnit(data.caster)", lua);
        Assert.Contains(".transform.position__x", lua);
        Assert.Contains(".transform.position__y", lua);
        Assert.Contains(".transform.position__z", lua);
        Assert.Contains("= pos__x, pos__y, pos__z", lua);
        Assert.Contains("SF__.DivineToll.Spawn(pos__x, pos__y)", lua);
        Assert.DoesNotContain("local pos = SF__.Vector3.FromUnit", lua);
        Assert.DoesNotContain("position__x, bolt.transform.position__y, bolt.transform.position__z = pos.x, pos.y, pos.z", lua);
    }

    [Fact]
    public async Task Struct_parameters_are_flattened()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;
            }

            public static class Sample
            {
                public static float Sum(Vector2 pos)
                {
                    return pos.x + pos.y;
                }

                public static float Run()
                {
                    return Sum(new Vector2 { x = 2, y = 3 });
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructParameter.cs");

        Assert.Contains("function SF__.Sample.Sum(pos__x, pos__y)", lua);
        Assert.Contains("return (pos__x + pos__y)", lua);
        Assert.Contains("return SF__.Sample.Sum(2, 3)", lua);
        Assert.DoesNotContain("pos.x", lua);
        Assert.DoesNotContain("pos.y", lua);
    }

    [Fact]
    public async Task Struct_methods_use_flattened_self_and_parameters()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public static Vector2 operator +(Vector2 left, Vector2 right)
                {
                    return new Vector2 { x = left.x + right.x, y = left.y + right.y };
                }

                public float MagnitudeSquared()
                {
                    return x * x + y * y;
                }
            }

            public static class Sample
            {
                public static float Run()
                {
                    var left = new Vector2 { x = 1, y = 2 };
                    var right = new Vector2 { x = 3, y = 4 };
                    var sum = left + right;
                    return sum.MagnitudeSquared();
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructMethods.cs");

        Assert.Contains("function SF__.Vector2.op_Addition(left__x, left__y, right__x, right__y)", lua);
        Assert.Contains("return (left__x + right__x), (left__y + right__y)", lua);
        Assert.Contains("function SF__.Vector2.MagnitudeSquared(self__x, self__y)", lua);
        Assert.Contains("(self__x * self__x)", lua);
        Assert.Contains("(self__y * self__y)", lua);
        Assert.Matches(@"local sum__x\d*, sum__y\d* = SF__\.Vector2\.op_Addition\(left__x\d*, left__y\d*, right__x\d*, right__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.MagnitudeSquared\(sum__x\d*, sum__y\d*\)", lua);
        Assert.DoesNotContain("function SF__.Vector2:MagnitudeSquared", lua);
    }

    [Fact]
    public async Task Nested_struct_operator_calls_spill_non_tail_multi_return_arguments()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public static Vector2 operator +(Vector2 left, Vector2 right)
                {
                    return new Vector2
                    {
                        x = left.x + right.x,
                        y = left.y + right.y,
                    };
                }
            }

            public static class Sample
            {
                public static Vector2 Run(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
                {
                    return (a + b) + (c + d);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "NestedStructOperatorCalls.cs");

        Assert.Contains("function SF__.Vector2.op_Addition(left__x, left__y, right__x, right__y)", lua);
        Assert.Matches(@"local left__x\d*, left__y\d* = SF__\.Vector2\.op_Addition\(a__x, a__y, b__x, b__y\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.op_Addition\(left__x\d*, left__y\d*, SF__\.Vector2\.op_Addition\(c__x, c__y, d__x, d__y\)\)", lua);
    }

    [Fact]
    public async Task Struct_properties_use_flattened_self_and_receiver_values()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public static Vector2 operator -(Vector2 left, Vector2 right)
                {
                    return new Vector2 { x = left.x - right.x, y = left.y - right.y };
                }

                public float Magnitude => SqrMagnitude + 1;
                public float SqrMagnitude => x * x + y * y;
            }

            public static class Sample
            {
                public static float Run()
                {
                    var left = new Vector2 { x = 6, y = 8 };
                    var right = new Vector2 { x = 1, y = 2 };
                    return (left - right).Magnitude;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructProperties.cs");

        Assert.Matches(@"function SF__\.Vector2\.get_Magnitude\(self__x\d*, self__y\d*\)", lua);
        Assert.Matches(@"function SF__\.Vector2\.get_SqrMagnitude\(self__x\d*, self__y\d*\)", lua);
        Assert.Matches(@"return \(SF__\.Vector2\.get_SqrMagnitude\(self__x\d*, self__y\d*\) \+ 1\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.get_Magnitude\(SF__\.Vector2\.op_Subtraction\(left__x\d*, left__y\d*, right__x\d*, right__y\d*\)\)", lua);
        Assert.DoesNotContain("function SF__.Vector2:get_Magnitude", lua);
        Assert.DoesNotContain(":get_Magnitude()", lua);
        Assert.DoesNotContain(":get_SqrMagnitude()", lua);
    }

    [Fact]
    public async Task Struct_returning_property_argument_is_not_treated_as_table_fields()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public Vector2(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }

                public static Vector2 operator *(Vector2 value, float scale)
                {
                    return new Vector2(value.x * scale, value.y * scale);
                }

                public static Vector2 operator *(float scale, Vector2 value)
                {
                    return new Vector2(value.x * scale, value.y * scale);
                }

                public Vector2 Normalized => new(0, 0);

                public Vector2 ClampMagnitude(float mag)
                {
                    return Normalized * mag;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructPropertyArgument.cs");

        Assert.Matches(@"function SF__\.Vector2\.ClampMagnitude\(self__x\d*, self__y\d*, mag\)", lua);
        Assert.Matches(@"local value__x\d*, value__y\d* = SF__\.Vector2\.get_Normalized\(self__x\d*, self__y\d*\)", lua);
        Assert.Matches(@"return SF__\.Vector2\.op_Multiply__ahdf\(value__x\d*, value__y\d*, mag\)", lua);
        Assert.DoesNotMatch(@"get_Normalized\(self__x\d*, self__y\d*\)\.x", lua);
        Assert.DoesNotMatch(@"get_Normalized\(self__x\d*, self__y\d*\)\.y", lua);
    }

    [Fact]
    public async Task Struct_returning_parameter_stays_flattened()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public static Vector3 operator -(Vector3 left, Vector3 right)
                {
                    return new Vector3 { x = left.x - right.x, y = left.y - right.y, z = left.z - right.z };
                }

                public static Vector3 operator +(Vector3 left, Vector3 right)
                {
                    return new Vector3 { x = left.x + right.x, y = left.y + right.y, z = left.z + right.z };
                }

                public static Vector3 operator /(Vector3 value, float scale)
                {
                    return new Vector3 { x = value.x / scale, y = value.y / scale, z = value.z / scale };
                }

                public float magnitude => x + y + z;

                public static Vector3 MoveTowards(Vector3 current, Vector3 target, float maxDistanceDelta)
                {
                    var toVector = target - current;
                    var dist = toVector.magnitude;
                    if (dist <= maxDistanceDelta || dist == 0f)
                    {
                        return target;
                    }
                    return current + toVector / (dist / maxDistanceDelta);
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructReturnParameter.cs");

        Assert.Matches(@"function SF__\.Vector3\.MoveTowards\(current__x\d*, current__y\d*, current__z\d*, target__x\d*, target__y\d*, target__z\d*, maxDistanceDelta\)", lua);
        Assert.Matches(@"return target__x\d*, target__y\d*, target__z\d*", lua);
        Assert.DoesNotContain("return {x = target__x", lua);
    }

    [Fact]
    public async Task Struct_ternary_assignment_spills_branches_to_flattened_fields()
    {
        var src = """
            public struct Vector3
            {
                public float x;
                public float y;
                public float z;

                public static Vector3 FromUnit(int unit)
                {
                    return new Vector3 { x = unit, y = unit + 1, z = unit + 2 };
                }
            }

            public class MoveTowardsComponent
            {
                public Vector3 pointTarget;
                private Vector3 _targetPosition;

                public void Update(bool useUnit, int unitTarget)
                {
                    _targetPosition = useUnit ? Vector3.FromUnit(unitTarget) : pointTarget;
                    var selected = useUnit ? Vector3.FromUnit(unitTarget) : pointTarget;
                    _targetPosition = selected;
                }
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructTernaryAssignment.cs");

        Assert.Contains("if useUnit then", lua);
        Assert.Contains("local ternary__x, ternary__y, ternary__z", lua);
        Assert.Matches(@"local ternary__x, ternary__y, ternary__z\s+do\s+if useUnit then", lua);
        Assert.Contains("ternary__x, ternary__y, ternary__z = SF__.Vector3.FromUnit(unitTarget)", lua);
        Assert.Contains("ternary__x, ternary__y, ternary__z = self.pointTarget__x, self.pointTarget__y, self.pointTarget__z", lua);
        Assert.Contains("self._targetPosition__x, self._targetPosition__y, self._targetPosition__z = ternary__x, ternary__y, ternary__z", lua);
        Assert.Contains("local selected__x, selected__y, selected__z", lua);
        Assert.Matches(@"local selected__x, selected__y, selected__z\s+do\s+if useUnit then", lua);
        Assert.Contains("selected__x, selected__y, selected__z = SF__.Vector3.FromUnit(unitTarget)", lua);
        Assert.Contains("selected__x, selected__y, selected__z = self.pointTarget__x, self.pointTarget__y, self.pointTarget__z", lua);
        Assert.DoesNotContain("SF__.Ternary__(useUnit", lua);
    }

    [Fact]
    public async Task Target_typed_struct_creation_in_expression_bodied_returns_uses_multi_return()
    {
        var src = """
            public struct Vector2
            {
                public float x;
                public float y;

                public Vector2(float x, float y)
                {
                    this.x = x;
                    this.y = y;
                }

                public static Vector2 Zero => new(0, 0);
                public static Vector2 One { get => new(1, 1); }
                public static Vector2 Make() => new(2, 3);
            }
            """;

        var lua = await TranspilerTestHelper.TranspileAsync(src, "StructExpressionBodiedReturns.cs");

        Assert.Contains("function SF__.Vector2.get_Zero()", lua);
        Assert.Contains("function SF__.Vector2.get_One()", lua);
        Assert.Contains("function SF__.Vector2.Make()", lua);
        Assert.Contains("return 0, 0", lua);
        Assert.Contains("return 1, 1", lua);
        Assert.Contains("return 2, 3", lua);
        Assert.DoesNotContain("return {x = 0, y = 0}", lua);
        Assert.DoesNotContain("return {x = 1, y = 1}", lua);
        Assert.DoesNotContain("return {x = 2, y = 3}", lua);
    }
}
