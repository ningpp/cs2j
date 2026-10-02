using System.IO;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Signatures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C46 Signatures M1 Core 单测矩阵（设计 §7.1 审计面）：内联源码 → CSharpCompilation →
/// MemberDesugarPreprocessor.RewriteFile。断言：
/// (a) Enabled=false（默认）⇒ 字节一致输出、零诊断、零签名计数；
/// (b) Enabled=true ⇒ 显式实现/隐藏/协变三类计数正确 + CS2J5190 Info；
/// (c) 二次运行零 diff；(d) 输出可编译。
/// M1 只读审计；M2 起 Enabled=true 会改写显式接口方法面（SI-1/2/3/5），
/// 隐藏/协变两类仍只审计（M3/M4 消费）。
/// </summary>
public class MemberDesugarSignatureUnitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    /// <summary>含三类签名问题的样例：显式接口实现 + new 隐藏 + 协变返回。</summary>
    private const string ThreeCategorySource = @"
using System.Collections.Generic;
public interface IFoo { void Foo(); }
public class Animal { }
public class Dog : Animal { }
public class Base
{
    public virtual Animal Create() => new Animal();
    public string Name() => ""base"";
}
public class Derived : Base, IFoo
{
    void IFoo.Foo() { }
    public new string Name() => ""derived"";
    public override Dog Create() => new Dog();
}
";

    /// <summary>隐藏形态（隐式/newslot/静态）+ 协变（retained/bridged/valueType）矩阵。</summary>
    private const string FlavorMatrixSource = @"
