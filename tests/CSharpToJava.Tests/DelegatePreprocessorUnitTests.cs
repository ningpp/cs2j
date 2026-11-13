using CSharpToJava.Core.DelegatePreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class DelegatePreprocessorUnitTests
{
    private static DelegatePreprocessorResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("delegate-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Delegate).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Action).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new DelegatePreprocessor().Preprocess(source, model);
    }

    /// <summary>
    /// Minimal stand-in for the emitted Cs2jPrelude.CsPrelude so rewritten output
    /// can be compiled in-process without referencing the support library project.
    /// </summary>
    private const string PreludeStub = """
        namespace Cs2jPrelude
        {
            public static class CsPrelude
            {
                public static T Combine<T>(T a, T b) where T : System.Delegate
                    => (T)System.Delegate.Combine(a, b);

                public static T Combine<T>(params T[] delegates) where T : System.Delegate
                    => (T)System.Delegate.Combine(delegates);

                public static T Remove<T>(T a, T b) where T : System.Delegate
                    => (T)System.Delegate.Remove(a, b);
            }
        }
        """;

    private static void AssertCompiles(string source)
    {
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(PreludeStub),
            CSharpSyntaxTree.ParseText(source),
        };
        var compilation = CSharpCompilation.Create("delegate-compile-test",
            trees,
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Delegate).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Action).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    [Fact]
    public void Clean_File_Is_Byte_Level_Preserved()
    {
        var src = """
        class C {
            void M() {
                var a = "hello";
                System.Console.WriteLine(a);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Delegate_Combine_Two_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2) {
                Action d = d1 + d2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
        Assert.Contains("d1", result.OutputCode);
        Assert.Contains("d2", result.OutputCode);
        Assert.DoesNotContain("d1 + d2", result.OutputCode);
    }

    [Fact]
    public void Delegate_Remove_Two_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2) {
                Action d = d1 - d2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Remove", result.OutputCode);
        Assert.Contains("d1", result.OutputCode);
        Assert.Contains("d2", result.OutputCode);
        Assert.DoesNotContain("d1 - d2", result.OutputCode);
    }

    [Fact]
    public void Numeric_Add_Not_Rewritten()
    {
        var src = """
        class C {
            void M() {
                int x = 1 + 2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundAdd_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d, Action h) {
                d += h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
        Assert.DoesNotContain("d += h", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundSubtract_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d, Action h) {
                d -= h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Remove", result.OutputCode);
        Assert.DoesNotContain("d -= h", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundAdd_Field_Rewritten()
    {
        var src = """
        using System;
        class C {
            Action _field;
            void M(Action h) {
                _field += h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundAdd_Property_Rewritten()
    {
        var src = """
        using System;
        class C {
            Action Prop { get; set; }
            void M(Action h) {
                Prop += h;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
    }

    [Fact]
    public void Delegate_CompoundAdd_UnstableLeft_Skipped()
    {
        var src = """
        using System;
        class C {
            void M(Action[] arr, int i, Action h) {
                arr[i++] += h;
            }
        }
        """;
        var result = Preprocess(src);
        // Should not rewrite — arr[i++] is not a stable reference
        Assert.Contains("arr[i++] += h", result.OutputCode);
    }

    [Fact]
    public void Delegate_Multi_Combine_Three_Uses_Params()
    {
        var src = """
        using System;
        class C {
            void M(Action a, Action b, Action c) {
                Action d = a + b + c;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
        Assert.Contains("a", result.OutputCode);
        Assert.Contains("b", result.OutputCode);
        Assert.Contains("c", result.OutputCode);
        // Should not contain nested Combine calls (flattened to params)
        Assert.DoesNotContain("Cs2jPrelude.CsPrelude.Combine(Cs2jPrelude.CsPrelude.Combine", result.OutputCode);
    }

    [Fact]
    public void Delegate_Nested_Combine_Remove()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2, Action d3) {
                Action d = (d1 + d2) - d3;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Remove", result.OutputCode);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
    }

    [Fact]
    public void Delegate_Right_Nested_Not_Flattened()
    {
        var src = """
        using System;
        class C {
            void M(Action a, Action b, Action c) {
                Action d = a + (b + c);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Right-nested: do not flatten, produce Combine(a, Combine(b, c))
        Assert.Contains("Cs2jPrelude.CsPrelude.Combine(a", result.OutputCode);
        Assert.Contains("Cs2jPrelude.CsPrelude.Combine(b", result.OutputCode);
    }

    [Fact]
    public void Delegate_Remove_Not_Flattened()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2, Action d3) {
                Action d = d1 - d2 - d3;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jPrelude.CsPrelude.Remove(Cs2jPrelude.CsPrelude.Remove", result.OutputCode);
    }

    [Fact]
    public void AnonymousMethod_ExplicitParams_To_Lambda()
    {
        var src = """
        using System;
        class C {
            void M() {
                Action<int> f = delegate(int x) { return; };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("=>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }

    [Fact]
    public void AnonymousMethod_NoParam_To_Empty_Lambda()
    {
        var src = """
        using System;
        class C {
            void M() {
                Action f = delegate { };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("=>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }

    [Fact]
    public void AnonymousMethod_Async_To_Async_Lambda()
    {
        var src = """
        using System;
        using System.Threading.Tasks;
        class C {
            async Task M() {
                Func<Task> f = async delegate { await Task.Delay(1); };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("async", result.OutputCode);
        Assert.Contains("=>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }

    [Fact]
    public void Delegate_Declaration_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            delegate void D(int x);
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void AnonymousMethod_As_MethodArg_To_Lambda()
    {
        var src = """
        using System;
        class C {
            void Run(Action<int> f) { }
            void M() {
                Run(delegate(int x) { });
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("=>", result.OutputCode);
        Assert.DoesNotContain("delegate", result.OutputCode);
    }

    [Fact]
    public void Event_CompoundAdd_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            event Action E;
            void M(Action h) {
                E += h;
            }
        }
        """;
        var result = Preprocess(src);
        // Events are skipped — left to Event preprocessor
        Assert.Contains("E += h", result.OutputCode);
    }

    [Fact]
    public void Explicit_DelegateCombine_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2) {
                Action d = (Action)Delegate.Combine(d1, d2);
            }
        }
        """;
        var result = Preprocess(src);
        // Explicit Delegate.Combine calls are preserved
        Assert.Contains("Delegate.Combine(d1, d2)", result.OutputCode);
    }

    [Fact]
    public void MethodGroup_Conversion_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M() { }
            void M2() {
                Action d = M;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Lambda_Not_Rewritten()
    {
        var src = """
        using System;
        class C {
            void M() {
                Action f = () => { };
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Delegate_Combine_With_MethodGroup()
    {
        var src = """
        using System;
        class C {
            void M() { }
            void M2(Action d) {
                Action r = d + M;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
    }

    [Fact]
    public void Delegate_Combine_With_Lambda()
    {
        var src = """
        using System;
        class C {
            void M(Action d) {
                Action r = d + (() => { });
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
    }

    [Fact]
    public void Generic_Delegate_Combine()
    {
        var src = """
        using System;
        class C {
            void M(Func<int, int> f1, Func<int, int> f2) {
                Func<int, int> r = f1 + f2;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("CsPrelude.Combine", result.OutputCode);
    }

    [Fact]
    public void Delegate_Member_In_String_Concat_Not_Rewritten()
    {
        var src = """
        using System;
        struct S {
            public object Item1;
            public Action Item2;
            public int Item3;
            public int Item4;
            public override string ToString() {
                return "(" + Item1 + ", " + Item2 + ", " + Item3 + ", " + Item4 + ")";
            }
        }
        """;
        var result = Preprocess(src);
        // "string + delegate" is concatenation: the chain's bound type is string
        Assert.False(result.Changed);
        Assert.DoesNotContain("CsPrelude.Combine", result.OutputCode);
        Assert.Equal(src, result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Delegate_Combine_Inside_String_Concat_Still_Rewritten()
    {
        var src = """
        using System;
        class C {
            string M(Action d1, Action d2) {
                return (d1 + d2) + "x";
            }
        }
        """;
        var result = Preprocess(src);
        // Outer node binds to string and is left alone; the inner delegates still combine
        Assert.True(result.Changed);
        Assert.Contains("Cs2jPrelude.CsPrelude.Combine(", result.OutputCode);
        Assert.DoesNotContain("Cs2jPrelude.CsPrelude.Combine(Cs2jPrelude.CsPrelude.Combine", result.OutputCode);
        Assert.Contains("+ \"x\"", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Delegate_Combine_Three_Flattened_And_Compiles()
    {
        var src = """
        using System;
        class C {
            Action M(Action d1, Action d2, Action d3) {
                return d1 + d2 + d3;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jPrelude.CsPrelude.Combine(", result.OutputCode);
        Assert.Contains("d1", result.OutputCode);
        Assert.Contains("d2", result.OutputCode);
        Assert.Contains("d3", result.OutputCode);
        Assert.DoesNotContain("Cs2jPrelude.CsPrelude.Combine(Cs2jPrelude.CsPrelude.Combine", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void AnonymousMethod_NoParam_In_Overloaded_Call_With_Unresolved_Body_Uses_Target_Arity()
    {
        var src = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        class C {
            void M() {
                Task.Factory.StartNew(delegate
                {
                    Unresolved.Assert(1);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // The only four-argument StartNew overload takes a parameter-less Action, so
        // the lambda must stay parameter-less even though the unresolved identifier in
        // the body leaves the call unbindable in this analysis-only compilation.
        Assert.Contains("StartNew(()=>", result.OutputCode);
        Assert.DoesNotContain("(p0)", result.OutputCode);
    }

    [Fact]
    public void Idempotent_Second_Run_Produces_Same_Output()
    {
        var src = """
        using System;
        class C {
            void M(Action d1, Action d2, Action h) {
                Action d = d1 + d2;
                d += h;
            }
        }
        """;
        var first = Preprocess(src);
        Assert.True(first.Changed);
        var second = Preprocess(first.OutputCode);
        // Second run: output should be byte-identical
        Assert.Equal(first.OutputCode, second.OutputCode);
    }
}
