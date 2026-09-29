using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C13 event 去糖 Core 单测矩阵（设计 §7.1）：内联源码 → CSharpCompilation →
/// MemberDesugarPreprocessor（规则经程序集扫描自动生效）→ RewriteFile → 忽略空白断言 +
/// AssertOutputCompiles。单文件用例复用 MemberDesugarPropertyUnitTests 的 internal 帮助方法
/// （同程序集可见）；跨文件/dynamic 用例用本类 RewriteAll（多树 + 文件路径 + TPA 引用）。
/// 事件产物引用 Cs2jPrelude.CsPrelude，ProjectTests 已引用 Cs2jPrelude 工程（TPA 可解析）。
/// </summary>
public class MemberDesugarEventUnitTests
{
    private static (string Output, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diags)
        RewriteEvent(string src, MemberDesugarOptions? options = null, params string[] extra)
        => MemberDesugarPropertyUnitTests.Rewrite(src, options ?? new MemberDesugarOptions(), extra);

    /// <summary>多文件重写（跨文件订阅 / dynamic 绑定）：每树一个文件路径，同一编译、同一 preprocessor。</summary>
    private static (string[] Outputs, MemberDesugarStatistics Stats, List<MemberDesugarDiagnostic> Diags)
        RewriteAll(MemberDesugarOptions options, params (string Path, string Src)[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(
            f.Src, new CSharpParseOptions(LanguageVersion.Preview), path: f.Path)).ToArray();
        var compilation = CSharpCompilation.Create("md-event-multi",
            trees, GetReferences(),
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

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
            foreach (var assembly in trusted.Split(Path.PathSeparator))
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
    }

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    private static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    private static void AssertPlanned(string output, MemberDesugarStatistics stats, params string[] extra)
    {
        Assert.Equal(0, stats.PlansSkipped);
        MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, extra);
    }

    private static bool HasCode(List<MemberDesugarDiagnostic> diags, MemberDesugarDiagnosticCode code)
        => diags.Any(d => d.Code == code);

    // ---- field-style 声明面（EV-1/EV-7）----

