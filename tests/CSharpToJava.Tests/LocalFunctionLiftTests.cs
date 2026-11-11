using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class LocalFunctionLiftTests
{
    // -----------------------------------------------------------------------
    // Baseline tests (Task 9)
    // -----------------------------------------------------------------------

    [Fact]
    public void NoLocalFunctions_ReturnsUnchanged()
    {
        var src = "class C { public int M() => 1; }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void SimpleLocalFunction_LiftedToPrivateMethod()
    {
        var src = """
        class C {
            int M(int x) {
                return Square(x);
                int Square(int v) => v * v;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private int square(int v)", result.OutputCode);
        Assert.Contains("square(x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void StaticLocalFunction_LiftedAsPrivateStatic()
    {
        var src = """
        class C {
            int M(int x) {
                return Add(x, 1);
                static int Add(int a, int b) => a + b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("private static int add(int a, int b)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Naming_CamelCase()
    {
        var src = """
        class C {
            void M() {
                ParseOffset(1);
                void ParseOffset(int x) { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void parseOffset(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Naming_MemberConflict_AppendsSuffix()
    {
        var src = """
        class C {
            int square;
            void M() {
                Square(1);
                void Square(int x) { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void square2(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Modifiers_AsyncPreserved()
    {
        var src = """
        class C {
            async System.Threading.Tasks.Task M() {
                await DoAsync();
                async System.Threading.Tasks.Task DoAsync() => System.Threading.Tasks.Task.CompletedTask;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private async", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Body_ExpressionBody_NonVoid_ConvertedToReturnBlock()
    {
        var src = """
        class C {
            int M() {
                return GetValue();
                int GetValue() => 42;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private int getValue()", result.OutputCode);
        Assert.Contains("return 42", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Body_ExpressionBody_Void_ConvertedToExpressionStatement()
    {
        var src = """
        class C {
            void M() {
                DoWork();
                void DoWork() => System.Console.WriteLine("hi");
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void doWork()", result.OutputCode);
        Assert.Contains("Console.WriteLine(\"hi\")", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // Capture and call site tests (Task 10)
    // -----------------------------------------------------------------------

    [Fact]
    public void Capture_ReadOnly_BecomesValueParameter()
    {
        var src = """
        class C {
            int M() {
                int x = 10;
                return GetX();
                int GetX() => x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("_cs2j_cap_x", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Capture_Written_BecomesRefParameter()
    {
        var src = """
        class C {
            int M() {
                int best = -1;
                Search(3);
                return best;
                void Search(int v) {
                    if (v > best) best = v;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("ref int _cs2j_cap_best", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void CallSite_Recursive()
    {
        var src = """
        class C {
            int M(int n) {
                return Fib(n);
                int Fib(int k) => k <= 1 ? k : Fib(k - 1) + Fib(k - 2);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("fib(n)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void CallSite_NameOf_RenamedNoArgs()
    {
        var src = """
        class C {
            string M() {
                return nameof(Helper);
                void Helper() { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("nameof(helper)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    /// <summary>
    /// Iterator local functions are lifted, not skipped: the hoisted method keeps the
    /// yield statements and is a valid iterator method at class level (see the
    /// deliberately disabled iterator gate in LocalFunctionLiftAnalyzer).
    /// </summary>
    [Fact]
    public void Iterator_LiftedToClassLevelIteratorMethod()
    {
        var src = """
        class C {
            System.Collections.Generic.IEnumerable<int> M() {
                return GetSeq();
                System.Collections.Generic.IEnumerable<int> GetSeq() {
                    yield return 1;
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.DoesNotContain(result.Diagnostics, d => d.Reason == LocalFunctionSkipReason.Iterator);
        // The hoisted method is declared at class level and keeps the yield statement.
        Assert.Contains("private System.Collections.Generic.IEnumerable<int> getSeq()", result.OutputCode);
        Assert.Contains("yield return 1;", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Statistics_CorrectCounts()
    {
        var src = """
        class C {
            void M() {
                A();
                B();
                void A() { }
                void B() { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, result.Statistics.FilesTransformed);
    }

    // -----------------------------------------------------------------------
    // V-09: local functions declared directly inside a switch section
    // (the rt10 JsonElement.DeepEquals shape — switch statement, section
    // statement list, NOT a nested block). Before the fix the class-level
    // method was generated and every call site was renamed while the
    // original declaration survived: a half-rewritten tree that only the
    // strict residual count caught.
    // -----------------------------------------------------------------------

    [Fact]
    public void SwitchSectionLocalFunction_DeclarationRemovedAfterLift()
    {
        var src = """
        class C {
            bool DeepEquals(int kind, int a, int b) {
                switch (kind) {
                    case 1:
                        return NameEquals(a, b);
                    default:
                        return NameEquals(a, b);
                        static bool NameEquals(int l, int r) { return l == r; }
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private static bool nameEquals(int l, int r)", result.OutputCode);
        Assert.Contains("nameEquals(a, b)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void SwitchSectionLocalFunction_InCaseLabel_Removed()
    {
        var src = """
        class C {
            int M(int kind) {
                switch (kind) {
                    case 0:
                        return Compute(1);
                        static int Compute(int x) { return x * 2; }
                    default:
                        return 0;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("private static int compute(int x)", result.OutputCode);
        Assert.Contains("compute(1)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void SwitchSectionLocalFunctions_MultipleSections_AllRemoved()
    {
        var src = """
        class C {
            int M(int kind) {
                switch (kind) {
                    case 0:
                        return Twice(1);
                        static int Twice(int x) { return x * 2; }
                    case 1:
                        return Triple(1);
                        static int Triple(int x) { return x * 3; }
                    default:
                        return 0;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private static int twice(int x)", result.OutputCode);
        Assert.Contains("private static int triple(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void SwitchSectionLocalFunction_InsideNestedBlock_StillLifted()
    {
        // Regression guard: a local function inside a block nested in a
        // switch section always went through the VisitBlock channel.
        var src = """
        class C {
            int M(int kind) {
                switch (kind) {
                    case 0:
                        {
                            return Compute(1);
                            static int Compute(int x) { return x * 2; }
                        }
                    default:
                        return 0;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("private static int compute(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // Capture propagation naming: a callee's capture parameter name must be
    // made unique in the caller's lifted signature. Two different symbols can
    // share the same original name (e.g. the caller's own local `node` and a
    // callee's parameter `node`), and each plan assigns `_cs2j_cap_node`
    // independently. Merging the callee captures verbatim produced two
    // `_cs2j_cap_node` parameters (CS0100).
    // -----------------------------------------------------------------------

    [Fact]
    public void CapturePropagation_SameNameAsExistingCapture_DeduplicatesParameterNames()
    {
        var src = """
        class C {
            void M() {
                int node = 1;
                Run(2);
                void Run(int node) {
                    Write();
                    Helper();
                    void Write() => System.Console.WriteLine(node);
                }
                void Helper() {
                    System.Console.WriteLine(node);
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        AssertNoLocalFunctions(result.OutputCode);

        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(result.OutputCode).GetRoot();
        var lifted = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "run");
        var parameterNames = lifted.ParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToList();
        Assert.Equal(parameterNames.Count, parameterNames.Distinct(StringComparer.Ordinal).Count());

        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // CS1628: a write capture becomes a `ref` parameter, and C# forbids using
    // ref parameters inside lambdas/anonymous methods. When lifting the body
    // would place such a use inside a lambda, the function is retained
    // (function-level, intentional) instead of emitting illegal code.
    // -----------------------------------------------------------------------

    [Fact]
    public void WriteCapture_MethodGroupReferenceInBody_SkipsLiftAndCompiles()
    {
        // Mirrors RegexCompiler.EmitNode: the body contains a method group
        // reference to the local function itself, which the rewriter would
        // turn into `() => inc(ref _cs2j_cap_x)` inside the lifted body.
        var src = """
        class C {
            void M() {
                int x = 0;
                Inc();
                Other();
                void Inc() {
                    System.Action a = Inc;
                    x++;
                    a();
                }
                void Other() { }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.RefCaptureInNestedLambda
            && d.IntentionallyRetained);
        // The other local function is still lifted; only the offending one is kept.
        Assert.Contains("private void other()", result.OutputCode);
        Assert.Contains("void Inc()", result.OutputCode);
        Assert.DoesNotContain("_cs2j_cap_", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void WriteCapture_ReferencedInsideNestedLambda_SkipsLiftAndCompiles()
    {
        var src = """
        class C {
            void M() {
                int x = 0;
                Inc();
                Other();
                void Inc() {
                    System.Action a = () => x++;
                    a();
                }
                void Other() { }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.RefCaptureInNestedLambda);
        Assert.Contains("private void other()", result.OutputCode);
        Assert.DoesNotContain("_cs2j_cap_", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ReadOnlyCapture_ReferencedInsideNestedLambda_StillLifts()
    {
        // Read-only captures are passed by value, so the lambda may use them.
        var src = """
        class C {
            int M() {
                int x = 1;
                return Inc();
                int Inc() {
                    System.Func<int> f = () => x + 1;
                    return f();
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private int inc(int _cs2j_cap_x)", result.OutputCode);
        Assert.Contains("() => _cs2j_cap_x + 1", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static LocalFunctionLiftResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("local-fn-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new LocalFunctionLiftPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    };

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("local-fn-output",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should compile without errors. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    private static void AssertNoLocalFunctions(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
    }

    private static void AssertNoSyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should be valid C#. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }
}
