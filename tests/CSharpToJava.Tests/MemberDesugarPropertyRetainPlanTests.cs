using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C11 property 去糖「有意保留」记账回归：PropertyRule 对两类属性有意不展开
/// （元数据接口实现 / 跨程序集同名类型镜像合并），保留原声明且不改写使用点。
/// 这类保留必须为每个源属性产出一条 plan（Outcome=Retained + Info 诊断），否则
/// PropertyUseSiteRewriter 的 PD-12 residual 闸门会把它当成「无 plan 的残留属性」
/// 误发 CS2J5112，并污染 PlansSkipped 计数。
/// </summary>
public class MemberDesugarPropertyRetainPlanTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static MemberDesugarDiagnosticCode ResidualCode
        => MemberDesugarDiagnosticCode.ResidualProperty;

    /// <summary>多文件整树编译 → 对首个文件跑一次 RewriteFile（其余树共用同一编译绑定）。</summary>
    private static (string OutputCode, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diags)
        RewriteFirstFile(string path, string source, params (string Path, string Source)[] others)
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source, ParseOptions, path),
        };
        trees.AddRange(others.Select(o =>
            CSharpSyntaxTree.ParseText(o.Source, ParseOptions, o.Path)));

        var compilation = CSharpCompilation.Create(
            "member-desugar-retain",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            source, compilation.GetSemanticModel(trees[0]), stats, diags);
        return (result.OutputCode, stats, diags);
    }

    private static void AssertNoResidualProperty(List<MemberDesugarDiagnostic> diags)
    {
        var residual = diags.Where(d => d.Code == ResidualCode).ToList();
        Assert.True(residual.Count == 0,
            "unexpected CS2J5112 residual-gate diagnostics:\n" +
            string.Join("\n", residual.Select(d => d.ToMsBuildString())));
    }

    // 场景 1：类型以隐式（public T Current 实现 IEnumerator<T>.Current）与显式
    // （object IEnumerator.Current）两种方式实现元数据接口属性 → 保留原属性、
    // 使用点不改写，但必须无 CS2J5112。同一文件内另置一个可去糖属性（Holder.Value）
    // 以驱动 residual 闸门所在的重写器真正运行。
    [Fact]
    public void MetadataInterfaceProperty_ImplicitAndExplicit_RetainedWithoutResidualGate()
    {
        const string src = """
            using System.Collections;
            using System.Collections.Generic;

            internal sealed class Enumerator : IEnumerator<string>
            {
                public string Current { get; private set; } = "x";
                public bool MoveNext() => false;
                public void Reset() { }
                object IEnumerator.Current => Current;
                public void Dispose() { }
            }

            internal class Holder
            {
                public int Value { get; set; }
                public int Read() => Value;
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Iter.cs", src);

        AssertNoResidualProperty(diags);

        // 有意保留：迭代器属性仍为属性声明，绝不能被展开成 get_/set_ 方法
        // （否则接口槽丢失 → CS0535/CS0470/CS0683）。
        Assert.Contains("public string Current { get; private set; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("string get_Current", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("set_Current", outCode, StringComparison.Ordinal);

        // 普通属性照常去糖，证明重写器确实运行过（residual 闸门因此被触发）。
        Assert.Contains("get_Value", outCode, StringComparison.Ordinal);

        // residual 闸门命中不得计入 PlansSkipped（保留是有意为之，非跳过）。
        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 2：同一编译内同一完全限定名类型被声明两次——一处 partial、一处非 partial
    // （CLI 整树编译把不同工程/程序集的同名类型折成同一符号 → 基线 CS0260）。
    // 展开属性会与镜像声明撞访问器槽（CS0082/CS0111），故整体保留且必须无 CS2J5112。
    [Fact]
    public void MergedMirrorTypeDeclarations_PropertyRetainedWithoutResidualGate()
    {
        const string srcA = """
            namespace N
            {
                public partial class C
                {
                    public int P { get; set; }
                    public int ReadP() => P;
                }

                public class D
                {
                    public int V { get; set; }
                    public int ReadV() => V;
                }
            }
            """;

        // 同名类型：非 partial（镜像声明），与上面 partial 声明被整树编译折进同一符号。
        const string srcB = """
            namespace N
            {
                public class C
                {
                    public int Q { get; set; }
                }
            }
            """;

        var (outCode, stats, diags) =
            RewriteFirstFile("MirrorA.cs", srcA, ("MirrorB.cs", srcB));

        AssertNoResidualProperty(diags);

        // 镜像合并类型上的属性保留原声明（不得展开）。
        Assert.Contains("public int P { get; set; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_P", outCode, StringComparison.Ordinal);

        // 非镜像类型 D 上的属性照常去糖（证明重写器运行过）。
        Assert.Contains("get_V", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 3：C21 匿名类型提升产物（Cs2jAnonymous/Anon_*）保持「只读属性」生成契约。
    // 生成文件落在 solution root，[JsonInclude] 的工程作用域无从判定；降形会让
    // STJ 的按名序列化与构造器参数绑定整体失效（真实链 text.json 11 例）。
    [Fact]
    public void AnonymousTypeLiftProperty_RetainedWithoutResidualGate()
    {
        const string src = """
            namespace Cs2jAnonymous
            {
                internal sealed class Anon_Name_String
                {
                    public global::System.String Name { get; }

                    public Anon_Name_String(global::System.String name)
                    {
                        Name = name;
                    }

                    public override global::System.Boolean Equals(global::System.Object obj) => false;
                }
            }

            namespace N
            {
                public class Plain
                {
                    public int Value { get; set; }
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("_Cs2jAnonymousTypes.cs", src);

        AssertNoResidualProperty(diags);

        // 生成契约的只读属性原样保留（使用点 Name/构造器赋值不再改成 get_Name()）。
        Assert.Contains("public global::System.String Name { get; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Name", outCode, StringComparison.Ordinal);

        // 普通类型上的属性照常去糖（证明重写器确实运行过）。
        Assert.Contains("get_Value", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 4：运行时按名反射（System.Type.GetProperty）命中的类型整体保留。
    // 真实链 json：EnumTests.GetProperty(testClass.GetType().GetProperty(name)) 58 例，
    // 以及 typeof(T).GetProperty / [InlineData] Type 参数等反射族。
    [Fact]
    public void ReflectivelyLookedUpTypes_RetainedWithoutResidualGate()
    {
        const string src = """
            using System;
            using System.Reflection;

            namespace Probe
            {
                [AttributeUsage(AttributeTargets.Method)]
                public sealed class InlineDataAttribute : Attribute
                {
                    public InlineDataAttribute(object data) { }
                }

                public class BaseDto
                {
                    public int BaseValue { get; set; }
                }

                public class SimpleDto : BaseDto
                {
                    public int Value { get; set; }
                }

                // 仅通过 GetProperties 整型枚举访问：属性数（STJ 缓存回退字典阈值）也是契约。
                public class EnumOnlyDto
                {
                    public int Count { get; set; }
                }

                public class UnrelatedDto
                {
                    public int Age { get; set; }
                }

                public static class Sites
                {
                    public static object ByInstance(SimpleDto dto, string name)
                        => dto.GetType().GetProperty(name).GetValue(dto);

                    public static object ByType(string name)
                        => typeof(SimpleDto).GetProperty(name).GetValue(null);

                    public static PropertyInfo[] All()
                        => typeof(EnumOnlyDto).GetProperties(BindingFlags.Instance | BindingFlags.Public);

                    [InlineData(typeof(SimpleDto))]
                    public static object ByTypeParameter(Type type, string name)
                        => type.GetProperty(name).GetValue(null);
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Sites.cs", src);

        AssertNoResidualProperty(diags);

        // 反射命中类型（含基类）保持原属性声明与原名成员访问。
        Assert.Contains("public int Value { get; set; }", outCode, StringComparison.Ordinal);
        Assert.Contains("public int BaseValue { get; set; }", outCode, StringComparison.Ordinal);
        Assert.Contains("public int Count { get; set; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Value", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_BaseValue", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Count", outCode, StringComparison.Ordinal);
        Assert.Contains("dto.GetType()", outCode, StringComparison.Ordinal);

        // 未被反射命中的类型照常去糖。
        Assert.Contains("get_Age", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 5：Type 参数经 [MemberData(nameof(...))] 数据源注入 typeof(T)（真实链 json：
    // PropertyVisibilityTests.JsonIgnoreCondition*/ExtensionData 一组，数据源是生成的
    // 嵌套 iterator 类，typeof(...) 写在 MoveNext 里）。
    [Fact]
    public void MemberDataInjectedTypes_RetainedWithoutResidualGate()
    {
        const string src = """
            using System;
            using System.Collections.Generic;

            namespace Probe
            {
                [AttributeUsage(AttributeTargets.Method)]
                public sealed class MemberDataAttribute : Attribute
                {
                    public MemberDataAttribute(string name) { }
                }

                public class SimpleDto
                {
                    public int Value { get; set; }
                }

                public static class Holder
                {
                    [MemberData(nameof(Data))]
                    public static object Check(Type type, string name)
                        => type.GetProperty(name).GetValue(null);

                    private static IEnumerable<object[]> Data() => new[]
                    {
                        new object[] { typeof(SimpleDto) },
                    };
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Holder.cs", src);

        AssertNoResidualProperty(diags);
        Assert.Contains("public int Value { get; set; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Value", outCode, StringComparison.Ordinal);
        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 6：只读/只写属性的序列化契约（真实链 PropertyVisibilityTests：
    // IgnoreReadOnlyProperties / IgnoreCondition*WinsOver_IgnoreReadOnly* / NoGetter）。
    [Fact]
    public void ReadOnlyAndWriteOnlyProperties_RetainedWithoutResidualGate()
    {
        const string src = """
            namespace Probe
            {
                public class ReadOnlyDto
                {
                    public int Value { get; } = 1;
                    public int Settable { get; set; }
                }

                public class WriteOnlyDto
                {
                    private string _value = "";

                    public string Value { set { _value = value; } }

                    public string Read() => _value;
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Visibility.cs", src);

        AssertNoResidualProperty(diags);

        // get-only 自动属性保留（否则 readonly 字段让 IgnoreReadOnlyProperties 失效）。
        Assert.Contains("public int Value { get; } = 1;", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Value", outCode, StringComparison.Ordinal);

        // 无 getter 的写属性保留（否则成员从 STJ 反射面消失，反序列化不再写入）。
        Assert.Contains("public string Value { set { _value = value; } }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("set_Value", outCode, StringComparison.Ordinal);

        // 普通可读写属性照常去糖。
        Assert.Contains("get_Settable", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 7：继承链同名属性（override / new-slot / 隐式 hiding）整链保留。
    // C46 NH 会改名，序列化名与 STJ 的 hiding 判定都依赖 CLR 成员名相同。
    [Fact]
    public void InheritanceHiddenProperties_RetainedWithoutResidualGate()
    {
        const string src = """
            namespace Probe
            {
                public class BaseDto
                {
                    public virtual int Value { get; set; }
                }

                public class NewSlotDto : BaseDto
                {
                    public new int Value { get; set; }
                }

                public class OverrideDto : BaseDto
                {
                    public override int Value { get; set; }
                }

                public class PlainDto
                {
                    public int Other { get; set; }
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Hidden.cs", src);

        AssertNoResidualProperty(diags);

        Assert.Contains("public virtual int Value { get; set; }", outCode, StringComparison.Ordinal);
        Assert.Contains("public new int Value { get; set; }", outCode, StringComparison.Ordinal);
        Assert.Contains("public override int Value { get; set; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Value", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Value_cs2jNew", outCode, StringComparison.Ordinal);

        // 无继承同名冲突的类型照常去糖。
        Assert.Contains("get_Other", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 8：STJ 消费面内的 full（非自动）属性保留——没有后备字段可挂 [JsonInclude]，
    // 降形成方法会让成员整体消失出反射面（真实链 json：集合 getter / 显式后备字段往返）。
    [Fact]
    public void FullPropertyInJsonScope_RetainedWithoutResidualGate()
    {
        const string withJsonScope = """
            using System.Text.Json.Serialization;

            namespace Probe
            {
                public class Dto
                {
                    private string _items = "";

                    public string Items { get => _items; set => _items = value; }

                    public int Computed => 1;
                }
            }
            """;

        var (jsonOut, stats, diags) = RewriteFirstFile("JsonScope.cs", withJsonScope);
        AssertNoResidualProperty(diags);
        Assert.Contains("public string Items { get => _items; set => _items = value; }",
            jsonOut, StringComparison.Ordinal);
        Assert.Contains("public int Computed => 1;", jsonOut, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Items", jsonOut, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Computed", jsonOut, StringComparison.Ordinal);

        // 非序列化消费面：维持 PD-2「访问器 → 方法」归约。
        const string plain = """
            namespace Plain
            {
                public class Dto
                {
                    private string _value = "";

                    public string Value { get => _value; set => _value = value; }
                }
            }
            """;

        var (plainOut, _, _) = RewriteFirstFile("PlainScope.cs", plain);
        Assert.Contains("get_Value", plainOut, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 9：泛型 helper 链上的反射接收者解析（真实链 json：
    // VerifyIgnore<T> → verify<T> → getProperty<T> → T.GetType().GetProperty(name)）。
    // 方法类型参数必须沿调用点实参逐层追踪到具体类型。
    [Fact]
    public void GenericHelperChain_RetainsCallSiteTypesWithoutResidualGate()
    {
        const string src = """
            using System.Collections.Generic;

            namespace Probe
            {
                public class DictionaryDto
                {
                    public IDictionary<string, int> Parsed1 { get; set; }
                }

                public class PlainDto
                {
                    public IDictionary<string, int> Other { get; set; }
                }

                public static class Helper
                {
                    private static IDictionary<string, int> GetProperty<T>(T obj, string name)
                        => (IDictionary<string, int>)obj.GetType().GetProperty(name).GetValue(obj);

                    private static void Verify<T>(bool skip1, bool skip2, bool skip3, T obj)
                    {
                        _ = GetProperty<T>(obj, "Parsed1");
                    }

                    private static void VerifyIgnore<T>(bool skip1, bool skip2, bool skip3, bool addMissing)
                    {
                        T obj = default!;
                        Verify<T>(skip1, skip2, skip3, obj);
                    }

                    private static void VerifyIgnore<T>(bool skip1, bool skip2, bool skip3)
                    {
                        VerifyIgnore<T>(skip1, skip2, skip3, false);
                    }

                    public static void Run()
                    {
                        VerifyIgnore<DictionaryDto>(false, false, true);
                    }
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Helper.cs", src);

        AssertNoResidualProperty(diags);

        Assert.Contains("public IDictionary<string, int> Parsed1 { get; set; }",
            outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Parsed1", outCode, StringComparison.Ordinal);

        // 链外类型照常去糖。
        Assert.Contains("get_Other", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 10：状态机（iterator lowering）捕获字段上的整型枚举——`_type` 无初始化器、
    // 由构造器赋值，接收者解析不到具体类型，退化为「同文件 typeof 源内类型」保守保留。
    // 真实链 json：InvalidJsonForTypeShouldFail 的 `GetProperties(...)[0]` 越界。
    [Fact]
    public void UnresolvedEnumerationReceiver_FallsBackToSameFileTypeOfs()
    {
        const string src = """
            using System;
            using System.Reflection;

            namespace Probe
            {
                public class DtoA
                {
                    public string Value { get; set; }
                }

                public class DtoB
                {
                    public int Size { get; set; }
                }

                public class DtoUnrelated
                {
                    public int Other { get; set; }
                }

                public sealed class StateMachine
                {
                    private readonly Type _type;

                    public StateMachine(Type type) { _type = type; }

                    public PropertyInfo First()
                        => _type.GetProperties(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
                }

                public static class Uses
                {
                    public static void Run()
                    {
                        _ = new StateMachine(typeof(DtoA));
                        _ = new StateMachine(typeof(DtoB)).First();
                    }
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("StateMachine.cs", src);

        AssertNoResidualProperty(diags);

        Assert.Contains("public string Value { get; set; }", outCode, StringComparison.Ordinal);
        Assert.Contains("public int Size { get; set; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Value", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Size", outCode, StringComparison.Ordinal);

        // 未出现在 typeof 的类型照常去糖。
        Assert.Contains("get_Other", outCode, StringComparison.Ordinal);

        Assert.Equal(0, stats.PlansSkipped);
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 11：类型级成员面一致性——STJ 先枚举属性再枚举字段，同一类型里只要有一个
    // 属性形态成员（这里 [PropertyOnly] 特性不可路由到字段 → 声明门禁失败），其余
    // 属性脱糖成字段会被排到它后面（真实链 json：ConvertEnumUsingAttributes 的 JSON
    // 顺序变成 WeekEnd 领先）。序列化消费面上整类型属性一并保留。
    [Fact]
    public void TypeWithRemainingProperty_RetainsSiblingPropertiesInJsonScope()
    {
        const string src = """
            using System;
            using System.Text.Json.Serialization;

            namespace Probe
            {
                [AttributeUsage(AttributeTargets.Property)]
                public sealed class PropertyOnlyAttribute : Attribute
                {
                }

                public class WeekDto
                {
                    [PropertyOnly]
                    public DayOfWeek WorkStart { get; set; }

                    public DayOfWeek WorkEnd { get; set; }
                }

                public class PlainDto
                {
                    public DayOfWeek Holiday { get; set; }
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Uniformity.cs", src);

        AssertNoResidualProperty(diags);

        Assert.Contains("public DayOfWeek WorkStart { get; set; }",
            outCode, StringComparison.Ordinal);
        Assert.Contains("public DayOfWeek WorkEnd { get; set; }",
            outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_WorkStart", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_WorkEnd", outCode, StringComparison.Ordinal);

        // 没有属性形态成员的类型照常去糖。
        Assert.Contains("get_Holiday", outCode, StringComparison.Ordinal);

        // WorkStart 的不可路由特性触发声明门禁失败（Plan 记为跳过），属预期诊断。
        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }

    // 场景 12：接口属性在序列化消费面保留——STJ 按声明类型（接口）解析成员面，接口属性
    // 降形成接口方法后接口 PropertyInfo 为空，声明类型为接口的成员序列化成 {}（真实链
    // json：CustomObjectConverterTests.AssertShouldSerializeTest 的 Thing:{}）。
    // 实现类经层次一致性同留，避免 CS0535。
    [Fact]
    public void InterfacePropertyInJsonScope_RetainsInterfaceAndImplementation()
    {
        const string src = """
            using System.Text.Json.Serialization;

            namespace Probe
            {
                public interface IThing
                {
                    int Number { get; set; }
                }

                public class ThingDto : IThing
                {
                    public int Number { get; set; }
                }

                public class PlainDto
                {
                    public int Value { get; set; }
                }
            }
            """;

        var (outCode, stats, diags) = RewriteFirstFile("Interface.cs", src);

        AssertNoResidualProperty(diags);

        Assert.Contains("int Number { get; set; }", outCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get_Number", outCode, StringComparison.Ordinal);

        // 非接口成员照常去糖。
        Assert.Contains("get_Value", outCode, StringComparison.Ordinal);

        Assert.False(stats.SkippedByReason.ContainsKey(ResidualCode));
    }
}