    [Fact]
    public void FieldLikeEvent_InitializerPreserved()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click = Handler;
                static void Handler() { }
            }
            """);
        AssertContains(output, "private Action? Click = Handler;");
        AssertPlanned(output, stats);
    }

    [Fact]
    public void StaticEvent_FieldAndMethodsAreStatic_InClassCombineUsesPrelude()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public static event Action? Tick;
                static void Hook() { Tick += OnTick; }
                static void OnTick() { }
            }
            """);
        AssertContains(output, "private static Action? Tick;");
        AssertContains(output, "public static void add_Tick(Action? value)");
        AssertContains(output, "public static void remove_Tick(Action? value)");
        AssertContains(output, "Tick = Cs2jPrelude.CsPrelude.Combine(Tick, OnTick);");
        Assert.Equal(1, stats.EventInClassCombineSitesRewritten);
        AssertPlanned(output, stats);
    }

    [Fact]
    public void NewHidingEvent_KeepsNewKeywordOnMethods()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class Base
            {
                public event Action? Click;
            }

            class Derived : Base
            {
                public new event Action? Click;
            }
            """);
        AssertContains(output, "public new void add_Click(Action? value)");
        AssertContains(output, "public new void remove_Click(Action? value)");
        AssertPlanned(output, stats);
    }

    [Fact]
    public void InClassUnsubscribe_UsesRemove()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
                void Unhook() { Click -= OnClick; }
                void OnClick() { }
            }
            """);
        AssertContains(output, "Click = Cs2jPrelude.CsPrelude.Remove(Click, OnClick);");
        Assert.Equal(1, stats.EventInClassCombineSitesRewritten);
        AssertPlanned(output, stats);
    }

    [Fact]
    public void InClassCompound_ExpressionPosition()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
                public Action? Hook(Action h)
                {
                    var y = (Click += h);
                    return y;
                }
            }
            """);
        AssertContains(output, "var y = (Click = Cs2jPrelude.CsPrelude.Combine(Click, h));");
        AssertPlanned(output, stats);
    }

    // ---- 类内零改写（D3/EV-12/EV-13）----

    // ---- 类外站点（EV-10）----

    [Fact]
    public void ExternalSubscribe_BecomesAccessorCall()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
            }

            class App
            {
                void Wire(C c)
                {
                    c.Click += OnClick;
                    c.Click -= OnClick;
                }
                void OnClick() { }
            }
            """);
        AssertContains(output, "c.add_Click(OnClick);");
        AssertContains(output, "c.remove_Click(OnClick);");
        AssertNotContains(output, "c.Click");
        Assert.Equal(1, stats.EventSubscribeSitesRewritten);
        Assert.Equal(1, stats.EventUnsubscribeSitesRewritten);
        AssertPlanned(output, stats);
    }

    [Fact]
    public void ExternalSubscribe_SideEffectReceiverSingleEvaluation()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
            }

            class App
            {
                private int _calls;
                C GetThing()
                {
                    _calls++;
                    return new C();
                }

                void Wire()
                {
                    GetThing().Click += OnClick;
                }
                void OnClick() { }
            }
            """);
        AssertContains(output, "GetThing().add_Click(OnClick);");
        // 接收者单次求值：GetThing 的调用只出现一次（方法定义不算）
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(output, @"GetThing\(\)\.add_Click"));
        AssertPlanned(output, stats);
    }

    [Fact]
    public void ExternalSubscribe_InterfaceReceiver()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            interface IFoo
            {
                event Action? Click;
            }

            class Impl : IFoo
            {
                public event Action? Click;
            }

            class App
            {
                void Wire(IFoo f)
                {
                    f.Click += OnClick;
                }
                void OnClick() { }
            }
            """);
        // 接口事件同步去糖为抽象方法对，实现类方法对构成隐式实现
        AssertContains(output, "void add_Click(Action? value);");
        AssertContains(output, "void remove_Click(Action? value);");
        AssertContains(output, "f.add_Click(OnClick);");
        AssertPlanned(output, stats);
    }

    // ---- nameof（EV-14）----

    [Fact]
    public void NameOf_Literalized_InClassAndOutOfClass()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
                public string InClass() => nameof(Click);
            }

            class App
            {
                public string OutOfClass() => nameof(C.Click);
            }
            """);
        AssertContains(output, "public string InClass() => \"Click\";");
        AssertContains(output, "public string OutOfClass() => \"Click\";");
        Assert.Equal(2, stats.EventNameofLiteralized);
        AssertPlanned(output, stats);
    }

    [Fact]
    public void NameOf_NonEvent_Unchanged()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
                public string Prop { get; set; } = "";
                public string Names() => nameof(Prop) + nameof(Console);
            }
            """);
        // 属性 nameof 由 C11 属性规则字面量化（既有行为），值保持 "Prop"
        AssertContains(output, "\"Prop\"");
        AssertContains(output, "nameof(Console)");
        AssertPlanned(output, stats);
    }

    // ---- custom 事件（EV-3/EV-16）----

    [Fact]
    public void CustomEvent_InClassSubscribe_GoesThroughAccessor()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                private Action? _listeners;

                public event Action? Changed
                {
                    add { _listeners += value; }
                    remove { _listeners -= value; }
                }

                void Hook(Action h) { Changed += h; }
            }
            """);
        AssertContains(output, "add_Changed(h);");
        AssertPlanned(output, stats);
    }

    [Fact]
    public void CustomEvent_ForwardingToPrivateFieldStyleEvent_BackupStorePattern()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                private event Action? _inner;

                public event Action? Changed
                {
                    add { _inner += value; }
                    remove { _inner -= value; }
                }

                public void Fire() => _inner?.Invoke();
            }
            """);
        // EV-16：两套成员都在；转发体按 _inner 的类内组合规则改写
        AssertContains(output, "private Action? _inner;");
        AssertContains(output, "void add__inner(Action? value)");
        AssertContains(output, "_inner = Cs2jPrelude.CsPrelude.Combine(_inner, value);");
        AssertContains(output, "_inner = Cs2jPrelude.CsPrelude.Remove(_inner, value);");
        AssertContains(output, "public void add_Changed(Action? value)");
        AssertContains(output, "public void remove_Changed(Action? value)");
        AssertContains(output, "_inner?.Invoke()");
        AssertPlanned(output, stats);
    }

    // ---- 显式接口实现 / 接口 / 抽象（EV-4/EV-6）----

    [Fact]
    public void ExplicitInterfaceImplementation_KeepsQualifier()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            interface IFoo
            {
                event Action? Click;
            }

            class C : IFoo
            {
                event Action? IFoo.Click
                {
                    add { }
                    remove { }
                }
            }
            """);
        AssertContains(output, "void IFoo.add_Click(Action? value)");
        AssertContains(output, "void IFoo.remove_Click(Action? value)");
        AssertNotContains(output, "event Action? IFoo.Click");
        AssertPlanned(output, stats);
    }

    [Fact]
    public void AbstractEvent_OnlySignatures_NoField()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            abstract class C
            {
                public abstract event Action? Click;
            }
            """);
        AssertContains(output, "public abstract void add_Click(Action? value);");
        AssertContains(output, "public abstract void remove_Click(Action? value);");
        AssertNotContains(output, "private Action? Click");
        Assert.Equal(0, stats.EventBackingFieldsGenerated);
        AssertPlanned(output, stats);
    }

    [Fact]
    public void VirtualOverrideEventHierarchy()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class Base
            {
                public virtual event Action? Click;
                public void FireBase() => Click?.Invoke();
            }

            class Derived : Base
            {
                public override event Action? Click;
            }

            class App
            {
                void Wire(Derived d)
                {
                    d.Click += OnClick;
                    Base b = d;
                    b.Click += OnClick;
                }
                void OnClick() { }
            }
            """);
        AssertContains(output, "public virtual void add_Click(Action? value)");
        AssertContains(output, "public override void add_Click(Action? value)");
        AssertContains(output, "d.add_Click(OnClick);");
        AssertContains(output, "b.add_Click(OnClick);");
        AssertPlanned(output, stats);
    }

    // ---- struct / 泛型 / 自定义 delegate ----

    [Fact]
    public void StructEvent_FieldAndNonVirtualMethods()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            struct S
            {
                public event Action? Changed;
                public void Fire() => Changed?.Invoke();
            }

            class App
            {
                void Wire(S s)
                {
                    s.Changed += OnChanged;
                }
                void OnChanged() { }
            }
            """);
        AssertContains(output, "private Action? Changed;");
        AssertContains(output, "public void add_Changed(Action? value)");
        AssertContains(output, "s.add_Changed(OnChanged);");
        AssertPlanned(output, stats);
    }

    [Fact]
    public void GenericTypeEvent_InClassCombine()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class Box<T>
            {
                public event Action<T>? Changed;
                public void Put(T value)
                {
                    var handler = new Action<T>(_ => { });
                    Changed += handler;
                }
            }
            """);
        AssertContains(output, "private Action<T>? Changed;");
        AssertContains(output, "Changed = Cs2jPrelude.CsPrelude.Combine(Changed, handler);");
        AssertPlanned(output, stats);
    }

    [Fact]
    public void CustomDelegateTypeSyntax_Preserved()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            delegate int MyDelegate(string s, int n);

            class C
            {
                public event MyDelegate? Parsed;
                public int Fire(string s) => Parsed(s, 42);
            }
            """);
        AssertContains(output, "private MyDelegate? Parsed;");
        AssertContains(output, "public void add_Parsed(MyDelegate? value)");
        AssertContains(output, "Parsed(s, 42)");
        AssertPlanned(output, stats);
    }

    // ---- 特性路由（D16）----

    [Fact]
    public void Attribute_DefaultMethodTarget_RoutesToMethods()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                [MethodAttr]
                public event Action? Click;
            }
            """, extra: """
            using System;

            [AttributeUsage(AttributeTargets.Method)]
            class MethodAttr : Attribute { }
            """);
        AssertContains(output, "[MethodAttr]");
        AssertContains(output, "public void add_Click(Action? value)");
        AssertPlanned(output, stats, """
            using System;

            [AttributeUsage(AttributeTargets.Method)]
            class MethodAttr : Attribute { }
            """);
    }

    [Fact]
    public void Attribute_FieldTarget_RoutesToField()
    {
        var (output, stats, _) = RewriteEvent("""
            using System;

            class C
            {
                [FieldAttr]
                public event Action? Click;
            }
            """, extra: """
            using System;

            [AttributeUsage(AttributeTargets.Field)]
            class FieldAttr : Attribute { }
            """);
        AssertContains(output, "[FieldAttr]");
        AssertContains(output, "private Action? Click;");
        AssertPlanned(output, stats, """
            using System;

            [AttributeUsage(AttributeTargets.Field)]
            class FieldAttr : Attribute { }
            """);
    }

    [Fact]
    public void Attribute_EventOnlyTarget_SkipsPlan()
    {
        var (output, stats, diags) = RewriteEvent("""
            using System;

            class C
            {
                [EventAttr]
                public event Action? Click;
            }
            """, extra: """
            using System;

            [AttributeUsage(AttributeTargets.Event)]
            class EventAttr : Attribute { }
            """);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.EventAttributeUnroutable));
        AssertContains(output, "public event Action? Click;");
        Assert.Equal(1, stats.PlansSkipped);
    }

    [Fact]
    public void Attribute_FieldTargetOnCustomEvent_SkipsPlan()
    {
        var (output, stats, diags) = RewriteEvent("""
            using System;

            class C
            {
                [field: NonSerialized]
                public event Action? Changed
                {
                    add { }
                    remove { }
                }
            }
            """);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.EventAttributeUnroutable));
        AssertContains(output, "public event Action? Changed");
        Assert.Equal(1, stats.PlansSkipped);
    }

    // ---- 反例/诊断（EV-9/EV-15/D20）----

    [Fact]
    public void SkipOverrides_LeavesDeclarationAndSites()
    {
        var (output, stats, diags) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
            }

            class App
            {
                void Wire(C c) { c.Click += OnClick; }
                void OnClick() { }
            }
            """, new MemberDesugarOptions
            {
                SkipQualifiedNames = new HashSet<string>(StringComparer.Ordinal) { "C::Click" },
            });
        AssertContains(output, "public event Action? Click;");
        AssertContains(output, "c.Click += OnClick;");
        Assert.Equal(0, stats.EventsPlanned);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.EventPlanCreated)); // UserSkipped 通知
    }

    [Fact]
    public void DynamicReceiver_SkipsPlan()
    {
        var (outputs, stats, diags) = RewriteAll(new MemberDesugarOptions(),
            ("A.cs", """
                using System;

                class C
                {
                    public event Action? Click;
                }
                """),
            ("B.cs", """
                using System;

                class App
                {
                    void Wire(C c)
                    {
                        dynamic d = c;
                        d.Click += OnClick;
                    }
                    void OnClick() { }
                }
                """));
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.EventDynamicSite));
        Assert.Contains("public event Action? Click;", outputs[0]);
        Assert.Equal(1, stats.PlansSkipped);
    }

    // partial 事件（C# 14）在 Roslyn 4.12（C# 13）下解析为 IncompleteMemberSyntax（语法错误），
    // 永远不会到达本规则——事件声明源本身非法，基线错误由整树回退兜底。
    // EventFacts.IsPartialEvent 闸门保留，供 Roslyn 升级后立即生效（设计 D20）。

    [Fact]
    public void ReservedNameValue_SkipsPlan()
    {
        var (output, stats, diags) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? value;
            }
            """);
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.EventNameCollision));
        AssertContains(output, "public event Action? value;");
        Assert.Equal(1, stats.PlansSkipped);
    }

    [Fact]
    public void Strict_ElevatesSkipWarningsToErrors()
    {
        var (_, _, diags) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? value;
            }
            """, new MemberDesugarOptions { Strict = true });
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.EventNameCollision
            && d.Severity == MemberDesugarSeverity.Error);
    }

    // ---- 幂等 / 跨文件 ----

    [Fact]
    public void Idempotent_SecondRunZeroChange()
    {
        var src = """
            using System;

            class C
            {
                public event Action? Click;
                public void Raise() => Click?.Invoke();
            }
            """;
        var (first, _, _) = RewriteEvent(src);
        var (second, stats2, _) = RewriteEvent(first);
        Assert.Equal(0, stats2.EventsPlanned);
        Assert.Equal(0, stats2.EventBackingFieldsGenerated);
        Assert.Equal(Normalize(first), Normalize(second));
    }

    // ---- CAS 模式（EV-2/D5/D6）----

    [Fact]
    public void CasMode_GeneratesRetryLoop()
    {
        var (output, stats, diags) = RewriteEvent("""
            using System;

            class C
            {
                public event Action? Click;
                public void Raise() => Click?.Invoke();
            }
            """, new MemberDesugarOptions
            {
                EventSubscriptionPolicy = EventSubscriptionPolicy.InterlockedCompareExchange,
            });
        AssertContains(output, "var current = Click;");
        AssertContains(output, "System.Threading.Interlocked.CompareExchange(ref Click, combined, current)");
        AssertContains(output, "if (ReferenceEquals(original, current)) break;");
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.EventCasPolicyApplied));
        AssertPlanned(output, stats);
    }

    [Fact]
    public void CasMode_StructDegradedToSimple()
    {
        var (output, stats, diags) = RewriteEvent("""
            using System;

            struct S
            {
                public event Action? Changed;
                public void Fire() => Changed?.Invoke();
            }
            """, new MemberDesugarOptions
            {
                EventSubscriptionPolicy = EventSubscriptionPolicy.InterlockedCompareExchange,
            });
        Assert.True(HasCode(diags, MemberDesugarDiagnosticCode.EventStructCasDegraded));
        AssertNotContains(output, "Interlocked");
        AssertContains(output, "Changed = Cs2jPrelude.CsPrelude.Combine(Changed, value);");
        AssertPlanned(output, stats);
    }
}
