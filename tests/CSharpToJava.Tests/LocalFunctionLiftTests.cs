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
    // Helpers
    // -----------------------------------------------------------------------

    private static LocalFunctionLiftResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("local-fn-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new LocalFunctionLiftPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
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
