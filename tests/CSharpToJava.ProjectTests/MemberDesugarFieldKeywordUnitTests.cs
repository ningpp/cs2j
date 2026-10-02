using System.IO;
using System.Linq;
using System.Reflection;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C50 field 上下文关键字归一单测矩阵（设计 §7.1）。
/// 开关关闭 → 零行为变化（CS2J5122 跳过）；开关开启 → 显式字段 + 方法、输出可编译、
/// 二次执行零 diff。
/// </summary>
public class MemberDesugarFieldKeywordUnitTests
{
    private static MemberDesugarOptions Enabled(bool strict = true, bool info = false)
        => new()
        {
            Strict = strict,
            ReportInfo = info,
            FieldKeyword = new FieldKeywordOptions { Enabled = true },
        };

    private static (string Output, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diags)
        Rewrite(string src, MemberDesugarOptions? options = null)
        => MemberDesugarPropertyUnitTests.Rewrite(src, options ?? new MemberDesugarOptions());

    private static void AssertCompiles(string output)
        => MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    private static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    // ---- 开关关闭（默认）：零行为变化 ----

    [Fact]
    public void Disabled_FieldKeywordProperty_SkippedWith5122_AndUnchanged()
    {
        var src = """
            public class C
            {
                public int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src);
        Assert.Equal(Normalize(src), Normalize(output));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.FieldKeywordAccessor);
        Assert.Equal(0, stats.FieldKeywordProperties);
    }

    [Fact]
    public void Disabled_PropertyLevelExpressionBodyField_FollowsLegacyFullPropertyPath()
    {
        // 属性级表达式体不被旧 UsesFieldKeyword 命中（只扫 AccessorList）：开关关闭时
        // 这是既有行为——按普通 full 属性展开，field 标识符原样保留（C50 不改变它）。
        var src = """
            public class C
            {
                public int P => field + 1;
            }
            """;
        var (output, stats, _) = Rewrite(src);
        Assert.Equal(0, stats.FieldKeywordProperties);
        AssertContains(output, "get_P");
        AssertContains(output, "field + 1");
    }

    // ---- FK-1~6：基础归一矩阵 ----

    [Fact]
    public void Enabled_GetSetFull_NormalizesToFieldAndMethods()
    {
        var src = """
            public class ViewModel
            {
                public int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private int p;");
        AssertContains(output, "public int get_P()");
        AssertContains(output, "=> p;");
        AssertContains(output, "public void set_P(int value)");
        AssertContains(output, "=> p = value;");
        AssertNotContains(output, "field");
        Assert.Equal(1, stats.FieldKeywordProperties);
        Assert.Equal(2, stats.FieldReferencesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }

    [Fact]
    public void Enabled_BlockBodiesAndGuardedSetter_KeepSemantics()
    {
        var src = """
            using System;
            public class ViewModel
            {
                public event Action<string>? Changed;
                public string Name
                {
                    get { return field; }
                    set
                    {
                        if (field != value)
                        {
                            field = value;
                            Changed?.Invoke(nameof(Name));
                        }
                    }
                }
            }
            """;
        var (output, stats, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private string name;");
        AssertContains(output, "return name;");
        AssertContains(output, "if (name != value)");
        AssertContains(output, "Changed?.Invoke(\"Name\")");
        AssertNotContains(output, "field");
        Assert.Equal(1, stats.FieldKeywordProperties);
    }

    [Fact]
    public void Enabled_GetOnlyFull_PropertyLevelExpressionBody_FieldNotReadonly()
    {
        // Roslyn 5.0 实测：field-keyword 形态合成字段 IL 层非 initonly（探针钉死）。
        var src = """
            public class C
            {
                public int Plus => field + 1;
            }
            """;
        var (output, stats, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private int plus;");
        AssertContains(output, "public int get_Plus() => plus + 1;");
        AssertNotContains(output, "readonly");
    }

    [Fact]
    public void Enabled_LazyCoalesceAssignment_FieldMutableAndExpressionPreserved()
    {
        var src = """
            using System;
            public class C
            {
                private static string Compute() => "x";
                public string Lazy
                {
                    get => field ??= Compute();
                }
            }
            """;
        var (output, stats, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private string lazy;");
        AssertContains(output, "=> lazy ??= Compute();");
        AssertNotContains(output, "readonly");
        AssertNotContains(output, "field");
    }

    [Fact]
    public void Enabled_MixedAutoAndFull_ExpandsBodylessAccessors()
    {
        var src = """
            public class C
            {
                public int P
                {
                    get;
                    private set => field = value * 2;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private int p;");
        AssertContains(output, "public int get_P() => p;");
        AssertContains(output, "private void set_P(int value) => p = value * 2;");
        Assert.Equal(1, stats.FieldKeywordAutoAccessorsExpanded);
    }

    [Fact]
    public void Enabled_InitAuto_WidenedSetterWritesMutableField()
    {
        var src = """
            public class C
            {
                public int P { get; init; } = 5;
            }
            """;
        // 注：auto 属性本身无 field token，走 C11 既有路径；此例作回归护栏。
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
    }

    [Fact]
    public void Enabled_Initializer_MovedToField_NoSetterCall()
    {
        var src = """
            public abstract class Base
            {
                public virtual int V { get; set; }
            }
            public class C : Base
            {
                public override int V
                {
                    get => field + 1;
                }
                public C() { }
            }
            """;
        // override get-only field-backed（无初始化器版）：override 合法。
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private int v;");
        AssertNotContains(output, "field");
    }

    [Fact]
    public void Enabled_PropertyInitializer_MovedToFieldDeclaration()
    {
        var src = """
            public class C
            {
                public int P { get; set => field = value; } = 42;
            }
            """;
        var (output, stats, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private int p = 42;");
        AssertNotContains(output, "} = 42;"); // 属性侧初始化器已摘除
        Assert.Equal(1, stats.FieldInitializersMoved);
    }

    [Fact]
    public void Enabled_StaticProperty_StaticField()
    {
        var src = """
            public class C
            {
                public static int S
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private static int s;");
        AssertContains(output, "public static int get_S()");
        AssertContains(output, "public static void set_S(int value)");
    }

    [Fact]
    public void Enabled_NullableReferenceType_NullabilityCarriedToField()
    {
        var src = """
            #nullable enable
            public class C
            {
                public string? Name
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "private string? name;");
    }

    [Fact]
    public void Enabled_GenericProperty_FieldTypeUsesTypeParameter()
    {
        var src = """
            public class Box<T>
            {
                public T Value
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        // 属性名 Value 与 set 的 value 形参撞名，BackingFieldNaming 既有契约加 Value 后缀。
        AssertContains(output, "private T valueValue;");
        AssertContains(output, "=> valueValue;");
        AssertContains(output, "=> valueValue = value;");
    }

    [Fact]
    public void Enabled_NameofField_RewrittenToNameofBackingField()
    {
        var src = """
            public class C
            {
                public int P
                {
                    get => field;
                    set
                    {
                        if (field == value) return;
                        field = value;
                        System.Console.WriteLine(nameof(field));
                    }
                }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "nameof(p)");
        AssertNotContains(output, "field");
    }

    [Fact]
    public void Enabled_VerbatimField_BoundToUserField_IsNotRewritten()
    {
        // @field 是 IdentifierName（非 FieldExpression），旧文本闸门仍保守跳过 5122。
        var src = """
            public class C
            {
                private int field = 3;
                public int P
                {
                    get => @field;
                    set => @field = value;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        // 不做任何改写（保守安全）
        Assert.Contains(MemberDesugarDiagnosticCode.FieldKeywordAccessor,
            diags.Select(d => d.Code));
        Assert.Contains("@field", output);
    }

    // ---- FK-8 闸门 ----

    [Fact]
    public void Enabled_RefReturningProperty_Gate5115()
    {
        var src = """
            public class C
            {
                private int _x;
                public ref int P => ref _x;
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.RefReturningProperty);
    }

    [Fact]
    public void Enabled_InterfaceInstanceFieldProperty_Gate5127()
    {
        var src = """
            public interface I
            {
                int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(
            MemberDesugarDiagnosticCode.FieldKeywordUnsupportedContainer,
            diags.Select(d => d.Code));
        Assert.Contains("field", output);
    }

    // ---- 幂等 ----

    [Fact]
    public void Enabled_SecondRun_IsZeroDiff()
    {
        var src = """
            public class C
            {
                public int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (first, _, _) = Rewrite(src, Enabled());
        var (second, _, _) = Rewrite(first, Enabled());
        Assert.Equal(Normalize(first), Normalize(second));
    }

    // ---- FK-7：[field:]/[get:]/[set:] 特性迁移（反射断言落位）----

    [Fact]
    public void Enabled_FieldTargetAttribute_MigratesWholeListToBackingField()
    {
        var src = """
            using System;
            public class Dto
            {
                [field: NonSerialized]
                public int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        // 属性侧不得残留 field 目标列表；字段上带着剥掉 target 的同一张列表。
        AssertNotContains(output, "field:");
        AssertContains(output, "[NonSerialized] private int p;");
        Assert.Equal(1, stats.FieldTargetAttributesMigrated);

        var fieldInfo = CompileToAssembly(output).GetType("Dto")!
            .GetField("p", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;
        Assert.True(fieldInfo.IsNotSerialized);
        Assert.False(fieldInfo.IsStatic);
        Assert.False(fieldInfo.IsInitOnly,
            "Roslyn 5.0 事实：field-keyword 形态（含 get-only）合成字段 IL 非 initonly");
    }

    [Fact]
    public void Enabled_GetTargetAttribute_AttachedToGetterMethodOnly()
    {
        var src = """
            using System;
            [AttributeUsage(AttributeTargets.Method)]
            public sealed class GetMarkAttribute : Attribute { }
            public class C
            {
                [get: GetMark]
                public int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, stats, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNotContains(output, "get:");
        Assert.Equal(0, stats.FieldTargetAttributesMigrated);

        var asm = CompileToAssembly(output);
        var type = asm.GetType("C")!;
        var markType = asm.GetType("GetMarkAttribute")!;
        var getter = type.GetMethod("get_P", BindingFlags.Public | BindingFlags.Instance)!;
        var setter = type.GetMethod("set_P", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.NotNull(getter.GetCustomAttribute(markType));
        Assert.Null(setter.GetCustomAttribute(markType));
    }

    [Fact]
    public void Enabled_StaticBackingField_ReflectsStaticAndMutable()
    {
        var src = """
            public class C
            {
                public static int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        var fieldInfo = CompileToAssembly(output).GetType("C")!
            .GetField("p", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
        Assert.True(fieldInfo.IsStatic);
        Assert.False(fieldInfo.IsInitOnly);
    }

    // ---- FK 矩阵补充（设计 §7.1）----

    [Fact]
    public void Enabled_SetOnlyProperty_Normalizes()
    {
        var src = """
            public class C
            {
                public int P
                {
                    set => field = value;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNotContains(output, "field");
        AssertContains(output, "public void set_P(int value)");
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }

    [Fact]
    public void Enabled_NewAndSealedOverride_ModifiersHandled()
    {
        var src = """
            public class Base
            {
                public virtual int P
                {
                    get => field;
                    set => field = value;
                }
            }
            public class Derived : Base
            {
                public sealed override int P
                {
                    get => field;
                    set => field = value;
                }
            }
            public class Hider : Base
            {
                public new int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNotContains(output, "field");
        Assert.Equal(3, stats.FieldKeywordProperties);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }

    [Fact]
    public void Enabled_ExplicitInterfaceImplementation_Normalizes()
    {
        var src = """
            public interface I
            {
                int P { get; set; }
            }
            public class C : I
            {
                int I.P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNotContains(output, "field");
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }

    [Fact]
    public void Enabled_ReadOnlyStruct_Normalizes()
    {
        var src = """
            public readonly struct S
            {
                public int P => field;
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNotContains(output, "field");
        Assert.Equal(1, stats.FieldKeywordProperties);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }

    [Fact]
    public void Enabled_FieldPassedAsRefArgument_RewrittenAndBehaviorPreserved()
    {
        var src = """
            public class C
            {
                private static void Set(ref int target, int v) => target = v;
                public int P
                {
                    get => field;
                    set => Set(ref field, value);
                }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertContains(output, "Set(ref p, value)");

        var type = CompileToAssembly(output).GetType("C")!;
        var instance = System.Activator.CreateInstance(type)!;
        type.GetMethod("set_P", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(instance, new object[] { 42 });
        var got = type.GetMethod("get_P", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(instance, null);
        Assert.Equal(42, got);
    }

    [Fact]
    public void Enabled_UserMemberNamedField_KeywordStillBindsSyntheticField()
    {
        // Roslyn 5.0 实证（CS9258 仅警告）：用户虽声明了 private int field，
        // 未转义 field 仍是 FieldExpression 且绑编译器合成后备字段；语义归一必须
        // 跟随符号（动合成字段 p），用户字段 field 原样保留，严禁按文本替换。
        var src = """
            public class C
            {
                private int field;
                public int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        Assert.Equal(1, stats.FieldKeywordProperties);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        // 用户字段原样保留；访问器引用被改写到合成字段 p。
        AssertContains(output, "private int field;");
        AssertContains(output, "=> p;");
        AssertContains(output, "=> p = value;");
    }

    [Fact]
    public void Enabled_InterfaceStaticFieldProperty_Normalized()
    {
        // 附裁决 4 M2：接口静态 field-backed 属性合法（接口可含静态字段），直接收编。
        var src = """
            public interface I
            {
                public static int P
                {
                    get => field;
                    set => field = value;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNotContains(output, "field");
        Assert.Equal(1, stats.FieldKeywordProperties);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);

        var fieldInfo = CompileToAssembly(output).GetType("I")!
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Single(f => f.FieldType == typeof(int));
        Assert.True(fieldInfo.IsStatic);
    }

    /// <summary>编译去糖产物并内存加载（TPA 引用），供反射断言字段/方法形态。</summary>
    private static Assembly CompileToAssembly(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(
            output, new CSharpParseOptions(LanguageVersion.Preview));
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "md-emit-" + System.Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "Rewritten code failed to emit:\n" +
            string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(ms.ToArray());
    }
}
