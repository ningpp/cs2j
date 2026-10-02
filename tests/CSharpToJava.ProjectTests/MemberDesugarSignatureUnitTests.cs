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

    // ---- M4c：访问器面跨文件站点按接收者静态类型回退绑定 ----

    [Fact]
    public void CrossFile_NhPropertyHiding_AccessorSitesRenamed_BothDirections()
    {
        // 声明在 file1（C11 方法化后为 get_/set_），站点在 file2：绑定失败 → 回退按接收者静态类型改名。
        const string decl = """
            public class Base { public string Name { get; set; } = ""; }
            public class Derived : Base { public new string Name { get; set; } = ""; }
            """;
        const string use = """
            public class Use
            {
                public void U(Base b, Derived d)
                {
                    d.Name = "x";
                    var y = d.Name;
                    b.Name = "y";
                    var z = b.Name;
                }
            }
            """;
        var (outputs, stats, diags) = RewriteAll(Enabled(),
            ("Decl.cs", decl), ("Use.cs", use));

        // 声明侧：派生属性方法化后配对改名（去 new）。
        AssertContains(outputs[0], "public string get_Name_cs2jNew()");
        AssertContains(outputs[0], "public void set_Name_cs2jNew(string value)");
        AssertNotContains(outputs[0], "new string get_Name");
        // 站点侧：派生静态类型站点改名（get_/set_ 配对），基类静态类型站点不动。
        AssertContains(outputs[1], "d.set_Name_cs2jNew(\"x\")");
        AssertContains(outputs[1], "d.get_Name_cs2jNew()");
        AssertContains(outputs[1], "b.set_Name(\"y\")");
        AssertContains(outputs[1], "b.get_Name()");
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outputs[0], outputs[1]);

        // 二次运行零 diff。
        var (second, _, _) = RewriteAll(Enabled(), ("Decl.cs", outputs[0]), ("Use.cs", outputs[1]));
        Assert.Equal(outputs[0], second[0]);
        Assert.Equal(outputs[1], second[1]);
    }

    [Fact]
    public void CrossFile_SiPropertyDisplacement_ClassTypedSiteRenamed_InterfaceTypedUntouched()
    {
        const string iface = "public interface IFoo { string Name { get; set; } }";
        const string impl = """
            public class C : IFoo
            {
                public string Name { get; set; } = "";
                string IFoo.Name { get; set; } = "";
            }
            """;
        const string use = """
            public class Use { public void U(IFoo i, C c) { i.Name = "a"; var x = c.Name; } }
            """;
        var (outputs, stats, diags) = RewriteAll(Enabled(),
            ("IFoo.cs", iface), ("C.cs", impl), ("Use.cs", use));

        // 让位类自有访问器对改名 get_/set_ 配对；接口实现体占规范名。
        AssertContains(outputs[1], "public string get_Name_cs2jClass()");
        AssertContains(outputs[1], "public void set_Name_cs2jClass(string value)");
        AssertContains(outputs[1], "public string get_Name()");
        // 站点侧：接口分派点不动；类静态类型站点（绑定失败）经回退改名。
        AssertContains(outputs[2], "i.set_Name(\"a\")");
        AssertContains(outputs[2], "c.get_Name_cs2jClass()");
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outputs[0], outputs[1], outputs[2]);

        var (second, _, _) = RewriteAll(Enabled(),
            ("IFoo.cs", outputs[0]), ("C.cs", outputs[1]), ("Use.cs", outputs[2]));
        Assert.Equal(outputs[0], second[0]);
        Assert.Equal(outputs[1], second[1]);
        Assert.Equal(outputs[2], second[2]);
    }

    [Fact]
    public void CrossFile_NhEventHiding_AddRemoveSitesRenamed()
    {
        const string decl = """
            using System;
            public class EBase { public event Action? E; }
            public class EDerived : EBase { public new event Action? E; }
            """;
        const string use = """
            using System;
            public class Use
            {
                public void U(EBase b, EDerived d)
                {
                    Action h = () => { };
                    d.E += h;
                    d.E -= h;
                    b.E += h;
                }
            }
            """;
        var (outputs, stats, diags) = RewriteAll(Enabled(),
            ("Decl.cs", decl), ("Use.cs", use));

        AssertContains(outputs[0], "add_E_cs2jNew");
        AssertContains(outputs[0], "remove_E_cs2jNew");
        AssertContains(outputs[1], "d.add_E_cs2jNew(h)");
        AssertContains(outputs[1], "d.remove_E_cs2jNew(h)");
        AssertContains(outputs[1], "b.add_E(h)");
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(outputs[0], outputs[1]);

        var (second, _, _) = RewriteAll(Enabled(), ("Decl.cs", outputs[0]), ("Use.cs", outputs[1]));
        Assert.Equal(outputs[0], second[0]);
        Assert.Equal(outputs[1], second[1]);
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

    // ---- M3：属性/索引器/事件显式实现（SI-1/2/5 访问器面）----

    [Fact]
    public void ExplicitProperty_Promoted_ClassOwnedAccessorsDisplaced_SitesRenamed()
    {
        const string src = """
            public interface IFoo { string Name { get; set; } }
            public class C : IFoo
            {
                public string Name { get; set; } = "";
                string IFoo.Name { get; set; } = "";
                public void Use(IFoo i, C c) { i.Name = "a"; c.Name = "b"; var x = c.Name; }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        // 接口实现体占规范名（public get_Set_Name），类自有访问器对让位改名（get_/set_ 配对保留）。
        AssertContains(output, "string get_Name();");
        AssertContains(output, "void set_Name(string value);");
        AssertContains(output, "public string get_Name_cs2jClass()");
        AssertContains(output, "public void set_Name_cs2jClass(string value)");
        AssertContains(output, "public string get_Name()");
        AssertContains(output, "public void set_Name(string value)");
        AssertNotContains(output, "IFoo.get_Name");
        AssertNotContains(output, "IFoo.set_Name");
        // 接口分派站点不动；类静态类型站点改名。
        AssertContains(output, "i.set_Name(\"a\");");
        AssertContains(output, "c.set_Name_cs2jClass(\"b\")");
        AssertContains(output, "c.get_Name_cs2jClass()");

        Assert.Equal(2, stats.SignaturePlansPromoted);
        Assert.Equal(2, stats.SignatureDisplacedRenamed);
        Assert.Equal(2, stats.SignatureCallSitesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void ExplicitIndexer_Promoted_ItemSlot_SitesClassifiedByReceiverType()
    {
        const string src = """
            public interface IFoo { int this[int i] { get; } }
            public class C : IFoo
            {
                int IFoo.this[int i] => i + 1;
                public void Use(IFoo i) { var x = i[0]; }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "int get_Item(int i);");
        AssertContains(output, "public int get_Item(int i)");
        AssertNotContains(output, "IFoo.get_Item");
        AssertContains(output, "i.get_Item(0)");
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void ExplicitEvent_Promoted_ExternalSitesRenamed_NoDuplicateBackingStore()
    {
        const string src = """
            using System;
            public interface IFoo { event Action E; }
            public class C : IFoo
            {
                public event Action E;
                event Action IFoo.E { add { } remove { } }
            }
            public class Use2
            {
                public void Use(C c, IFoo f) { c.E += () => { }; c.E -= () => { }; f.E += () => { }; }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "void add_E(Action value);");
        AssertContains(output, "void remove_E(Action value);");
        AssertContains(output, "public void add_E_cs2jClass(Action value)");
        AssertContains(output, "public void remove_E_cs2jClass(Action value)");
        AssertContains(output, "public void add_E(Action value)");
        AssertNotContains(output, "IFoo.add_E");
        AssertNotContains(output, "IFoo.remove_E");
        // 事件外部订阅站点（C13 已降为方法调用）按接收者静态类型改名。
        AssertContains(output, "c.add_E_cs2jClass(() => { })");
        AssertContains(output, "c.remove_E_cs2jClass(() => { })");
        AssertContains(output, "f.add_E(() => { })");

        // 不得重复发射后备存储：类自有事件仅一个后备字段，显式实现体不生成字段/事件声明。
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Normalize(output), "privateActionE;")
            .Cast<System.Text.RegularExpressions.Match>());
        AssertNotContains(output, "event Action E;");
        AssertNotContains(output, "event Action IFoo.E");

        Assert.Equal(2, stats.SignaturePlansPromoted);
        Assert.Equal(2, stats.SignatureDisplacedRenamed);
        Assert.Equal(2, stats.SignatureCallSitesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    // ---- M3：SI-4 泛型构造 / 接口重新实现 / 候选歧义 ----

    [Fact]
    public void GenericConstructions_EquivalentBodies_MergeToOnePublicMethod()
    {
        const string src = """
            public interface IFoo<T> { void Bar(); }
            public class C : IFoo<int>, IFoo<string>
            {
                void IFoo<int>.Bar() { }
                void IFoo<string>.Bar() { }
                public void Use(IFoo<int> a, IFoo<string> b) { a.Bar(); b.Bar(); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertNotContains(output, "IFoo<int>.Bar");
        AssertNotContains(output, "IFoo<string>.Bar");
        AssertContains(output, "public void Bar()");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Normalize(output), "publicvoidBar()")
            .Cast<System.Text.RegularExpressions.Match>());
        AssertContains(output, "a.Bar(); b.Bar();");
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.Equal(1, stats.SignatureDualInterfaceMerged);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Reimplementation_RellstInterface_NewImplementationPromoted()
    {
        const string src = """
            public interface IFoo { void Bar(); }
            public class B : IFoo { void IFoo.Bar() { System.Console.WriteLine("b"); } }
            public class D : B, IFoo { void IFoo.Bar() { System.Console.WriteLine("d"); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertNotContains(output, "IFoo.Bar");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(Normalize(output), "publicvoidBar()").Count);
        Assert.Equal(2, stats.SignaturePlansPromoted);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void AmbiguousSlotMapping_CS2J5197_PlanSkipped()
    {
        const string src = """
            public interface IFoo<T> { void Bar(); }
            public class Base : IFoo<int> { void IFoo<int>.Bar() { } }
            public class Derived : Base, IFoo<string> { void IFoo<string>.Bar() { } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        // Base 的槽位不歧义（被提升）；Derived 的槽位跨类型多候选 → CS2J5197 原子跳过（声明不动）。
        Assert.True(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureSlotAmbiguous);
        AssertContains(output, "public void Bar()");                 // Base 提升
        AssertContains(output, "void IFoo<string>.Bar()");           // Derived 原样保留
        Assert.Equal(1, stats.SignaturePlansPromoted);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Info
            && d.Code == MemberDesugarDiagnosticCode.SignatureResidual);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    // ---- M3：NH-3 纯 base 委托白名单桥接 ----

    [Fact]
    public void Nh3_WhitelistedPureBaseDelegation_SameNameBridgeRetained_SitesUntouched()
    {
        const string src = """
            public class Base { protected virtual string Name { get { return "b"; } } }
            public class Derived : Base { public new string Name { get { return base.Name; } } }
            public class Use { public string Get(Derived d) => d.Name; }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        // NH-3：白名单内保留同名 public 桥接（不改名/不改站点）；C11 仍将属性方法化。
        AssertContains(output, "public new string get_Name()");
        AssertContains(output, "return base.get_Name()");
        AssertContains(output, "d.get_Name()");
        AssertNotContains(output, "_cs2jNew");
        AssertNotContains(output, "_cs2jClass");
        Assert.Equal(1, stats.SignatureHideBridged);
        Assert.Equal(0, stats.SignatureHideRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Nh3_NonWhitelisted_CS2J5192_OutputByteIdentical()
    {
        const string src = """
            public class MyEx : System.Exception
            {
                public new string ToString() => base.ToString();
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // 成员原样保留、字节一致
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureNameReservationConflict
            && d.Severity == MemberDesugarSeverity.Error);
        Assert.Equal(0, stats.SignatureHideBridged);
        Assert.Equal(0, stats.SignatureHideRenamed);
    }

    // ---- M3：NH-4 字段/静态方法隐藏改名；new virtual 留 M4 ----

    [Fact]
    public void Nh4_NewField_And_NewStaticMethod_Renamed_StaticSitesResolved()
    {
        const string src = """
            public class Base { public int F = 1; public static void Stat() { } }
            public class Derived : Base
            {
                public new int F = 2;
                public new static void Stat() { }
                public int Use(Base b, Derived d) { return b.F + d.F; }
                public void Go() { Base.Stat(); Derived.Stat(); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public int F_cs2jNew = 2;");
        AssertContains(output, "public static void Stat_cs2jNew()");
        AssertNotContains(output, "new int F");
        AssertNotContains(output, "new static");
        // 静态绑定按接收者静态类型解析：Base.Stat 不动、Derived.Stat 改名；字段同理。
        AssertContains(output, "Base.Stat();");
        AssertContains(output, "Derived.Stat_cs2jNew();");
        AssertContains(output, "b.F + d.F_cs2jNew");
        Assert.Equal(2, stats.SignatureHideRenamed);
        Assert.Equal(2, stats.SignatureCallSitesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Nh2_NewVirtual_NewSlotChainRenamed_ForM4a()
    {
        const string src = """
            public class Base { public virtual void Ping() { } }
            public class Derived : Base { public new virtual void Ping() { } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);   // M4a：newslot 链整组改名
        AssertContains(output, "public virtual void Ping_cs2jNew()");
        AssertNotContains(output, "new virtual");
        Assert.Equal(1, stats.SignatureHideRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    // ---- M4a：NH-1 实例化石隐藏改名（显式 new / 隐式 CS0108）----

    [Fact]
    public void Nh1_Method_ExplicitNew_BaseSitesUntouched_DerivedRenamed_Idempotent()
    {
        const string src = """
            public class Base { public string Name() => "base"; }
            public class Derived : Base { public new string Name() => "derived"; }
            public class Use { public string Call(Base b, Derived d) => b.Name() + d.Name(); }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public string Name_cs2jNew() => \"derived\"");
        AssertNotContains(output, "new string Name()");
        // 静态绑定：基类静态类型站点绑定基槽 → 不动；派生静态类型站点 → 新名。
        AssertContains(output, "b.Name() + d.Name_cs2jNew()");
        Assert.Equal(1, stats.SignatureHideRenamed);
        Assert.Equal(1, stats.SignatureCallSitesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);   // 二次运行零 diff
    }

    [Fact]
    public void Nh1_Method_ImplicitHiding_NoToken_Renamed()
    {
        const string src = """
            public class Base { public string Name() => "base"; }
            public class Derived : Base { public string Name() => "derived"; }
            public class Use { public string Call(Base b, Derived d) => b.Name() + d.Name(); }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public string Name_cs2jNew() => \"derived\"");
        AssertContains(output, "d.Name_cs2jNew()");
        AssertContains(output, "b.Name()");
        Assert.Equal(1, stats.SignatureHideRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Nh1_Property_AccessorPairRenamed_ClassTypedSitesRenamed_BaseUntouched()
    {
        const string src = """
            public class Base { public string Name { get; set; } = ""; }
            public class Derived : Base { public new string Name { get; set; } = ""; }
            public class Use { public void U(Base b, Derived d) { b.Name = "x"; var y = d.Name; } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        // C11–C13 方法化后按 get_/set_ 配对改名（保留配对）。
        AssertContains(output, "public string get_Name_cs2jNew()");
        AssertContains(output, "public void set_Name_cs2jNew(string value)");
        AssertContains(output, "public string get_Name()");          // 基类同名槽保留
        AssertNotContains(output, "new string get_Name");
        // 基类静态类型站点不动、派生静态类型站点改名。
        AssertContains(output, "b.set_Name(\"x\")");
        AssertContains(output, "d.get_Name_cs2jNew()");
        Assert.Equal(2, stats.SignatureHideRenamed);                 // get_ + set_ 两条声明
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Nh1_Indexer_AccessorPairRenamed_ClassTypedSiteRenamed()
    {
        const string src = """
            public class IBase { public int this[int i] => i; }
            public class IDerived : IBase { public new int this[int i] => i * 2; }
            public class Use { public int U(IBase b, IDerived d) => b[0] + d[0]; }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "get_Item_cs2jNew");
        AssertNotContains(output, "new int this");
        AssertContains(output, "b.get_Item(0) + d.get_Item_cs2jNew(0)");
        Assert.Equal(1, stats.SignatureHideRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    // ---- M4a：NH-2 newslot 链整组改名 ----

    [Fact]
    public void Nh2_ThreeLevelChain_NewVirtual_GroupRenamed_BaseChainUntouched()
    {
        const string src = """
            public class A { public virtual void Foo() { } }
            public class B : A { public new virtual void Foo() { } }
            public class C : B { public override void Foo() { } }
            public class Use { public void U(A a, B b, C c) { a.Foo(); b.Foo(); c.Foo(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        // B 的 newslot 与 C 的 override 整组改名；A 基槽不动。
        AssertContains(output, "public virtual void Foo_cs2jNew()");
        AssertContains(output, "public override void Foo_cs2jNew()");
        AssertContains(output, "public virtual void Foo()");     // A 保留
        AssertNotContains(output, "new virtual");
        // 静态类型 A 绑定基槽不动；B/C 绑定新槽链改名（两条虚链独立）。
        AssertContains(output, "a.Foo(); b.Foo_cs2jNew(); c.Foo_cs2jNew();");
        Assert.Equal(2, stats.SignatureHideRenamed);
        Assert.Equal(2, stats.SignatureCallSitesRewritten);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Nh2_NewAbstract_GroupRenamed_KeepsAbstractAndOverride()
    {
        const string src = """
            public class A { public virtual void Foo() { } }
            public abstract class B : A { public new abstract void Foo(); }
            public class C : B { public override void Foo() { } }
            public class Use { public void U(A a, C c) { a.Foo(); c.Foo(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public abstract void Foo_cs2jNew();");
        AssertContains(output, "public override void Foo_cs2jNew()");
        AssertNotContains(output, "new abstract");
        AssertContains(output, "a.Foo(); c.Foo_cs2jNew();");
        Assert.Equal(2, stats.SignatureHideRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Nh2_ImplicitVirtualHiding_NoToken_NewSlotRenamed()
    {
        const string src = """
            public class A { public virtual void Foo() { } }
            public class B : A { public virtual void Foo() { } }
            public class Use { public void U(A a, B b) { a.Foo(); b.Foo(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public virtual void Foo_cs2jNew()");
        AssertContains(output, "a.Foo(); b.Foo_cs2jNew();");
        Assert.Equal(1, stats.SignatureHideRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    // ---- M4a：NH-5 闸门/对账 ----

    [Fact]
    public void Nh5_UnclaimedHidingNew_CS2J5199_ReportedAndCounted()
    {
        // 静态属性隐藏不在 NH-1/NH-2/NH-4 认领范围（设计裁决 14 只覆盖方法/字段）→ NH-5 residual 对账。
        const string src = """
            public class Base { public static string Name { get; set; } = ""; }
            public class Derived : Base { public static new string Name { get; set; } = ""; }
            """;
        var (output, _, stats, diags) = Rewrite(src, Enabled());

        var residual = diags.SingleOrDefault(d =>
            d.Code == MemberDesugarDiagnosticCode.SignatureResidual);
        Assert.NotNull(residual);
        Assert.Equal(MemberDesugarSeverity.Error, residual!.Severity);   // strict 默认 true
        Assert.Equal(1, stats.SignatureResidual);
        Assert.Equal(0, stats.SignatureHideRenamed);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);
    }

    [Fact]
    public void Nh5_ExpressionTreeSite_CS2J5194_Atomic_DeclarationUntouched()
    {
        const string src = """
            using System;
            using System.Linq.Expressions;
            public class Base { public string Name() => "base"; }
            public class Derived : Base { public new string Name() => "derived"; }
            public class Use { public void U(Derived d) { Expression<Func<string>> e = () => d.Name(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // plan 原子不动（含声明）
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureExpressionTreeOrDynamic);
        Assert.Equal(0, stats.SignatureHideRenamed);
    }

    [Fact]
    public void Nh5_DynamicSite_CS2J5194_Atomic_DeclarationUntouched()
    {
        const string src = """
            public class Base { public string Name() => "base"; }
            public class Derived : Base { public new string Name() => "derived"; }
            public class Use { public void U(dynamic d) { var x = d.Name(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureExpressionTreeOrDynamic
            && d.Message.Contains("dynamic"));
        Assert.Equal(0, stats.SignatureHideRenamed);
    }

    [Fact]
    public void Nh5_ReflectionStringSite_CS2J5193_NonBlocking_SiteUntouched()
    {
        const string src = """
            using System;
            using System.Reflection;
            public class Base { public string Name() => "base"; }
            public class Derived : Base { public new string Name() => "derived"; }
            public class Use { public MethodInfo? R() => typeof(Derived).GetMethod("Name"); }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public string Name_cs2jNew() => \"derived\"");
        AssertContains(output, "GetMethod(\"Name\")");   // 反射站点文本保留
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureReflectionContract
            && d.Severity == MemberDesugarSeverity.Warning);   // 非阻断，不升 Error
        Assert.Equal(1, stats.SignatureReflectionWarnings);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);
    }

    // ---- M4b：CR 协变返回（CR-1/2/3）----

    [Fact]
    public void Cr1_ReferenceCovariant_Retained_ByteIdentical_Counters()
    {
        const string src = """
            public class Animal { }
            public class Dog : Animal { }
            public class Base { public virtual Animal Create() => new Animal(); }
            public class Derived : Base { public override Dog Create() => new Dog(); }
            public class Use { public string U(Base b, Derived d) => d.Create().GetType().Name; }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // CR-1 直映保留：零改写、字节一致
        Assert.False(changed);
        AssertNotContains(output, "_cs2jCovariant");
        Assert.Equal(1, stats.SignatureCovariantChains);
        Assert.Equal(1, stats.SignatureCovariantRetained);
        Assert.Equal(0, stats.SignatureCovariantBridgesSynthesized);
        Assert.Equal(0, stats.SignatureCovariantRenamed);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }

    [Fact]
    public void Cr1_C27ClonePair_NeverBridged_ByteIdentical()
    {
        // C27 Cs2jWithClone 合成对（命名约定识别）固定归 CR-1，不重复桥接（设计决策 10/§11）。
        const string src = """
            using System.Collections.Generic;
            public class B { public virtual IEnumerable<object> Cs2jWithClone() => new List<object>(); }
            public class D : B { public override IEnumerable<string> Cs2jWithClone() => new List<string>(); }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // 不桥接、字节一致
        Assert.False(changed);
        AssertNotContains(output, "_cs2jCovariant");
        Assert.Equal(1, stats.SignatureCovariantRetained);
        Assert.Equal(0, stats.SignatureCovariantBridgesSynthesized);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
    }

    [Fact]
    public void Cr2_ErasureConflict_BridgeAndRename_BaseSiteUntouched_DerivedSiteRenamed()
    {
        const string src = """
            using System.Collections.Generic;
            public class Base { public virtual IEnumerable<object> Items() => new List<object>(); }
            public class Derived : Base { public override IEnumerable<string> Items() => new List<string>(); }
            public class Use { public void U(Base b, Derived d) { var x = b.Items(); var y = d.Items(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        // bridge：基槽签名 override，体委托改名方法。
        AssertContains(output, "public override IEnumerable<object> Items() => Items_cs2jCovariant();");
        // 原体改名方法（无更深派生 → 非虚独立 newslot）。
        AssertContains(output, "public IEnumerable<string> Items_cs2jCovariant() => new List<string>();");
        // 基类静态类型站点不动（虚分派进 bridge）；派生静态类型站点改新名。
        AssertContains(output, "var x = b.Items();");
        AssertContains(output, "var y = d.Items_cs2jCovariant();");
        Assert.Equal(1, stats.SignatureCovariantBridgesSynthesized);
        Assert.Equal(1, stats.SignatureCovariantRenamed);
        Assert.Equal(1, stats.SignatureCallSitesRewritten);
        Assert.Equal(0, stats.SignatureResidual);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);   // 二次运行零 diff
    }

    [Fact]
    public void Cr2_DeepDerivedChain_GroupRenamed_KeepsVirtualOverride_BothPaths()
    {
        const string src = """
            using System.Collections.Generic;
            public class Base { public virtual IEnumerable<object> Items() => new List<object>(); }
            public class Derived : Base { public override IEnumerable<string> Items() => new List<string>(); }
            public class More : Derived { public override IEnumerable<string> Items() => new List<string>(); }
            public class Use
            {
                public void U(Base b, Derived d, More m)
                {
                    var a = b.Items(); var c = d.Items(); var e = m.Items();
                }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        // bridge 委托虚分派进入最深改名体；sub-chain 整组改名，顶层保留 virtual、派生保留 override。
        AssertContains(output, "public override IEnumerable<object> Items() => Items_cs2jCovariant();");
        AssertContains(output, "public virtual IEnumerable<string> Items_cs2jCovariant() => new List<string>();");
        AssertContains(output, "public override IEnumerable<string> Items_cs2jCovariant() => new List<string>();");
        // 三条调用路径：基类静态类型不动，Derived/More 均改同名（虚分派保全）。
        AssertContains(output, "var a = b.Items();");
        AssertContains(output, "var c = d.Items_cs2jCovariant();");
        AssertContains(output, "var e = m.Items_cs2jCovariant();");
        Assert.Equal(1, stats.SignatureCovariantBridgesSynthesized);
        Assert.Equal(2, stats.SignatureCovariantRenamed);   // bridge 点 + 更深派生
        Assert.Equal(2, stats.SignatureCallSitesRewritten);
        Assert.Equal(0, stats.SignatureResidual);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);   // 二次运行零 diff
    }

    [Fact]
    public void Cr2_SealedOverride_BridgeCarriesSealed_RenamedNonVirtual()
    {
        const string src = """
            using System.Collections.Generic;
            public class Base { public virtual IEnumerable<object> Items() => new List<object>(); }
            public class Derived : Base { public sealed override IEnumerable<string> Items() => new List<string>(); }
            public class Use { public void U(Derived d) { var y = d.Items(); } }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.True(changed);
        AssertContains(output, "public sealed override IEnumerable<object> Items() => Items_cs2jCovariant();");
        AssertContains(output, "public IEnumerable<string> Items_cs2jCovariant() => new List<string>();");
        AssertNotContains(output, "virtual IEnumerable<string> Items_cs2jCovariant");
        AssertContains(output, "var y = d.Items_cs2jCovariant();");
        Assert.Equal(1, stats.SignatureCovariantBridgesSynthesized);
        Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output);

        var (second, _, _, _) = Rewrite(output, Enabled());
        Assert.Equal(output, second);
    }

    [Fact]
    public void Cr3_UnprovableCovariant_CS2J5195_AtomicByteIdentical()
    {
        const string src = """
            public class GenBase { public virtual object Make() => new object(); }
            public class GenDerived<T> : GenBase where T : class
            {
                public override T Make() => default!;
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // plan 原子不动、字节一致
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureValueTypeCovariant
            && d.Severity == MemberDesugarSeverity.Error);   // strict 默认 true
        Assert.Equal(0, stats.SignatureCovariantBridgesSynthesized);
        Assert.Equal(0, stats.SignatureCovariantRenamed);
    }

    [Fact]
    public void Cr2_ExpressionTreeSite_CS2J5194_Atomic_DeclarationUntouched()
    {
        const string src = """
            using System;
            using System.Collections.Generic;
            using System.Linq.Expressions;
            public class Base { public virtual IEnumerable<object> Items() => new List<object>(); }
            public class Derived : Base { public override IEnumerable<string> Items() => new List<string>(); }
            public class Use
            {
                public void U(Derived d) { Expression<Func<IEnumerable<string>>> e = () => d.Items(); }
            }
            """;
        var (output, changed, stats, diags) = Rewrite(src, Enabled());

        Assert.Equal(src, output);   // plan 原子不动（含声明）
        Assert.False(changed);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.SignatureExpressionTreeOrDynamic);
        Assert.Equal(0, stats.SignatureCovariantBridgesSynthesized);
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