using System.Linq.Expressions;
using System.Reflection;
using CSharpToJava.Core.ExtensionMethodResolve;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// M2 tests for extension-method explicitness: EM-REF (ref/in/scoped),
/// EM-GROUP (capture lambdas + CS2J1207), EM-COND (single-evaluation temps
/// and guards), EM-EXPRTREE (identity assertion + per-site rollback), and
/// residual gates 2/4. Rewrites are asserted textually, recompiled, and,
/// where useful, executed.
/// </summary>
public sealed class ExtensionMethodResolveM2Tests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Linq.dll")),
    };

    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true);

    private static string Compact(string code)
        => string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

    private static (CSharpCompilation Compilation, SemanticModel Model) CompileOne(
        string source,
        ExtensionMethodResolveOptions? options = null,
        string fileName = "Test.cs")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "em-m2-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        return (compilation, compilation.GetSemanticModel(tree));
    }

    private static ExtensionFileResult RunRewrite(
        CSharpCompilation compilation,
        SemanticModel model,
        out ExtensionMethodResolvePreprocessor preprocessor,
        ExtensionMethodResolveOptions? options = null)
    {
        preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(options ?? new ExtensionMethodResolveOptions());
        return preprocessor.RewriteFile(model);
    }

    private static CSharpCompilation Recompile(
        string code,
        bool assertClean = true,
        string fileName = "Test.cs")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "em-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        if (assertClean)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.False(errors.Count != 0,
                "rewritten code must compile: " + string.Join("; ", errors.Select(e => e.GetMessage())));
        }

        return compilation;
    }

    private static Assembly Emit(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success,
            "emit failed: " + string.Join("; ", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())));
        return Assembly.Load(stream.ToArray());
    }

    private static void AssertNoResiduals(
        ExtensionMethodResolvePreprocessor preprocessor,
        CSharpCompilation rewritten)
    {
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.False(residuals.Count != 0,
            "unexpected residuals: " + string.Join(", ", residuals.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    // ── EM-REF ──────────────────────────────────────────────────────────────

    [Fact]
    public void RefReceiver_WritableLocal_EmitsRefModifier()
    {
        const string source = """
            public static class Ext
            {
                public static void Bump(this ref int x) => x++;
            }

            public class Program
            {
                public int Run()
                {
                    var v = 1;
                    v.Bump();
                    return v;
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RefReceiverSitesEmitted);
        Assert.Contains("global::Ext.Bump(refv)", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var instance = assembly.GetType("Program")!.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        var value = assembly.GetType("Program")!.GetMethod("Run")!.Invoke(instance, null);
        Assert.Equal(2, value);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void InReceiver_Rvalue_EmitsInModifier()
    {
        const string source = """
            public static class Ext
            {
                public static int Read(this in int x) => x;
            }

            public class Program
            {
                public int Run() => (2 + 3).Read();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RefReceiverSitesEmitted);
        Assert.Contains("global::Ext.Read((2+3))", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void ScopedRefReceiver_EmitsRefKeyword()
    {
        const string source = """
            using System;

            public static class Ext
            {
                public static void Bump(this scoped ref Span<int> span) => span[0]++;
            }

            public class Program
            {
                public int Run()
                {
                    Span<int> values = stackalloc int[2];
                    values.Bump();
                    return values[0];
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Contains("global::Ext.Bump(refvalues)", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var instance = assembly.GetType("Program")!.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        var value = assembly.GetType("Program")!.GetMethod("Run")!.Invoke(instance, null);
        Assert.Equal(1, value);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void RefReceiver_UnprovableRvalue_Reports1204_StaysSugar()
    {
        const string source = """
            public static class Ext
            {
                public static void Bump(this ref int x) => x++;
            }

            public class Program
            {
                public int Value { get; set; }

                public void Run()
                {
                    Value.Bump();
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.RefMismatch);
        Assert.Equal(0, result.Statistics.RefReceiverSitesEmitted);

        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
    }

    [Fact]
    public void RefReceiver_RefReturningIndexer_EmitsRefModifier()
    {
        const string source = """
            using System;

            public static class Ext
            {
                public static void Bump(this ref int x) => x++;
            }

            public class Slots
            {
                private readonly int[] _values = new int[2];
                public ref int this[int index] => ref _values[index];
            }

            public class Program
            {
                public int Run()
                {
                    var slots = new Slots();
                    slots[0] = 41;
                    slots[0].Bump();
                    return slots[0];
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Contains("global::Ext.Bump(refslots[0])", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var instance = assembly.GetType("Program")!.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        var value = assembly.GetType("Program")!.GetMethod("Run")!.Invoke(instance, null);
        Assert.Equal(42, value);
        AssertNoResiduals(preprocessor, rewritten);
    }

    // ── EM-GROUP ────────────────────────────────────────────────────────────

    private const string GroupHostsSource = """
        using System;

        public static class HostA
        {
            public static void NoOp(this string s) { }
            public static int Add(this string s, int value) => value + 1;
        }

        public static class HostB
        {
            public static void NoOp(this object o) { }
        }

        public struct ValueBox
        {
            public int Value;
        }

        public static class ValueBoxExt
        {
            public static void Touch(this ValueBox b) { }
        }

        public class Program
        {
            public string Receiver => "abc";
        }
        """;

    [Fact]
    public void Group_Action_DirectCapture_RewritesToLambda()
    {
        var source = GroupHostsSource + """

            public class Site
            {
                public void Run()
                {
                    var s = "abc";
                    Action a = s.NoOp;
                    a();
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.GroupSitesRewritten);
        Assert.Contains("Actiona=()=>global::HostA.NoOp(s)", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var instance = assembly.GetType("Site")!.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        assembly.GetType("Site")!.GetMethod("Run")!.Invoke(instance, null);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Group_FuncWithArguments_DirectCapture_RewritesToLambda()
    {
        var source = GroupHostsSource + """

            public class Site
            {
                public int Run()
                {
                    var s = "abc";
                    Func<int, int> f = s.Add;
                    return f(41);
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Contains("Func<int,int>f=(int_cs2j_emp1)=>global::HostA.Add(s,_cs2j_emp1)", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var site = assembly.GetType("Site")!.GetConstructor(Type.EmptyTypes)!.Invoke(null);
        var value = (int)assembly.GetType("Site")!.GetMethod("Run")!.Invoke(site, null)!;
        Assert.Equal(42, value);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Group_TempCapture_InsertsSingleEvaluationTemp()
    {
        var source = GroupHostsSource + """

            public class Site : Program
            {
                public void Run()
                {
                    Action a = Receiver.NoOp;
                    a();
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        var compact = Compact(result.OutputCode);
        Assert.Contains("_cs2j_emg", compact);
        Assert.Contains("global::HostA.NoOp(_cs2j_emg", compact);
        var rewritten = Recompile(result.OutputCode);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Group_EventSubscription_DirectCapture_RewritesToLambda()
    {
        var source = GroupHostsSource + """

            public class Site
            {
                public event Action? Happened;

                public void Run()
                {
                    var s = "abc";
                    Happened += s.NoOp;
                    Happened!.Invoke();
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Contains("Happened+=()=>global::HostA.NoOp(s)", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Group_ValueTypeReceiver_Reports1207()
    {
        var source = GroupHostsSource + """

            public class Site
            {
                public void Run()
                {
                    var b = new ValueBox { Value = 1 };
                    Action a = b.Touch;
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.GroupSitesDeferred);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.ReducedMethodGroup);
        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedMethodGroup, residuals.Keys);
    }

    [Fact]
    public void Group_AmbiguousTwoHosts_Reports1207()
    {
        const string source = """
            using System;

            public static class HostA
            {
                public static void NoOp(this string s) { }
            }

            public static class HostB
            {
                public static void NoOp(this string s) { }
            }

            public class Site
            {
                public void Run()
                {
                    string s = "abc";
                    Action a = s.NoOp;
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.ReducedMethodGroup);
        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedMethodGroup, residuals.Keys);
    }

    [Fact]
    public void Group_NoDelegateTarget_Reports1207()
    {
        var source = GroupHostsSource + """

            public class Site
            {
                public void Run()
                {
                    var s = "abc";
                    object o = s.NoOp;
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.ReducedMethodGroup);
        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedMethodGroup, residuals.Keys);
    }

    // ── EM-COND ─────────────────────────────────────────────────────────────

    private const string CondHostsSource = """
        using System;

        public static class CondExt
        {
            public static Box? Go(this Box? b) => b;
            public static Box? Chain(this Box? b) => b;
            public static int? Shout(this string? s) => s?.Length;
            public static void Ping(this Box? b) { }
        }

        public class Box
        {
            public int Id { get; set; }
            public Box? Next { get; set; }
        }

        public class Program
        {
            public static Box? Make() => new Box();
        }
        """;

    [Fact]
    public void Cond_ReferenceTypeValue_TempAndNullableDefault()
    {
        var source = CondHostsSource + """

            public class Site
            {
                public Box? Run(Box? b) => b?.Go();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, result.Statistics.ConditionalChainsLifted);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var_cs2j_emc1=b;", compact);
        Assert.Contains("_cs2j_emc1!=null?global::CondExt.Go(_cs2j_emc1):default(global::Box)", compact);
        var rewritten = Recompile(result.OutputCode);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Cond_ValueTypeValue_NullableResult_NoThrowOnNullReceiver()
    {
        var source = CondHostsSource + """

            public class Site
            {
                public int? Run(string? s) => s?.Shout();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed);
        Assert.Contains("default(int?)", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var siteType = assembly.GetType("Site")!;
        var instance = Activator.CreateInstance(siteType);
        Assert.Null(siteType.GetMethod("Run")!.Invoke(instance, new object?[] { null }));
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Cond_VoidCall_Statement_BecomesGuardedIf()
    {
        var source = CondHostsSource + """

            public class Site
            {
                public bool Pinged;

                public void Run(Box? b)
                {
                    b?.Ping();
                }
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        var compact = Compact(result.OutputCode);
        Assert.Contains("if(_cs2j_emc1!=null){global::CondExt.Ping(_cs2j_emc1);}", compact);
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var siteType = assembly.GetType("Site")!;
        var site = Activator.CreateInstance(siteType);
        siteType.GetMethod("Run")!.Invoke(site, new object?[] { null });
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Cond_TwoSegmentChain_TwoTempsAndTwoBranches()
    {
        var source = CondHostsSource + """

            public class Site
            {
                public Box? Run(Box? b) => b?.Go()?.Chain();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed, string.Join("\n", result.Diagnostics));
        var compact = Compact(result.OutputCode);
        Assert.Contains("var_cs2j_emc1=b;", compact);
        Assert.Contains("var_cs2j_emc2=_cs2j_emc1!=null?global::CondExt.Go(_cs2j_emc1):default(global::Box);", compact);
        Assert.Contains("return_cs2j_emc2!=null?global::CondExt.Chain(_cs2j_emc2):default(global::Box);", compact);
        var rewritten = Recompile(result.OutputCode);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Cond_ExpressionLambda_Value_ExpandsToBlockLambda()
    {
        var source = CondHostsSource + """

            public class Site
            {
                public Func<Box?, Box?> Run() => b => b?.Go();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("b=>{", compact);
        Assert.Contains("return_cs2j_emc1!=null?global::CondExt.Go(_cs2j_emc1):default(global::Box);", compact);
        var rewritten = Recompile(result.OutputCode);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Cond_VoidExpressionLambda_ExpandsToGuardedBlock()
    {
        var source = CondHostsSource + """

            public class Site
            {
                public Action<Box?> Run() => b => b?.Ping();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("b=>{", compact);
        Assert.Contains("if(_cs2j_emc1!=null)", compact);
        var rewritten = Recompile(result.OutputCode);
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void Cond_FieldInitializer_NoAnchor_Reports1205()
    {
        var source = CondHostsSource + """

            public class Site
            {
                private static Box? Box => new Box();

                public static Box? Value = Box?.Go();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.NullConditional);
        Assert.Equal(1, result.Statistics.ConditionalChainsDeferred);
        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
    }

    [Fact]
    public void Cond_ArgumentPosition_NotDominant_Reports1205()
    {
        var source = CondHostsSource + """

            public class Site
            {
                public Box? Consume(Box? b) => b;

                public Box? Run(Box? b) => Consume(b?.Go());
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.NullConditional);
        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
    }

    [Fact]
    public void Cond_ForeignCallInArgument_Reports1205()
    {
        var source = """
            using System;

            public static class CondExt
            {
                public static Box? Go(this Box? b, Box? a) => b;

                public static Box? Other(this Box? b) => b;
            }

            public class Box { public int Id { get; set; } }

            public class Site
            {
                public Box? Run(Box? b, Box? other) => b?.Go(other.Other());
            }
            """;
        // The inner other.Other() in the chain segment argument is a foreign
        // reduced call: lifting only the spine would leave residual sugar.
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.NullConditional);
        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
    }

    // ── EM-EXPRTREE ─────────────────────────────────────────────────────────

    private const string ExpressionTreeSource = """
        using System;
        using System.Linq.Expressions;

        public static class EtExt
        {
            public static int Twice(this int x) => x * 2;
        }

        public class Program
        {
            public static Expression<Func<int, int>> Build() => x => x.Twice();
        }
        """;

    [Fact]
    public void ExprTree_Rewritten_KeepsMethodCallExpressionIdentity()
    {
        var (compilation, model) = CompileOne(ExpressionTreeSource);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.True(result.Changed);
        Assert.Contains("global::EtExt.Twice(x)", Compact(result.OutputCode));
        var rewritten = Recompile(result.OutputCode);
        var assembly = Emit(rewritten);
        var expression = (Expression<Func<int, int>>)assembly.GetType("Program")!
            .GetMethod("Build")!.Invoke(null, null)!;
        var call = Assert.IsAssignableFrom<MethodCallExpression>(expression.Body);
        Assert.Equal("Twice", call.Method.Name);
        Assert.Equal("EtExt", call.Method.DeclaringType!.Name);
        Assert.Equal(42, expression.Compile()(21));
        AssertNoResiduals(preprocessor, rewritten);
    }

    [Fact]
    public void ExprTree_IdentityFails_PerSiteRollbackAnd1210()
    {
        var (compilation, model) = CompileOne(ExpressionTreeSource);
        var localPreprocessor = new ExtensionMethodResolvePreprocessor
        {
            ForceExpressionTreeMismatch = (_, _) => true,
        };
        localPreprocessor.BindCompilation(compilation);
        localPreprocessor.Analyze(new ExtensionMethodResolveOptions());
        var result = localPreprocessor.RewriteFile(model);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == ExtensionMethodCodes.ExpressionTreeIdentityFailed);
        Assert.Equal(1, result.Statistics.ExpressionTreeRollbacks);
        Assert.Contains("x.Twice()", result.OutputCode);
        var rewritten = Recompile(result.OutputCode);
        var residuals = localPreprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleExpressionTreeShape, residuals.Keys);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
    }

    [Fact]
    public void ExprTree_NullConditional_AssertOnlyNotLifted()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;

            public static class EtExt
            {
                public static int? Shout(this string? s) => s?.Length;
            }

            public class Program
            {
                public static Expression<Func<string?, int?>> Build() => s => s?.Shout();
            }
            """;
        var (compilation, model) = CompileOne(source);
        var result = RunRewrite(compilation, model, out var preprocessor);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.ConditionalChainsLifted);
        Assert.Equal(1, result.Statistics.ConditionalChainsDeferred);
        var rewritten = Recompile(result.OutputCode, assertClean: false);
        var residuals = preprocessor.ResidualScan(rewritten);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleRemainingReducedInvocation, residuals.Keys);
        Assert.DoesNotContain(ExtensionMethodResidualScanner.RuleExpressionTreeShape, residuals.Keys);
    }
}
