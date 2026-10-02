using System.IO;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Signatures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C46 Signatures M1 Core 单测矩阵（设计 §7.1 审计面）：内联源码 → CSharpCompilation →
/// MemberDesugarPreprocessor.RewriteFile。断言：
/// (a) Enabled=false（默认）⇒ 字节一致输出、零诊断、零签名计数；
/// (b) Enabled=true ⇒ 显式实现/隐藏/协变三类计数正确 + CS2J5190 Info；
/// (c) 二次运行零 diff；(d) 输出可编译。
/// M1 为只读审计（Enabled=true 也不改写树）。
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
        var (output, changed, stats, diags) = Rewrite(ThreeCategorySource, Enabled());

        Assert.Equal(ThreeCategorySource, output);   // M1 只读审计，不改树
        Assert.False(changed);

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

        Assert.Equal(firstOutput, secondOutput);
        Assert.Equal(FlavorMatrixSource, firstOutput);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(firstOutput);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignaturePlanCreated);
    }

    [Fact]
    public void FlavorMatrixSource_Compiles()
        => MemberDesugarPropertyUnitTests.AssertOutputCompiles(FlavorMatrixSource);

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