using System.Collections.Generic;
public interface IFoo { void Foo(); }
public class Animal { }
public class Dog : Animal { }
public class Base
{
    public virtual Animal Create() => new Animal();
    public virtual IEnumerable<object> Items() => new List<object>();
    public string Name() => ""base"";
    public virtual void Ping() { }
    public static void Stat() { }
}
public class Mid : Base
{
    public new virtual void Ping() { }
}
public class Derived : Mid, IFoo
{
    void IFoo.Foo() { }
    public string Name() => ""derived"";
    public override Dog Create() => new Dog();
    public override IEnumerable<string> Items() => new List<string>();
    public static void Stat() { }
}
public class GenBase { public virtual object Make() => new object(); }
public class GenDerived<T> : GenBase where T : class
{
    public override T Make() => default!;
}
";

    private static (string Output, bool Changed, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags) Rewrite(string src, MemberDesugarOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "md-signature-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var pp = new MemberDesugarPreprocessor(options ?? new MemberDesugarOptions());
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, result.Changed, stats, diags);
    }

    private static MemberDesugarOptions Enabled() => new()
    {
        Signatures = new SignatureOptions { Enabled = true },
    };

    private static int SignatureCounterTotal(MemberDesugarStatistics s)
        => s.SignatureExplicitMembersDetected + s.SignatureHidingDetected + s.SignatureCovariantChains;

    // ---- (a) 默认关：零执行 ----

    [Fact]
    public void Disabled_IsInert_ByteIdentical_ZeroDiagnostics_ZeroCounters()
    {
        var (output, changed, stats, diags) = Rewrite(ThreeCategorySource);

        Assert.Equal(ThreeCategorySource, output);   // 字节一致
        Assert.False(changed);
        Assert.Empty(diags);
        Assert.Equal(0, SignatureCounterTotal(stats));
        Assert.Equal(0, stats.SignatureExplicitMethods);
        Assert.Equal(0, stats.SignatureHidingDetected);
        Assert.Equal(0, stats.SignatureCovariantChains);
    }

    [Fact]
    public void Disabled_FlavorMatrix_IsInert()
    {
        var (output, changed, stats, diags) = Rewrite(FlavorMatrixSource);

        Assert.Equal(FlavorMatrixSource, output);
        Assert.False(changed);
        Assert.Empty(diags);
        Assert.Equal(0, SignatureCounterTotal(stats));
    }

    // ---- (b) Enabled=true：审计计数 ----

    [Fact]
    public void Enabled_ThreeCategories_CorrectCounts_AndInfo()
    {
        var (_, changed, stats, diags) = Rewrite(ThreeCategorySource, Enabled());

        Assert.True(changed);   // M2：显式接口方法被提升（去限定名 + public）

        Assert.Equal(1, stats.SignatureExplicitMembersDetected);
        Assert.Equal(1, stats.SignatureExplicitMethods);
        Assert.Equal(0, stats.SignatureExplicitProperties);
        Assert.Equal(0, stats.SignatureExplicitIndexers);
        Assert.Equal(0, stats.SignatureExplicitEvents);

        Assert.Equal(1, stats.SignatureHidingDetected);
        Assert.Equal(1, stats.SignatureHidingNew);

        Assert.Equal(1, stats.SignatureCovariantChains);
        Assert.Equal(1, stats.SignatureCovariantRetained);

        var info = diags.SingleOrDefault(d =>
            d.Code == MemberDesugarDiagnosticCode.SignaturePlanCreated);
        Assert.NotNull(info);
        Assert.Equal(MemberDesugarSeverity.Info, info!.Severity);
    }

    [Fact]
    public void Enabled_FlavorMatrix_Buckets()
    {
        var (_, _, stats, diags) = Rewrite(FlavorMatrixSource, Enabled());

        Assert.Equal(1, stats.SignatureExplicitMembersDetected);
        Assert.Equal(1, stats.SignatureExplicitMethods);

        Assert.Equal(3, stats.SignatureHidingDetected);
        Assert.Equal(0, stats.SignatureHidingNew);
        Assert.Equal(1, stats.SignatureHidingImplicit);   // Derived.Name（无 new 令牌）
        Assert.Equal(1, stats.SignatureHidingNewSlot);    // Mid.Ping（new virtual）
        Assert.Equal(1, stats.SignatureHidingStatic);     // Derived.Stat（静态）

        Assert.Equal(3, stats.SignatureCovariantChains);
        Assert.Equal(1, stats.SignatureCovariantRetained);         // Dog over Animal
        Assert.Equal(1, stats.SignatureCovariantBridged);          // IEnumerable<string> over IEnumerable<object>
        Assert.Equal(1, stats.SignatureCovariantValueTypeSkipped); // 泛型 T（分析边界）

        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignaturePlanCreated);
    }

    [Fact]
    public void Enabled_ReportInfoFalse_SuppressesAuditInfo()
    {
        var options = new MemberDesugarOptions
        {
            Signatures = new SignatureOptions { Enabled = true, ReportInfo = false },
        };
        var (_, _, stats, diags) = Rewrite(ThreeCategorySource, options);

        Assert.True(stats.SignatureExplicitMembersDetected > 0); // 计数仍写
        Assert.DoesNotContain(diags,
            d => d.Code == MemberDesugarDiagnosticCode.SignaturePlanCreated);
    }

    [Fact]
    public void Enabled_NoSignatureConstructs_NoInfo()
    {
        const string src = "public class Plain { public int Add(int a, int b) => a + b; }";
        var (output, _, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);
        Assert.Equal(0, SignatureCounterTotal(stats));
        Assert.Empty(diags);
    }

    [Fact]
    public void Enabled_SkipSignatures_ExcludesMember()
    {
        var options = new MemberDesugarOptions
        {
            Signatures = new SignatureOptions
            {
                Enabled = true,
                SkipSignatures = new HashSet<string>(StringComparer.Ordinal)
                {
                    "Derived::Name",
                },
            },
        };
        var (_, _, stats, _) = Rewrite(ThreeCategorySource, options);

        Assert.Equal(0, stats.SignatureHidingDetected);          // Derived.Name 被跳过
        Assert.Equal(1, stats.SignatureExplicitMembersDetected); // 其余仍计
    }

    // ---- (c) 幂等：(d) 可编译 ----

    [Fact]
    public void Enabled_SecondRun_ZeroDiff_AndCompiles()
    {
        var (firstOutput, _, _, diags) = Rewrite(FlavorMatrixSource, Enabled());
        var (secondOutput, _, _, _) = Rewrite(firstOutput, Enabled());

        Assert.Equal(firstOutput, secondOutput);              // 二次运行零 diff（幂等）
        Assert.NotEqual(FlavorMatrixSource, firstOutput);     // M2：显式接口方法已被提升
        Assert.DoesNotContain("IFoo.Foo", firstOutput);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(firstOutput);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignaturePlanCreated);
    }

    [Fact]
    public void FlavorMatrixSource_Compiles()
        => MemberDesugarPropertyUnitTests.AssertOutputCompiles(FlavorMatrixSource);

    // ---- M2：显式接口方法面（SI-1/2/3/5）----

    [Fact]
    public void Promoted_NoCollision_NoModifier_AddsPublic_KeepsSlotName_Idempotent()
    {
        const string src = """
            public interface IFoo { void Foo(); }
            public class C : IFoo
            {
                void IFoo.Foo() { }
            }
            public class Use
            {
                public void Call(IFoo i) { i.Foo(); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public void Foo() { }");
        AssertNotContains(output, "IFoo.Foo");
        AssertContains(output, "i.Foo();");   // 接口分派点不动
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);         // 二次运行零 diff
    }

    [Fact]
    public void Collision_InterfaceTakesSlot_ClassMemberRenamed_CallSitesClassified()
    {
        const string src = """
            public interface IFoo { void Foo(); }
            public class C : IFoo
            {
                public void Foo() { System.Console.WriteLine("class"); }
                void IFoo.Foo() { System.Console.WriteLine("iface"); }
                public void Use(IFoo i, C c) { i.Foo(); c.Foo(); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public void Foo_cs2jClass()");
        AssertContains(output, "public void Foo()");
        AssertContains(output, "i.Foo(); c.Foo_cs2jClass();");   // 两类静态类型各归各位
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.Equal(1, stats.SignatureDisplacedRenamed);
        Assert.Equal(1, stats.SignatureCallSitesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void DualInterface_EquivalentBodies_MergeToOnePublicMethod_SatisfiesBoth()
    {
        const string src = """
            public interface IA { void Foo(); }
            public interface IB { void Foo(); }
            public class C : IA, IB
            {
                void IA.Foo() { System.Console.WriteLine("x"); }
                void IB.Foo() { System.Console.WriteLine("x"); }
                public void Use(IA a, IB b) { a.Foo(); b.Foo(); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertNotContains(output, "IA.Foo");
        AssertNotContains(output, "IB.Foo");
        AssertContains(output, "public void Foo()");
        AssertContains(output, "a.Foo(); b.Foo();");
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.Equal(1, stats.SignatureDualInterfaceMerged);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);
    }

    [Fact]
    public void DualInterface_DivergentBodies_CS2J5191_DeclarationUntouched_ByteIdentical()
    {
        const string src = """
            public interface IA { void Foo(); }
            public interface IB { void Foo(); }
            public class C : IA, IB
            {
                void IA.Foo() { System.Console.WriteLine("a"); }
                void IB.Foo() { System.Console.WriteLine("b"); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // plan 原子不动、字节一致
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureDivergentInterfaceBodies);
        Assert.Equal(0, stats.SignaturePlansPromoted);
    }

    [Fact]
    public void GenericInterface_SlotNameCarriesNoTypeArguments()
    {
        const string src = """
            public interface IFoo<T> { void Bar(); }
            public class C : IFoo<int>
            {
                void IFoo<int>.Bar() { }
            }
            public class Use { public void Call(IFoo<int> f) { f.Bar(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertNotContains(output, "IFoo<int>.Bar");
        AssertContains(output, "public void Bar()");
        AssertContains(output, "f.Bar();");
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);
    }

    [Fact]
    public void Receivers_InterfaceFormsUntouched_MethodGroupRenamed_BaseUntouched()
    {
        const string src = """
            using System;
            public interface IFoo { void Foo(); }
            public class B { public void Ping() { } }
            public class C : B, IFoo
            {
                public IFoo F = null!;
                public void Foo() { }
                void IFoo.Foo() { }
                public void Use(IFoo p)
                {
                    IFoo v = p;
                    v.Foo();
                    p.Foo();
                    F.Foo();
                    var c = new C();
                    ((IFoo)c).Foo();
                    var x = c as IFoo;
                    if (x != null) x.Foo();
                    if (c is IFoo f) f.Foo();
                    Action a = c.Foo;
                    base.Ping();
                }
                public void UseGeneric<T>(T t) where T : IFoo { t.Foo(); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        // (a)(b)(c) 五类接口接收者：接口分派，全部不动。
        AssertContains(output, "v.Foo();");
        AssertContains(output, "p.Foo();");
        AssertContains(output, "F.Foo();");
        AssertContains(output, "((IFoo)c).Foo();");
        AssertContains(output, "x.Foo();");
        AssertContains(output, "f.Foo();");
        AssertContains(output, "t.Foo();");
        AssertContains(output, "base.Ping();");        // base 调用绑定基类成员 → 不动
        AssertContains(output, "c.Foo_cs2jClass");     // (d) 方法组绑定让位成员 → 改名
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.Equal(1, stats.SignatureDisplacedRenamed);
        Assert.Equal(1, stats.SignatureCallSitesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);
    }

    [Fact]
    public void CrossFile_Interface_Implementation_CallSite_ThreeFiles()
    {
        const string iface = "public interface IFoo { void Foo(); }";
        const string impl = """
            public class C : IFoo
            {
                public void Foo() { }
                void IFoo.Foo() { }
            }
            """;
        const string use = """
            public class Use { public void Call(IFoo i, C c) { i.Foo(); c.Foo(); } }
            """;
        var (outputs, stats, diags) = RewriteAll(Enabled(),
            ("IFoo.cs", iface), ("C.cs", impl), ("Use.cs", use));

        Assert.Equal(iface, outputs[0]);   // 接口声明文件零 diff
        AssertContains(outputs[1], "public void Foo_cs2jClass()");
        AssertContains(outputs[1], "public void Foo()");
        AssertContains(outputs[2], "i.Foo(); c.Foo_cs2jClass();");
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.Equal(1, stats.SignatureDisplacedRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outputs[1], outputs[0], outputs[2]);
    }

    [Fact]
    public void ExpressionTreeSite_CS2J5194_DeclarationNotRewritten()
    {
        const string src = """
            using System;
            using System.Linq.Expressions;
            public interface IFoo { void Foo(); }
            public class C : IFoo
            {
                public void Foo() { }
                void IFoo.Foo() { }
                public void Use()
                {
                    Expression<Action> e = () => Foo();
                }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // plan 原子不动（含声明）
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureExpressionTreeOrDynamic);
        Assert.Equal(0, stats.SignaturePlansPromoted);
    }

    [Fact]
    public void NameofLiteralized_ReflectionSiteWarnedAndUntouched()
    {
        const string src = """
            using System;
            using System.Reflection;
            public interface IFoo { void Foo(); }
            public class C : IFoo
            {
                public void Foo() { }
                void IFoo.Foo() { }
                public string Name1() => nameof(Foo);
                public MethodInfo? Refl() => typeof(C).GetMethod("Foo");
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "=> \"Foo\";");          // nameof 字面量化
        AssertNotContains(output, "nameof(Foo)");
        AssertContains(output, "GetMethod(\"Foo\")");   // 反射站点文本不变
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureReflectionContract);
        Assert.Equal(1, stats.SignatureReflectionWarnings);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);
    }

    // ---- 多文件重写（跨文件 plan TouchedFiles 闭合）----

    private static (string[] Outputs, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diags)
        RewriteAll(MemberDesugarOptions options, params (string Path, string Src)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Src, ParseOptions, path: f.Path)).ToArray();
        var compilation = CSharpCompilation.Create(
            "md-signature-multi", trees, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var preprocessor = new MemberDesugarPreprocessor(options);
        preprocessor.BindCompilation(compilation);
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var outputs = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var result = preprocessor.RewriteFile(
                tree.GetCompilationUnitRoot().ToFullString(),
                compilation.GetSemanticModel(tree), stats, diags);
            outputs.Add(result.OutputCode);
        }
        return (outputs.ToArray(), stats, diags);
    }

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    private static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}