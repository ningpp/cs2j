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

    [Fact]
    public void CoalesceAssign_Simple_Identifier_No_Temp_Variable()
    {
        var src = """
        class C {
            void M(string? a, string b) {
                a ??= b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Contains("if (a == null)", result.OutputCode);
        Assert.DoesNotContain("___t", result.OutputCode);
    }

    [Fact]
    public void CoalesceAssign_Member_Access_Generates_Temp_Variable()
    {
        var src = """
        class C {
            public string? Timeout;
            void M(C config, int val) {
                config.Timeout ??= val.ToString();
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("if (___t1 == null)", result.OutputCode);
        Assert.Contains("config.Timeout =", result.OutputCode);
    }

    [Fact]
    public void CoalesceAssign_Indexer_Generates_Temp_Variable()
    {
        var src = """
        using System.Collections.Generic;
        class C {
            void M(Dictionary<string, string> cache, string key, string val) {
                cache[key] ??= val;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Contains("___t1", result.OutputCode);
        Assert.Contains("if (___t1 == null)", result.OutputCode);
        Assert.Contains("cache[key] =", result.OutputCode);
    }

    [Fact]
    public void CoalesceAssign_Statistics_Counted()
    {
        var src = """
        class C {
            void M(string? a, string b, string? c, string d) {
                a ??= b;
                c ??= d;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.NullCoalescingAssignRewritten);
    }

    [Fact]
    public void CoalesceAssign_Simple_No_Residual()
    {
        var src = """
        class C {
            void M(string? a, string b) {
                a ??= b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    // ---- C51 guard (design §3.5): '??=' inside a ConditionalAccess is left
    // intact for the C51 assignment rule instead of being turned into an
    // IfStatement in an expression slot with a bare binding left.

    [Fact]
    public void Guard_ConditionalMemberCoalesceAssign_LeftIntact_NotRewritten()
    {
        var src = """
        class C {
            public string? B;
            void M(C? a, string c) {
                a?.B ??= c;
            }
        }
        """;
        var result = Preprocess(src);

        // The E03 entry always reformats a dirty file; the proof is shape:
        // the guarded statement is NOT turned into an IfStatement/temp pair.
        Assert.Equal(1, result.Statistics.SkippedUnsupported);
        Assert.Contains("a?.B ??= c", result.OutputCode);
        Assert.DoesNotContain("___t", result.OutputCode);
        Assert.DoesNotContain("if (", result.OutputCode);
    }

    [Fact]
    public void Guard_ConditionalIndexCoalesceAssign_LeftIntact_NotRewritten()
    {
        var src = """
        using System.Collections.Generic;
        class C {
            void M(Dictionary<string, string>? a, string k, string c) {
                a?[k] ??= c;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(1, result.Statistics.SkippedUnsupported);
        Assert.Contains("a?[k] ??= c", result.OutputCode);
        Assert.DoesNotContain("___t", result.OutputCode);
        Assert.DoesNotContain("if (", result.OutputCode);
    }

    [Fact]
    public void Guard_ValueFormConditionalCoalesceAssign_LeftIntact()
    {
        var src = """
        class C {
            public string? B;
            string? M(C? a, string c) {
                return a?.B ??= c;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(1, result.Statistics.SkippedUnsupported);
        Assert.Contains("a?.B ??= c", result.OutputCode);
        Assert.DoesNotContain("___t", result.OutputCode);
    }

    [Fact]
    public void Guard_OrdinaryMemberCoalesceAssign_StillRewritten()
    {
        // The guard must not over-trigger: a non-binding member left keeps
        // the ordinary E03 lowering.
        var src = """
        class C {
            public string? Timeout;
            void M(C config, int val) {
                config.Timeout ??= val.ToString();
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.SkippedUnsupported);
        Assert.Contains("___t1", result.OutputCode);
    }
}
