using CSharpToJava.Core.NullCoalesceRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

public class NullCoalesceRewriterUnitTests
{
    private static readonly MetadataReference[] CompileReferences =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
    };

    private static NullCoalesceResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("null-coalesce-test",
            new[] { tree },
            CompileReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new NullCoalesceRewriter().Preprocess(source, model);
    }

    /// <summary>
    /// Compiles rewritten output and returns error-level diagnostics. Every
    /// regression below asserts the rewrite is not only shaped right but also
    /// produces valid C# — the old anchor-by-identity lifter silently emitted
    /// undeclared temp variables (CS0103).
    /// </summary>
    private static List<Diagnostic> OutputCompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("null-coalesce-test-output",
            new[] { tree },
            CompileReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
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
    public void CoalesceAssign_Member_Access_Emits_Direct_Guard()
    {
        // The historical temp form (`var ___t = config.Timeout;
        // if (___t == null) config.Timeout = ...;`) never wrote through the
        // temp, so it was dead code that (once hoisting was repaired) merely
        // added a redundant read. The correct guard tests and assigns the
        // lvalue directly.
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
        Assert.DoesNotContain("___t", result.OutputCode);
        Assert.Contains("if (config.Timeout == null)", result.OutputCode);
        Assert.Contains("config.Timeout = val.ToString()", result.OutputCode);
    }

    [Fact]
    public void CoalesceAssign_Indexer_Emits_Direct_Guard()
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
        Assert.DoesNotContain("___t", result.OutputCode);
        Assert.Contains("if (cache[key] == null)", result.OutputCode);
        Assert.Contains("cache[key] = val", result.OutputCode);
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
        Assert.Contains("if (config.Timeout == null)", result.OutputCode);
        Assert.DoesNotContain("___t", result.OutputCode);
    }

    // ---- Regression group: hoist landing + ??= in value context (2026-10-11) ----
    //
    // Two coupled defects crashed the SDK conversion in null-coalesce:
    //  1. The second-pass lifter matched guards against ORIGINAL anchor nodes
    //     inside the REWRITTEN tree, so every hoisted declaration was dropped
    //     and the output referenced undeclared temp variables (CS0103).
    //  2. '??=' used as a value (object-creation argument in an arrow method,
    //     e.g. RegexCompiler.RentInt32Local) made VisitAssignmentExpression
    //     return an IfStatementSyntax into an expression slot, throwing
    //     InvalidCastException (IfStatementSyntax -> ExpressionSyntax).

    [Fact]
    public void Regression_Hoisted_Temp_Declaration_Actually_Lands_Before_Statement()
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
        // The declaration must be present in a statement slot — previously it
        // was silently dropped, leaving ___t1 undeclared.
        Assert.Contains("var ___t1 = x;", result.OutputCode);
        Assert.Contains("var a = (___t1 != null ? ___t1 : y);", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_CoalesceAssign_In_ObjectCreation_Argument_ArrowBody_Expands_To_Block_Guard()
    {
        // Exact SDK shape (RegexCompiler.RentInt32Local /
        // CompiledRegexRunnerFactory): '=> new R(_f ??= Make())'. The arrow
        // body is expanded to a block so the if-guard lands in the method body
        // (never at declaration time) and the argument reads the post-assign
        // field — previously this path returned an IfStatementSyntax into an
        // expression slot (InvalidCastException), then an inline ternary.
        var src = """
        class Maker {}
        class Rented { public Rented(Maker m) {} }
        class C {
            Maker? _pool;
            Maker Make() => new Maker();
            Rented Rent() => new Rented(_pool ??= Make());
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Equal(1, result.Statistics.NullCoalescingAssignRewritten);
        Assert.Contains("if (_pool == null)", result.OutputCode);
        Assert.Contains("_pool = Make();", result.OutputCode);
        Assert.Contains("return new Rented(_pool);", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_CoalesceAssign_In_Arrow_Property_Expands_To_Block_Getter()
    {
        var src = """
        class C {
            Group? _group;
            sealed class Group { public Group(C c, object? x) {} }
            Group Groups => _group ??= new Group(this, null);
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("if (_group == null)", result.OutputCode);
        Assert.Contains("_group = new Group(this, null);", result.OutputCode);
        Assert.Contains("return _group;", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_CoalesceAssign_Identifier_Value_In_Block_Hoists_Guard_And_Uses_Identifier()
    {
        var src = """
        class C {
            string? _cache;
            string Make() => "x";
            void Use(string s) {}
            void M() {
                Use(_cache ??= Make());
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("if (_cache == null)", result.OutputCode);
        Assert.Contains("_cache = Make()", result.OutputCode);
        // The value of '??=' is the post-assignment lvalue.
        Assert.Contains("Use(_cache);", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_CoalesceAssign_Indexer_Value_In_Block_Hoists_Guard_And_Uses_Lvalue()
    {
        // RegexPrefixAnalyzer shape: (classes[i] ??= new C()).AddChar(x)
        var src = """
        class Box { public string? V { get; set; } }
        class C {
            Box[] classes = new Box[1];
            void M(int i, string s) {
                (classes[i].V ??= s).ToString();
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("if (classes[i].V == null)", result.OutputCode);
        Assert.Contains("classes[i].V = s", result.OutputCode);
        // The original parenthesized receiver stays parenthesized.
        Assert.Contains("(classes[i].V).ToString()", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_Two_Value_CoalesceAssigns_In_One_Statement_Hoist_Two_Guards()
    {
        var src = """
        class C {
            string? _a;
            string? _b;
            string Make() => "x";
            void Use(string x, string y) {}
            void M() {
                Use(_a ??= Make(), _b ??= Make());
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.NullCoalescingAssignRewritten);
        Assert.Contains("if (_a == null)", result.OutputCode);
        Assert.Contains("if (_b == null)", result.OutputCode);
        Assert.Contains("Use(_a, _b);", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_CoalesceAssign_In_Expression_Bodied_Lambda_Expands_To_Block_Lambda_Inside_Scope()
    {
        // The arrow lambda is expanded into a block lambda so the if-guard
        // lands INSIDE the lambda (it must run on invocation, not at
        // declaration time); no guard may escape into the enclosing M() body.
        var src = """
        using System;
        class C {
            string? _s;
            string Make() => "x";
            Func<string> M() {
                return () => _s ??= Make();
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        // Exactly one guard, and it sits between the lambda braces — the
        // enclosing M() body contains only a return.
        Assert.Contains("() =>", result.OutputCode);
        Assert.Contains("if (_s == null)", result.OutputCode);
        Assert.Contains("_s = Make();", result.OutputCode);
        Assert.Contains("return _s;", result.OutputCode);
        var methodBodyStart = result.OutputCode.IndexOf("Func<string> M()");
        var methodBody = result.OutputCode.Substring(methodBodyStart);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(methodBody, "if \\(_s == null\\)").Count);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_CoalesceAssign_In_Block_Bodied_Lambda_Hoists_Inside_Lambda()
    {
        var src = """
        using System;
        class C {
            string? _s;
            string Make() => "x";
            void Use(string s) {}
            Action M() {
                return () => { Use(_s ??= Make()); };
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("if (_s == null)", result.OutputCode);
        Assert.Contains("Use(_s);", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_Member_LValue_CoalesceAssign_In_Arrow_Property_Expands_And_Hoists_Guard()
    {
        // The expression-body expansion gives the member-lvalue ??= a real
        // statement slot: the guard writes through _box.V directly and the
        // getter returns the post-assignment value.
        var src = """
        class Box { public string? V { get; set; } }
        class C {
            Box _box = new Box();
            string? Name => _box.V ??= "default";
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Equal(0, result.Statistics.SkippedUnsupported);
        Assert.Contains("if (_box.V == null)", result.OutputCode);
        Assert.Contains("_box.V = \"default\";", result.OutputCode);
        Assert.Contains("return _box.V;", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_Member_LValue_CoalesceAssign_In_Field_Initializer_Stays_Residual_Not_Crash()
    {
        // A field initializer has NO host block at all (an expansion target
        // does not exist), so a side-effecting member lvalue stays residual
        // instead of throwing or emitting invalid code.
        var src = """
        class Box { public string? V { get; set; } }
        class C {
            static Box NewBox() => new Box();
            string? _name = NewBox().V ??= "default";
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.SkippedUnsupported);
        Assert.Contains("NewBox().V ??= \"default\"", result.OutputCode);
        // The untouched source must still compile and never throw.
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_Nested_Coalesce_On_Rhs_Of_CoalesceAssign_Composes()
    {
        var src = """
        class C {
            string? _a;
            void Use(string s) {}
            void M(string? b, string c) {
                Use(_a ??= (b ?? c));
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.NullCoalescingAssignRewritten);
        Assert.Equal(1, result.Statistics.NullCoalescingRewritten);
        Assert.Contains("if (_a == null)", result.OutputCode);
        Assert.Contains("Use(_a);", result.OutputCode);
        Assert.Contains("___t1 != null ? ___t1 : c", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_Nullable_Value_Type_Coalesce_True_Branch_Reads_Value()
    {
        // RegexCharClass.cs shape: 'cc._rangelist?.Count' is int? and the right
        // side is int; a bare temp in the true branch types the conditional as
        // int? (CS0266). The true branch must read ___tN.Value.
        var src = """
        using System.Collections.Generic;
        class RangeList { public List<int>? _rangelist; }
        class C {
            int Count(RangeList cc) {
                int ccRangeCount = cc._rangelist?.Count ?? 0;
                return ccRangeCount;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("___t1 != null ? ___t1.Value : 0", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_Nullable_Value_Type_Coalesce_With_Nullable_Right_Stays_Nullable()
    {
        // Both sides int?: the result stays int?; .Value in the true branch
        // still types the conditional correctly (int : int? => int?).
        var src = """
        class C {
            int? Pick(int? a, int? b) {
                return a ?? b;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("___t1.Value", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Regression_Nullable_Value_Type_Coalesce_In_Argument_Compiles()
    {
        var src = """
        using System.Collections.Generic;
        class C {
            void Take(int x) {}
            void M(List<int>? items) {
                Take(items?.Count ?? -1);
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("___t1.Value", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Nullable_Reference_Type_Coalesce_True_Branch_Has_No_Value_Property()
    {
        // Reference types never get the .Value unwrap.
        var src = """
        class C {
            string M(string? a, string b) {
                return a ?? b;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("___t1 != null ? ___t1 : b", result.OutputCode);
        Assert.DoesNotContain("___t1.Value", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    // ---- Expression-body expansion ('=>' members have no statement slot) ----
    //
    // The 2026-10-11 SDK conversion left 38 residual ??/??= sites because
    // every one of them lived in an expression-bodied member/accessor/lambda,
    // where a hoisted temp declaration or if-guard has nowhere to land. The
    // pass now expands ONLY the arrow bodies that contain a coalesce operator
    // into equivalent block bodies before the single-pass rewriter runs.

    [Fact]
    public void Arrow_Method_Value_Body_Expands_To_Return_Block()
    {
        // Utf8JsonWriter.WritePropertiesBytes shape: ?? throw inside an
        // argument of a void arrow method is covered separately; this is the
        // value-yielding form (WriteBase64String-style result is discarded by
        // the caller, but a value arrow member wraps in 'return').
        var src = """
        using System;
        class C {
            string? _p;
            string Get() => _p ?? throw new InvalidOperationException();
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("return (___t1 != null ? ___t1 : throw", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Void_Arrow_Method_Expands_To_Expression_Statement_Without_Return()
    {
        // Utf8JsonWriter.WritePropertiesBytes exact shape:
        // '=> WriteBase64String((name ?? throw ...).AsSpan(), bytes);'
        var src = """
        using System;
        class C {
            void WriteBase64String(string s, byte[] bytes) {}
            void Write(string? propertyName, byte[] bytes) =>
                WriteBase64String(propertyName ?? throw new ArgumentNullException(nameof(propertyName)), bytes);
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("var ___t1 = propertyName;", result.OutputCode);
        Assert.Contains("WriteBase64String(", result.OutputCode);
        // A void arrow body expands to an expression statement: the only
        // 'return' in the file belongs to AsSpan() invocation text nowhere;
        // assert no return keyword was emitted.
        Assert.DoesNotContain("return", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Arrow_Property_Nullable_Value_Type_Expands_With_Value_Unwrap()
    {
        // ArrayBuilder.Capacity shape: 'public int Capacity => _array?.Length ?? 0;'
        var src = """
        class C {
            int[]? _array;
            public int Capacity => _array?.Length ?? 0;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("get", result.OutputCode);
        Assert.Contains("___t1.Value", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Arrow_Property_Lazy_Static_Cache_CoalesceAssign_Expands()
    {
        // UTF8Encoding UTF8BomThrowing shape:
        // '=> s_utf8 ?? (s_utf8 = new UTF8Encoding(...));'
        var src = """
        using System.Text;
        class C {
            static UTF8Encoding? s_utf8;
            private static UTF8Encoding UTF8BomThrowing
                => s_utf8 ?? (s_utf8 = new UTF8Encoding(true, true));
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // Note: this is plain '??' whose RHS is a parenthesized assignment.
        Assert.Contains("var ___t1 = s_utf8;", result.OutputCode);
        Assert.Contains("(s_utf8 = new UTF8Encoding(true, true))", result.OutputCode);
        Assert.Contains("return (___t1 != null ? ___t1 : (s_utf8 = new UTF8Encoding(true, true)));",
            result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Arrow_Get_And_Set_Accessors_Both_Expand()
    {
        var src = """
        class C {
            string? _nspace;
            string? N {
                get => _nspace ?? "default";
                set => _nspace = value ?? throw new System.ArgumentNullException();
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("return (___t1 != null ? ___t1 : \"default\");", result.OutputCode);
        Assert.Contains("_nspace = (___t2 != null ? ___t2 : throw", result.OutputCode);
        Assert.DoesNotContain("=>", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Arrow_Constructor_Expands_To_Block()
    {
        // TestUtilities ctor shape: '=> _collection = collection ?? throw ...;'
        var src = """
        using System;
        using System.Collections.Generic;
        class C {
            IEnumerable<string> _collection;
            public C(IEnumerable<string>? collection)
                => _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("var ___t1 = collection;", result.OutputCode);
        Assert.Contains("_collection = (___t1 != null ? ___t1 : throw", result.OutputCode);
        Assert.DoesNotContain("=>", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Arrow_Indexer_Expands_To_Block_Getter()
    {
        var src = """
        class C {
            string?[] _a = new string?[1];
            string? this[int i] => _a[i] ?? "-1";
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("return (___t1 != null ? ___t1 : \"-1\");", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Arrow_Local_Function_Expands_To_Block()
    {
        var src = """
        class C {
            string M(string? a) {
                string Pick() => a ?? "d";
                return Pick();
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("string Pick()", result.OutputCode);
        Assert.Contains("return (___t1 != null ? ___t1 : \"d\");", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Simple_And_Parenthesized_Value_Lambdas_Expand_To_Block_Lambdas()
    {
        var src = """
        using System;
        class C {
            void M(string? s) {
                Func<string, string> a = x => x ?? s!;
                Func<string> b = () => s ?? "d";
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("x =>", result.OutputCode);
        Assert.Contains("() =>", result.OutputCode);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Void_Arrow_Lambda_Expands_Without_Return_And_Guard_Stays_Inside()
    {
        // 'Action f = () => Use(x ?? y);' — the delegate returns void; the
        // expanded statement lambda must contain an expression statement and
        // the hoisted temp, never a 'return'.
        var src = """
        using System;
        class C {
            void Use(string s) {}
            void M(string? name) {
                Action f = () => Use(name ?? "x");
                f();
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("() =>", result.OutputCode);
        Assert.Contains("var ___t1 = name;", result.OutputCode);
        Assert.Contains("Use((___t1 != null ? ___t1 : \"x\"));", result.OutputCode);
        var lambdaStart = result.OutputCode.IndexOf("() =>");
        Assert.DoesNotMatch(result.OutputCode.Substring(lambdaStart), @"return");
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }

    [Fact]
    public void Arrow_Method_Containing_Arrow_Lambda_Keeps_Guards_In_Respective_Scopes()
    {
        // Both outer arrow method and inner arrow lambda contain ??: the
        // method temp lands in the method block, the lambda temp inside the
        // lambda block; nothing crosses the lambda boundary.
        var src = """
        using System;
        class C {
            string? _a;
            void Use(string s) {}
            Action F(string? b) => () => Use((_a ?? "x") + (b ?? "y"));
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.ExpressionBodiesExpanded);
        Assert.DoesNotContain("??", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));

        // The method block must not hoist the lambda's temps: the method body
        // contains only the 'return () =>' (its own expression has no ??, so
        // not even a method-level temp is expected here — both ?? live inside
        // the lambda).
        var methodStart = result.OutputCode.IndexOf("Action F(");
        var lambdaStart = result.OutputCode.IndexOf("() =>", methodStart);
        var methodPrelude = result.OutputCode.Substring(methodStart, lambdaStart - methodStart);
        Assert.DoesNotContain("___t", methodPrelude);
    }

    [Fact]
    public void Arrow_Bodies_Without_Coalesce_Are_Not_Expanded()
    {
        var src = """
        class C {
            string? _clean;
            string Clean => _clean ?? "d";
            string AlsoClean() => Clean;
            string? Other() => null;
            void M(string? a, string b) {
                var x = a ?? b;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        // Only the one ?? (in Clean) triggers a single expansion; AlsoClean
        // and Other contain no coalesce and keep their arrow form.
        Assert.Equal(1, result.Statistics.ExpressionBodiesExpanded);
        Assert.Contains("string AlsoClean() => Clean;", result.OutputCode);
        Assert.Contains("string? Other() => null;", result.OutputCode);
    }

    [Fact]
    public void Field_Initializer_Coalesce_Stays_Residual_And_Does_Not_Expand()
    {
        // Field initializers have no block host at all: they must never be
        // expanded and the site remains a (strict-mode) residual instead of
        // producing broken code.
        var src = """
        class C {
            static string? Provider() => null;
            string? _field = Provider() ?? "default";
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ExpressionBodiesExpanded);
        Assert.Equal(1, result.Statistics.SkippedUnsupported);
        Assert.Equal(1, result.Statistics.ResidualCount);
        Assert.Contains("Provider() ?? \"default\"", result.OutputCode);
        Assert.Empty(OutputCompileErrors(result.OutputCode));
    }
}
