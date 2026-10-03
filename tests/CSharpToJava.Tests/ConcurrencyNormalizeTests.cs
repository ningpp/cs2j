using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.ConcurrencyNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C29 concurrency-normalize Core 单测（设计 §7.1 验收清单）：
/// 内联源码 → CSharpCompilation → BindCompilation/Analyze/RewriteFile →
/// 忽略空白（token 流）文本断言 + 统计/诊断断言。
/// </summary>
public class ConcurrencyNormalizeTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
    };

    private sealed record RunResult(
        CSharpCompilation Compilation,
        ConcurrencyPreprocessor Preprocessor,
        IReadOnlyDictionary<string, string?> Outputs,
        ConcurrencyStatistics Statistics,
        IReadOnlyList<ConcurrencyDiagnostic> Diagnostics)
    {
        public string? Output(string path) => Outputs[path];
    }

    private static RunResult RunCore(
        ConcurrencyOptions options,
        bool allowCompilationErrors,
        (string Path, string Source)[] files)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = files
            .Select(f => CSharpSyntaxTree.ParseText(f.Source, parseOptions, path: f.Path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "concurrency-normalize-test",
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        if (!allowCompilationErrors)
        {
            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0,
                "test source must bind cleanly; unbound sites fall to the residual path:\n"
                + string.Join("\n", errors.Select(e => e.ToString())));
        }

        var preprocessor = new ConcurrencyPreprocessor(options);
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze();

        var statistics = new ConcurrencyStatistics();
        var diagnostics = new List<ConcurrencyDiagnostic>();
        var outputs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, source) in files)
        {
            var tree = trees.First(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
            var result = preprocessor.RewriteFile(
                source, compilation.GetSemanticModel(tree), statistics, diagnostics);
            outputs[path] = result?.OutputCode;
        }

        return new RunResult(compilation, preprocessor, outputs, statistics, diagnostics);
    }

    private static RunResult Run(params (string Path, string Source)[] files)
        => RunCore(new ConcurrencyOptions(), allowCompilationErrors: false, files);

    private static RunResult Run(ConcurrencyOptions options, params (string Path, string Source)[] files)
        => RunCore(options, allowCompilationErrors: false, files);

    private static RunResult RunSource(string source)
        => Run(("Test.cs", source));

    private static RunResult RunSource(ConcurrencyOptions options, string source)
        => Run(options, ("Test.cs", source));

    private static RunResult RunSourceAllowingErrors(string source)
        => RunCore(new ConcurrencyOptions(), allowCompilationErrors: true, [("Test.cs", source)]);

    private static List<ConcurrencyDiagnostic> Codes(RunResult run, string code)
        => run.Diagnostics.Where(d => d.Code == code).ToList();

    /// <summary>把文本按全部空白切开再以单空格重连，得到忽略空白/换行/缩进的 token 流。</summary>
    private static string Normalized(string code)
        => string.Join(" ", code.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void AssertNormalizedEqual(string expected, string actual)
        => Assert.Equal(Normalized(expected), Normalized(actual));

    private static void AssertNormalizedContains(string haystack, string needle)
        => Assert.Contains(Normalized(needle), Normalized(haystack));

    private static void AssertNormalizedDoesNotContain(string haystack, string needle)
        => Assert.DoesNotContain(Normalized(needle), Normalized(haystack));

    private static (CSharpCompilation Compilation, SemanticModel Model) Recompile(
        string code, string path = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest), path: path);
        var compilation = CSharpCompilation.Create(
            "concurrency-normalize-recompile",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    // ===================================================================
    // CN-1：lock 保留直映——树不动，仅计数/审计
    // ===================================================================

    [Fact]
    public void Clean_File_Is_Left_Unchanged_And_Counted()
    {
        var run = RunSource("""
            class C {
                void M() {
                    int a = 1;
                    int b = a + 2;
                }
            }
            """);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.FilesScanned);
        Assert.Equal(1, run.Statistics.FilesSkippedClean);
        Assert.Equal(0, run.Statistics.FilesTransformed);
    }

    [Fact]
    public void Lock_Single_Level_Is_Byte_Preserved_Only_Counted()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o) {
                        Work();
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.LockStatementsSeen);
        Assert.Equal(0, run.Statistics.LockNestedRegions);
        Assert.Empty(run.Statistics.IdentityAuditsByKind);
    }

    [Fact]
    public void Lock_Nested_And_Recursive_Same_Object_Only_Counted()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o, object p) {
                    lock (o) {
                        lock (p) {
                            lock (o) {
                                Work();
                            }
                        }
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(3, run.Statistics.LockStatementsSeen);
        Assert.Equal(2, run.Statistics.LockNestedRegions);
    }

    [Fact]
    public void Lock_With_Throw_Is_Byte_Preserved()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o) {
                        if (Danger()) {
                            throw new InvalidOperationException("no");
                        }
                        Work();
                    }
                }
                bool Danger() => false;
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.LockStatementsSeen);
    }

    [Fact]
    public void Lock_Identity_Audits_This_TypeOf_String_GetType_By_Kind()
    {
        var src = """
            using System.Threading;
            class C {
                void M() {
                    lock (this) { Work(); }
                    lock (typeof(C)) { Work(); }
                    lock ("gate") { Work(); }
                    lock (GetType()) { Work(); }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.IdentityAuditsByKind["This"]);
        Assert.Equal(1, run.Statistics.IdentityAuditsByKind["TypeOf"]);
        Assert.Equal(1, run.Statistics.IdentityAuditsByKind["String"]);
        Assert.Equal(1, run.Statistics.IdentityAuditsByKind["TypeVariable"]);
        Assert.Equal(4, Codes(run, ConcurrencyDiagnosticCodes.LockIdentityAudit).Count);
    }

    // ===================================================================
    // CN-2：手写 Enter/Exit 配对逆向归一为 lock
    // ===================================================================

    [Fact]
    public void EnterExit_FormA_With_Taken_Is_Rewritten_To_Lock()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.Enter(o, ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        AssertNormalizedEqual("""
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o) {
                        Work();
                    }
                }
                void Work() { }
            }
            """, run.Output("Test.cs")!);
        AssertNormalizedDoesNotContain(run.Output("Test.cs")!, "Monitor.Enter");
        AssertNormalizedDoesNotContain(run.Output("Test.cs")!, "bool t = false");
        Assert.Equal(1, run.Statistics.FilesTransformed);
        Assert.Equal(1, run.Statistics.EnterExitPairsDetected);
        Assert.Equal(1, run.Statistics.EnterExitPairsRewritten);
        Assert.Equal(0, run.Statistics.EnterExitPairsRejected);
        Assert.Contains(run.Diagnostics, d =>
            d.Code == ConcurrencyDiagnosticCodes.RegionRewritten
            && d.Severity == ConcurrencySeverity.Info);
    }

    [Fact]
    public void EnterExit_FormB_Plain_Enter_Is_Rewritten_To_Lock()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    Monitor.Enter(o);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        AssertNormalizedEqual("""
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o) {
                        Work();
                    }
                }
                void Work() { }
            }
            """, run.Output("Test.cs")!);
        AssertNormalizedDoesNotContain(run.Output("Test.cs")!, "Monitor.Enter");
        AssertNormalizedDoesNotContain(run.Output("Test.cs")!, "Monitor.Exit");
        Assert.Equal(1, run.Statistics.EnterExitPairsRewritten);
    }

    [Fact]
    public void EnterExit_Taken_Referenced_In_Body_Is_Rejected_2910()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.Enter(o, ref t);
                    try
                    {
                        if (t) { Work(); }
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.NotEmpty(Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure));
        Assert.True(run.Statistics.InfeasibleSites >= 1);
        Assert.Equal(1, run.Statistics.EnterExitPairsDetected);
        Assert.Equal(1, run.Statistics.EnterExitPairsRejected);
        Assert.Equal(0, run.Statistics.EnterExitPairsRewritten);
        Assert.True(run.Statistics.SkippedByReason[ConcurrencyDiagnosticCodes.NonCanonicalStructure] >= 1);
    }

    [Fact]
    public void EnterExit_Exit_Not_In_Finally_Is_Rejected_2910()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.Enter(o, ref t);
                    try
                    {
                        Work();
                        Monitor.Exit(o);
                    }
                    catch (Exception)
                    {
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.NotEmpty(Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure));
        Assert.Equal(1, run.Statistics.EnterExitPairsRejected);
    }

    [Fact]
    public void EnterExit_Release_In_Catch_Is_Rejected_2910()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.Enter(o, ref t);
                    try
                    {
                        Work();
                    }
                    catch (Exception)
                    {
                        Monitor.Exit(o);
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.NotEmpty(Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure));
        Assert.Equal(1, run.Statistics.EnterExitPairsRejected);
    }

    [Fact]
    public void EnterExit_Cross_Method_Pairing_Is_Rejected_2910_For_Both_Methods()
    {
        var src = """
            using System.Threading;
            class C {
                void M1(object o) {
                    bool t = false;
                    Monitor.Enter(o, ref t);
                    Work();
                }
                void M2(object o) {
                    Monitor.Exit(o);
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(2, Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure).Count);
        Assert.Equal(2, run.Statistics.InfeasibleSites);
    }

    [Fact]
    public void EnterExit_Conditional_Enter_Is_Rejected_2910()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o, bool cond) {
                    bool t = false;
                    if (cond)
                    {
                        Monitor.Enter(o, ref t);
                    }
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.NotEmpty(Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure));
        Assert.True(run.Statistics.InfeasibleSites >= 1);
    }

    [Fact]
    public void EnterExit_MethodCall_LockExpression_Is_Rejected_2910()
    {
        var src = """
            using System.Threading;
            class C {
                void M() {
                    bool t = false;
                    Monitor.Enter(GetLock(), ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(GetLock());
                    }
                }
                object GetLock() => new object();
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.NotEmpty(Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure));
        Assert.Equal(1, run.Statistics.EnterExitPairsRejected);
    }

    // ===================================================================
    // CN-3：TryEnter 规范形（3 参 int 毫秒 + lockTaken + try/finally 守卫）
    // ===================================================================

    [Fact]
    public void TryEnter_NoTimeout_BareBool_Is_Canonicalized_With_Zero()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    if (Monitor.TryEnter(o))
                    {
                        try
                        {
                            Work();
                        }
                        finally
                        {
                            Monitor.Exit(o);
                        }
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedEqual("""
            using System.Threading;
            class C {
                void M(object o) {
                    bool __cs2j_mt = false;
                    Monitor.TryEnter(o, 0, ref __cs2j_mt);
                    try
                    {
                        if (__cs2j_mt)
                        {
                            Work();
                        }
                    }
                    finally
                    {
                        if (__cs2j_mt)
                        {
                            Monitor.Exit(o);
                        }
                    }
                }
                void Work() { }
            }
            """, output);
        Assert.Equal(1, run.Statistics.TryEnterSitesDetected);
        Assert.Equal(1, run.Statistics.TryEnterSitesCanonicalized);
        Assert.Equal(1, run.Statistics.TryEnterZeroTimeout);
        Assert.Equal(1, run.Statistics.TryEnterBareBool);
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.TryEnterGate).Count);
    }

    [Fact]
    public void TryEnter_Timed_BareBool_Keeps_Else_Timeout_Branch()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    if (Monitor.TryEnter(o, 50))
                    {
                        try
                        {
                            Work();
                        }
                        finally
                        {
                            Monitor.Exit(o);
                        }
                    }
                    else
                    {
                        Fallback();
                    }
                }
                void Work() { }
                void Fallback() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedEqual("""
            using System.Threading;
            class C {
                void M(object o) {
                    bool __cs2j_mt = false;
                    Monitor.TryEnter(o, 50, ref __cs2j_mt);
                    try
                    {
                        if (__cs2j_mt)
                        {
                            Work();
                        }
                        else
                        {
                            Fallback();
                        }
                    }
                    finally
                    {
                        if (__cs2j_mt)
                        {
                            Monitor.Exit(o);
                        }
                    }
                }
                void Work() { }
                void Fallback() { }
            }
            """, output);
        Assert.Equal(1, run.Statistics.TryEnterSitesCanonicalized);
        Assert.Equal(1, run.Statistics.TryEnterTimed);
        Assert.Equal(1, run.Statistics.TryEnterBareBool);
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.TryEnterGate).Count);
    }

    [Fact]
    public void TryEnter_Zero_Timeout_Literal_Is_Kept_As_Zero()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    if (Monitor.TryEnter(o, 0))
                    {
                        try { Work(); } finally { Monitor.Exit(o); }
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedContains(output, "bool __cs2j_mt = false;");
        AssertNormalizedContains(output, "Monitor.TryEnter(o, 0, ref __cs2j_mt);");
        AssertNormalizedContains(output, "if (__cs2j_mt) { Work(); }");
        AssertNormalizedContains(output, "finally { if (__cs2j_mt) { Monitor.Exit(o); } }");
        Assert.Equal(1, run.Statistics.TryEnterSitesCanonicalized);
        Assert.Equal(1, run.Statistics.TryEnterTimed);
    }

    [Fact]
    public void TryEnter_Negative_Timeout_Value_Is_Preserved()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    if (Monitor.TryEnter(o, -1))
                    {
                        try { Work(); } finally { Monitor.Exit(o); }
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedContains(output, "Monitor.TryEnter(o, -1, ref __cs2j_mt);");
        AssertNormalizedDoesNotContain(output, "Monitor.TryEnter(o, -1)");
        Assert.Equal(1, run.Statistics.TryEnterSitesCanonicalized);
    }

    [Fact]
    public void TryEnter_TimeSpan_BareBool_Promotes_Int_Temp()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                void M(object o, TimeSpan timeout) {
                    if (Monitor.TryEnter(o, timeout))
                    {
                        try { Work(); } finally { Monitor.Exit(o); }
                    }
                    else { Fallback(); }
                }
                void Work() { }
                void Fallback() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedContains(output, "int __cs2j_to = (int)timeout.TotalMilliseconds;");
        AssertNormalizedContains(output, "bool __cs2j_mt = false;");
        AssertNormalizedContains(output, "Monitor.TryEnter(o, __cs2j_to, ref __cs2j_mt);");
        AssertNormalizedContains(output, "else { Fallback(); }");
        Assert.Equal(1, run.Statistics.TryEnterSitesCanonicalized);
        Assert.Equal(1, run.Statistics.TryEnterTimed);
        Assert.Equal(1, run.Statistics.TryEnterBareBool);
    }

    [Fact]
    public void TryEnter_TwoArg_RefTaken_Form_Adds_Zero_And_Keeps_User_Taken()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.TryEnter(o, ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedEqual("""
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.TryEnter(o, 0, ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """, output);
        Assert.Equal(1, run.Statistics.TryEnterSitesCanonicalized);
        Assert.Equal(1, run.Statistics.TryEnterZeroTimeout);
        Assert.Equal(1, run.Statistics.TryEnterLockTaken);
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.TryEnterGate).Count);
    }

    [Fact]
    public void TryEnter_ThreeArg_RefTaken_TimeSpan_Promotes_Int_Temp()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                void M(object o, TimeSpan timeout) {
                    bool t = false;
                    Monitor.TryEnter(o, timeout, ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedEqual("""
            using System;
            using System.Threading;
            class C {
                void M(object o, TimeSpan timeout) {
                    int __cs2j_to = (int)timeout.TotalMilliseconds;
                    bool t = false;
                    Monitor.TryEnter(o, __cs2j_to, ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """, output);
        Assert.Equal(1, run.Statistics.TryEnterSitesCanonicalized);
        Assert.Equal(1, run.Statistics.TryEnterTimed);
        Assert.Equal(1, run.Statistics.TryEnterLockTaken);
    }

    [Fact]
    public void TryEnter_AlreadyCanonical_ThreeArg_Is_Unchanged_Without_2911()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.TryEnter(o, 50, ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Empty(Codes(run, ConcurrencyDiagnosticCodes.TryEnterGate));
        Assert.Equal(0, run.Statistics.TryEnterSitesCanonicalized);
        Assert.Equal(1, run.Statistics.TryEnterSitesDetected);
        Assert.Equal(1, run.Statistics.TryEnterTimed);
        Assert.Equal(1, run.Statistics.TryEnterLockTaken);
    }

    [Fact]
    public void TryEnter_NonCanonical_Structure_Is_Rejected_2910_Atomic()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    if (Monitor.TryEnter(o, 50))
                    {
                        Work();
                        Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.NotEmpty(Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure));
        Assert.True(run.Statistics.InfeasibleSites >= 1);
        Assert.True(run.Statistics.SkippedByReason[ConcurrencyDiagnosticCodes.NonCanonicalStructure] >= 1);
    }

    // ===================================================================
    // CN-4：Wait 重载归一 + 持锁校验
    // ===================================================================

    [Fact]
    public void Wait_NoTimeout_In_Lock_Is_Unchanged_And_Proven()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o)
                    {
                        Monitor.Wait(o);
                        Work();
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.WaitSites);
        Assert.Equal(1, run.Statistics.WaitSitesNoTimeout);
        Assert.Equal(0, run.Statistics.OwnershipWarnings);
        Assert.Empty(Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven));
    }

    [Fact]
    public void Wait_IntMs_In_Lock_Is_Unchanged_And_Proven()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o)
                    {
                        var got = Monitor.Wait(o, 100);
                    }
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.WaitSites);
        Assert.Equal(1, run.Statistics.WaitSitesIntMs);
        Assert.Equal(0, run.Statistics.WaitSitesCanonicalized);
        Assert.Equal(0, run.Statistics.OwnershipWarnings);
    }

    [Fact]
    public void Wait_TimeSpan_In_Lock_Promotes_Temp_And_Rewrites_To_Int_Overload()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                bool M(object o, TimeSpan ts) {
                    lock (o)
                    {
                        return Monitor.Wait(o, ts);
                    }
                }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedContains(output, "int __cs2j_to = (int)ts.TotalMilliseconds;");
        AssertNormalizedContains(output, "return Monitor.Wait(o, __cs2j_to);");
        AssertNormalizedDoesNotContain(output, "Monitor.Wait(o, ts)");
        Assert.Equal(1, run.Statistics.WaitSites);
        Assert.Equal(1, run.Statistics.WaitSitesTimeSpan);
        Assert.Equal(1, run.Statistics.WaitSitesCanonicalized);
        Assert.Equal(0, run.Statistics.OwnershipWarnings);
    }

    [Fact]
    public void Wait_Timed_Bool_Result_Is_Preserved_In_Assignment_And_Ternary()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                private readonly object _o = new object();

                bool M(int ms, TimeSpan ts) {
                    lock (_o)
                    {
                        bool got = Monitor.Wait(_o, ms);
                        bool timedOut = Monitor.Wait(_o, ts) ? true : false;
                        return got && timedOut;
                    }
                }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedContains(output, "bool got = Monitor.Wait(_o, ms);");
        AssertNormalizedContains(output, "int __cs2j_to = (int)ts.TotalMilliseconds;");
        AssertNormalizedContains(output, "bool timedOut = Monitor.Wait(_o, __cs2j_to) ? true : false;");
        Assert.Equal(2, run.Statistics.WaitSites);
        Assert.Equal(1, run.Statistics.WaitSitesIntMs);
        Assert.Equal(1, run.Statistics.WaitSitesTimeSpan);
        Assert.Equal(1, run.Statistics.WaitSitesCanonicalized);
    }

    [Fact]
    public void Wait_ExitContext_Constant_False_Folds_To_Int_Overload()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o)
                    {
                        var got = Monitor.Wait(o, 100, false);
                    }
                }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedContains(output, "Monitor.Wait(o, 100)");
        AssertNormalizedDoesNotContain(output, "exitContext");
        AssertNormalizedDoesNotContain(output, "Monitor.Wait(o, 100, false)");
        Assert.Equal(1, run.Statistics.WaitSitesExitContext);
        Assert.Equal(1, run.Statistics.WaitSitesCanonicalized);
        Assert.Empty(Codes(run, ConcurrencyDiagnosticCodes.WaitExitContext));
    }

    [Fact]
    public void Wait_ExitContext_Constant_True_Is_2921_And_Unchanged()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o)
                    {
                        var got = Monitor.Wait(o, 100, true);
                    }
                }
            }
            """;
        var run = RunSource(src);
        // 站点原样（无任何结构改动 → 输出为 null，runner 字节级复制）。
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.WaitExitContext).Count);
        Assert.Equal(1, run.Statistics.WaitSitesExitContext);
        Assert.Equal(0, run.Statistics.WaitSitesCanonicalized);
        Assert.Equal(1, run.Statistics.SkippedByReason[ConcurrencyDiagnosticCodes.WaitExitContext]);
    }

    [Fact]
    public void Wait_ExitContext_Variable_Is_2921_And_Unchanged()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o, bool ctx) {
                    lock (o)
                    {
                        var got = Monitor.Wait(o, 100, ctx);
                    }
                }
            }
            """;
        var run = RunSource(src);
        // 站点原样（无任何结构改动 → 输出为 null，runner 字节级复制）。
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.WaitExitContext).Count);
    }

    [Fact]
    public void Wait_Same_Field_Lock_Object_This_Qualified_Is_Proven()
    {
        var src = """
            using System;
            using System.Threading;
            class C {
                private object _gate = new object();

                bool M(TimeSpan ts) {
                    lock (this._gate)
                    {
                        return Monitor.Wait(_gate, ts);
                    }
                }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedContains(output, "int __cs2j_to = (int)ts.TotalMilliseconds;");
        AssertNormalizedContains(output, "return Monitor.Wait(_gate, __cs2j_to);");
        Assert.Equal(0, run.Statistics.OwnershipWarnings);
        Assert.Empty(Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven));
    }

    [Fact]
    public void Wait_Inside_Rewritten_EnterExit_Pair_Body_Is_Proven()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.Enter(o, ref t);
                    try
                    {
                        Monitor.Wait(o, 100);
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(o);
                    }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        AssertNormalizedEqual("""
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o) {
                        Monitor.Wait(o, 100);
                        Work();
                    }
                }
                void Work() { }
            }
            """, output);
        Assert.Equal(0, run.Statistics.OwnershipWarnings);
        Assert.Equal(1, run.Statistics.EnterExitPairsRewritten);
        Assert.Equal(1, run.Statistics.WaitSitesIntMs);
    }

    [Fact]
    public void Wait_Outside_Conditional_Lock_Is_2920()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o, bool cond) {
                    if (cond)
                    {
                        lock (o) { Work(); }
                    }
                    Monitor.Wait(o, 100);
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven).Count);
        Assert.Equal(1, run.Statistics.OwnershipWarnings);
        Assert.Equal(1, run.Statistics.SkippedByReason[ConcurrencyDiagnosticCodes.LockOwnershipUnproven]);
    }

    [Fact]
    public void Wait_On_Different_Object_Than_Lock_Is_2920()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object a, object b) {
                    lock (a)
                    {
                        Monitor.Wait(b, 100);
                    }
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven).Count);
    }

    [Fact]
    public void Wait_With_Property_LockExpression_Is_2920()
    {
        var src = """
            using System.Threading;
            class C {
                private object Gate => new object();

                void M() {
                    Monitor.Wait(Gate, 100);
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven).Count);
    }

    [Fact]
    public void Wait_With_MethodCall_LockExpression_Is_2920()
    {
        var src = """
            using System.Threading;
            class C {
                void M() {
                    Monitor.Wait(GetLock(), 100);
                }
                object GetLock() => new object();
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven).Count);
    }

    // ===================================================================
    // CN-5：Pulse / PulseAll 持锁校验与计数
    // ===================================================================

    [Fact]
    public void Pulse_And_PulseAll_In_Lock_Are_Counted_And_Proven()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    lock (o)
                    {
                        Monitor.Pulse(o);
                        Monitor.PulseAll(o);
                    }
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.PulseSites);
        Assert.Equal(1, run.Statistics.PulseAllSites);
        Assert.Equal(0, run.Statistics.OwnershipWarnings);
        Assert.Empty(Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven));
    }

    [Fact]
    public void Pulse_And_PulseAll_Not_In_Lock_Are_2920()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    Monitor.Pulse(o);
                    Monitor.PulseAll(o);
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(2, Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven).Count);
        Assert.Equal(2, run.Statistics.OwnershipWarnings);
    }

    // ===================================================================
    // CN-7 / CN-9：volatile 字段与 Volatile 静态 API 审计（不改写）
    // ===================================================================

    [Fact]
    public void Volatile_Primitive_Field_Is_Counted_Unchanged()
    {
        var src = """
            using System.Threading;
            class C {
                private volatile int _flag;

                void M() {
                    _flag = 1;
                    int read = _flag;
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.VolatileFieldsPrimitive);
        Assert.Equal(0, run.Statistics.VolatileFieldsNonPrimitive);
        Assert.Empty(Codes(run, ConcurrencyDiagnosticCodes.VolatileNonPrimitiveField));
    }

    [Fact]
    public void Volatile_NonPrimitive_Field_Is_2940_With_ReadWrite_Points_Unchanged()
    {
        var src = """
            using System.Threading;
            class C {
                private volatile object _state = new object();

                void M() {
                    var current = _state;
                    _state = new object();
                    object sink = current;
                }
            }
            """;
        var run = RunSource(src);
        // 树未动（runner 字节级复制），读写点原地保留。
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, run.Statistics.VolatileFieldsNonPrimitive);
        Assert.Equal(0, run.Statistics.VolatileFieldsPrimitive);
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.VolatileNonPrimitiveField).Count);
    }

    [Fact]
    public void Volatile_Static_Api_Sites_Are_Counted_2960_Unchanged()
    {
        var src = """
            using System.Threading;
            class C {
                private int _value;

                int Read() => Volatile.Read(ref _value);
                void Write(int v) => Volatile.Write(ref _value, v);
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(2, run.Statistics.VolatileStaticApiSites);
        Assert.Equal(2, Codes(run, ConcurrencyDiagnosticCodes.VolatileStaticApi).Count);
    }

    // ===================================================================
    // CN-10：lock 内 await 的防御镜像（基线 CS1996 优先）
    // ===================================================================

    [Fact]
    public void Await_In_Lock_With_Baseline_CS1996_Does_Not_Mirror_2930()
    {
        var src = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class C {
                private readonly object _o = new object();

                public async Task M() {
                    lock (_o)
                    {
                        await Task.Delay(1);
                    }
                }
            }
            """;
        var run = RunSourceAllowingErrors(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Empty(Codes(run, ConcurrencyDiagnosticCodes.AwaitInLockMirror));
        Assert.DoesNotContain(run.Statistics.SkippedByReason, kv =>
            kv.Key == ConcurrencyDiagnosticCodes.AwaitInLockMirror);
    }

    [Fact]
    public void Await_In_Lock_Defensive_Mirror_2930_When_Baseline_Missing()
    {
        // 合法 C#：lock 体内的 async lambda（其 await 在锁外未来执行）——
        // 基线无 CS1996，分析器词法保守镜像发 CS2J2930。
        var src = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class C {
                private readonly object _o = new object();

                public void M() {
                    lock (_o)
                    {
                        Func<Task> worker = async () => await Task.Delay(1);
                    }
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.AwaitInLockMirror).Count);
        Assert.Equal(1, run.Statistics.SkippedByReason[ConcurrencyDiagnosticCodes.AwaitInLockMirror]);
    }

    [Fact]
    public void Async_Method_Monitor_Pulse_Still_Goes_Ownership_Check_2920()
    {
        var src = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class C {
                public async Task M(object o) {
                    Monitor.Pulse(o);
                    await Task.CompletedTask;
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(1, Codes(run, ConcurrencyDiagnosticCodes.LockOwnershipUnproven).Count);
        Assert.Equal(1, run.Statistics.PulseSites);
    }

    // ===================================================================
    // CN-11：范围外同步原语（只计数，不改不阻断、不进残余）
    // ===================================================================

    [Fact]
    public void OutOfScope_Primitives_Are_Counted_Unchanged_And_Not_Residual()
    {
        var src = """
            using System.Threading;
            class C {
                void M() {
                    var sem = new SemaphoreSlim(1, 1);
                    sem.Wait();
                    var mutex = new Mutex(false);
                    mutex.WaitOne();
                    var ev = new ManualResetEventSlim(false);
                    ev.Wait();
                }
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.Equal(2, run.Statistics.OutOfScopeSitesByType["SemaphoreSlim"]);
        Assert.Equal(1, run.Statistics.OutOfScopeSitesByType["Mutex"]);
        Assert.Equal(1, run.Statistics.OutOfScopeSitesByType["WaitHandle"]);
        Assert.Equal(2, run.Statistics.OutOfScopeSitesByType["ManualResetEventSlim"]);
        var diagnostics = Codes(run, ConcurrencyDiagnosticCodes.OutOfScopePrimitive);
        Assert.Equal(6, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal(ConcurrencySeverity.Info, d.Severity));

        var (compilation, _) = Recompile(src);
        var residual = run.Preprocessor.ResidualScan(compilation, run.Statistics);
        Assert.Empty(residual);
        Assert.Equal(0, run.Statistics.ResidualMonitorCalls);
    }

    // ===================================================================
    // 跨文件：锁对象字段声明与使用分两个 syntax tree
    // ===================================================================

    [Fact]
    public void Cross_Tree_Field_Lock_Object_Binds_And_Rewrites_With_One_Bind()
    {
        var run = Run(
            ("A.cs", """
            partial class C
            {
                private object _gate = new object();
            }
            """),
            ("B.cs", """
            using System.Threading;
            partial class C
            {
                public void M()
                {
                    bool t = false;
                    Monitor.Enter(_gate, ref t);
                    try
                    {
                        Work();
                    }
                    finally
                    {
                        if (t) Monitor.Exit(_gate);
                    }
                }
                void Work() { }
            }
            """));
        Assert.Null(run.Output("A.cs"));
        var outputB = run.Output("B.cs")!;
        AssertNormalizedContains(outputB, "lock (_gate) { Work(); }");
        AssertNormalizedDoesNotContain(outputB, "Monitor.Enter");
        Assert.Equal(1, run.Statistics.EnterExitPairsRewritten);
        Assert.Equal(1, run.Statistics.FilesTransformed);
        Assert.Equal(1, run.Statistics.FilesSkippedClean);
        var plan = Assert.Single(run.Preprocessor.Plans);
        Assert.Contains("B.cs", plan.TouchedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("A.cs", plan.TouchedFiles, StringComparer.OrdinalIgnoreCase);
    }

    // ===================================================================
    // residual 闸门：无 plan 归属站点 → CS2J2970；strict 升级（经适配器契约）
    // ===================================================================

    [Fact]
    public void Residual_Unowned_Monitor_Site_In_Field_Initializer_Lambda_Emits_2970()
    {
        // 字段初始化器 lambda 内的 Monitor.Enter 无法归属任何普通方法 plan：
        // 分析侧 2910（无 plan 归属），残余闸门 2970。
        var src = """
            using System;
            using System.Threading;
            class C {
                private static readonly object _gate = new object();
                private static readonly Action _worker = () =>
                {
                    Monitor.Enter(_gate);
                };
            }
            """;
        var run = RunSource(src);
        Assert.Null(run.Output("Test.cs"));
        Assert.NotEmpty(Codes(run, ConcurrencyDiagnosticCodes.NonCanonicalStructure));

        var (compilation, _) = Recompile(src);
        var residual = run.Preprocessor.ResidualScan(compilation, run.Statistics);
        var gate = Assert.Single(residual);
        Assert.Equal(ConcurrencyDiagnosticCodes.ResidualMonitorGate, gate.Code);
        Assert.Equal(ConcurrencySeverity.Warning, gate.Severity);
        Assert.Equal(1, run.Statistics.ResidualMonitorCalls);
    }

    [Fact]
    public void Residual_Is_Zero_After_Rewrite()
    {
        var src = """
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.Enter(o, ref t);
                    try { Work(); } finally { if (t) Monitor.Exit(o); }
                }
                void Work() { }
            }
            """;
        var run = RunSource(src);
        var output = run.Output("Test.cs")!;
        var (compilation, _) = Recompile(output);
        var residual = run.Preprocessor.ResidualScan(compilation, run.Statistics);
        Assert.Empty(residual);
        Assert.Equal(0, run.Statistics.ResidualMonitorCalls);
    }

    [Fact]
    public void Rewritten_Output_Second_Pass_Is_Idempotent_Zero_Diff()
    {
        var tryEnterSrc = """
            using System.Threading;
            class C {
                void M(object o) {
                    if (Monitor.TryEnter(o, 50))
                    {
                        try { Work(); } finally { Monitor.Exit(o); }
                    }
                    else { Fallback(); }
                }
                void Work() { }
                void Fallback() { }
            }
            """;
        var first = RunSource(tryEnterSrc);
        var canonical = first.Output("Test.cs")!;
        var second = RunSource(canonical);
        Assert.Null(second.Output("Test.cs"));

        var waitSrc = """
            using System;
            using System.Threading;
            class C {
                bool M(object o, TimeSpan ts) {
                    lock (o)
                    {
                        return Monitor.Wait(o, ts);
                    }
                }
            }
            """;
        var firstWait = RunSource(waitSrc);
        var secondWait = RunSource(firstWait.Output("Test.cs")!);
        Assert.Null(secondWait.Output("Test.cs"));

        var refFormSrc = """
            using System.Threading;
            class C {
                void M(object o) {
                    bool t = false;
                    Monitor.TryEnter(o, ref t);
                    try { Work(); } finally { if (t) Monitor.Exit(o); }
                }
                void Work() { }
            }
            """;
        var firstRef = RunSource(refFormSrc);
        var secondRef = RunSource(firstRef.Output("Test.cs")!);
        Assert.Null(secondRef.Output("Test.cs"));
    }

    [Fact]
    public async Task Strict_Via_Adapter_Escalates_2910_To_Error()
    {
        var (compilation, model) = BuildAdapterCompilation();
        var adapter = new ConcurrencyNormalizePassAdapter(
            new ConcurrencyOptions(), verbose: false, filterUnsupportedProjects: false,
            compatNamespacePrefixes: null, verifyBuild: true, verifyTests: true, strict: true);
        await adapter.CompilationReadyAsync(compilation);
        await adapter.RewriteFileAsync(new PassFileContext
        {
            SourceCode = ConditionalEnterSource,
            SourceFilePath = "Test.cs",
            RelativePath = "Test.cs",
            DestinationFilePath = "Test.cs",
            SemanticModel = model,
        });

        var nonCanonical = adapter.Diagnostics
            .Where(d => d.Code == ConcurrencyDiagnosticCodes.NonCanonicalStructure).ToList();
        Assert.NotEmpty(nonCanonical);
        // 严格模式下所有 2910（配对拒绝 + 孤立 Exit）都升级为 Error。
        Assert.All(nonCanonical, d => Assert.Equal(ConcurrencySeverity.Error, d.Severity));
        Assert.True(adapter.HasErrors);
    }

    [Fact]
    public async Task NonStrict_Via_Adapter_Keeps_2910_As_Warning()
    {
        var (compilation, model) = BuildAdapterCompilation();
        var adapter = new ConcurrencyNormalizePassAdapter(
            new ConcurrencyOptions(), verbose: false, filterUnsupportedProjects: false,
            compatNamespacePrefixes: null, verifyBuild: true, verifyTests: true, strict: false);
        await adapter.CompilationReadyAsync(compilation);
        await adapter.RewriteFileAsync(new PassFileContext
        {
            SourceCode = ConditionalEnterSource,
            SourceFilePath = "Test.cs",
            RelativePath = "Test.cs",
            DestinationFilePath = "Test.cs",
            SemanticModel = model,
        });

        var nonCanonical = adapter.Diagnostics
            .Where(d => d.Code == ConcurrencyDiagnosticCodes.NonCanonicalStructure).ToList();
        Assert.NotEmpty(nonCanonical);
        // 非严格模式保持 Warning（设计 §6：Warning 可经 strict 升 Error）。
        Assert.All(nonCanonical, d => Assert.Equal(ConcurrencySeverity.Warning, d.Severity));
        Assert.False(adapter.HasErrors);
    }

    private const string ConditionalEnterSource = """
        using System.Threading;
        class C {
            void M(object o, bool cond) {
                bool t = false;
                if (cond)
                {
                    Monitor.Enter(o, ref t);
                }
                try
                {
                    Work();
                }
                finally
                {
                    if (t) Monitor.Exit(o);
                }
            }
            void Work() { }
        }
        """;

    private static (CSharpCompilation Compilation, SemanticModel Model) BuildAdapterCompilation()
    {
        var tree = CSharpSyntaxTree.ParseText(
            ConditionalEnterSource, new CSharpParseOptions(LanguageVersion.Latest), path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "concurrency-normalize-adapter-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (compilation, compilation.GetSemanticModel(tree));
    }
}
