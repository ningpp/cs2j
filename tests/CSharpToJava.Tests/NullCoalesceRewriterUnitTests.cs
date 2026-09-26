using CSharpToJava.Core.NullCoalesceRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

public class NullCoalesceRewriterUnitTests
{
    private static NullCoalesceResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("null-coalesce-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new NullCoalesceRewriter().Preprocess(source, model);
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
    public void Simple_Coalesce_Variable_Declaration_Generates_Temp()
    {
        var src = """
        class C {
            void M(string? x, string y) {
                var a = x ?? y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("___t1 != null", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Method_Call_Left_Operand_Generates_Temp()
    {
        var src = """
        class C {
            string? Get() => null;
            void M(string y) {
                var a = Get() ?? y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("Get()", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Multiple_Coalesce_In_Same_Statement_Generates_Independent_Temps()
    {
        var src = """
        class C {
            void M(string? a, string? b, string? c, string? d) {
                var z = (a ?? b) + (c ?? d);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("___t2", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Nested_Coalesce_Unfolded_From_Inside_Out()
    {
        var src = """
        class C {
            void M(string? a, string? b, string? c, string? d) {
                var r = a ?? b ?? c ?? d;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("___t2", result.OutputCode);
        Assert.Contains("___t3", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Coalesce_In_Return_Statement()
    {
        var src = """
        class C {
            string? Get() => null;
            string M(string fallback) {
                return Get() ?? fallback;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Coalesce_In_Method_Argument()
    {
        var src = """
        class C {
            string? Get() => null;
            void Foo(string s) {}
            void M(string fallback) {
                Foo(Get() ?? fallback);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("___t1", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
    }

    [Fact]
    public void Non_Coalesce_BinaryExpression_Not_Rewritten()
    {
        var src = """
        class C {
            void M(int x, int y) {
                var a = x + y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Regular_Assignment_Not_Rewritten()
    {
        var src = """
        class C {
            void M() {
                var a = "x";
                a = "y";
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Output_Has_No_Residual_Coalesce_Syntax()
    {
        var src = """
        class C {
            void M(string? x, string y) {
                var a = x ?? y;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    [Fact]
    public void Statistics_Count_Coalesce_Rewrites()
    {
        var src = """
        class C {
            void M(string? a, string? b, string? c) {
                var x = a ?? b;
                var y = c ?? "default";
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.NullCoalescingRewritten);
        Assert.Equal(2, result.Statistics.TempVariablesGenerated);
    }
}
