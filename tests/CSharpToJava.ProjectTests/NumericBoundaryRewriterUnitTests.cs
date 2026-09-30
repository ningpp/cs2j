using System.Text.RegularExpressions;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// M1 unit tests for the numeric-boundary pass: NB-1 (unchecked expression
/// stripping), NB-2 (unchecked block unwrapping), NB-6 (constant
/// materialization), NB-9/NB-11 (non-integer checked shell stripping),
/// decision 4 (op_Checked* user-operator exemption) and NB-13-adjacent
/// retention boundaries (built-in integer checked sites, M3 buckets).
/// </summary>
public class NumericBoundaryRewriterUnitTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("numeric-boundary-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    private static NumericBoundaryFileResult Run(string src)
    {
        var (tree, model) = ParseWithSemantics(src);
        return new NumericBoundaryPreprocessor().Rewrite(src, model);
    }

    /// <summary>Whitespace-insensitive containment (the formatter owns layout).</summary>
    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");

    private const string WideUserOps = """
        public readonly struct Wide
        {
            private readonly int _v;
            public Wide(int v) { _v = v; }
            public static Wide operator +(Wide a, Wide b) => new Wide(a._v + b._v);
            public static Wide operator checked +(Wide a, Wide b) => new Wide(a._v + b._v);
            public int V => _v;
        }
        """;

    // ── NB-1: unchecked expression shells ──

    [Fact]
    public void NB1_UncheckedBinaryExpression_ShellStripped_InnerParenthesized()
    {
        var src = "public class C { public int M(int a, int b) { return unchecked(a + b); } }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("return (a + b);", result.OutputCode);
        Assert.DoesNotContain("unchecked", result.OutputCode);
        Assert.Equal(1, result.Statistics.UncheckedExpressionsStripped);
        Assert.Equal(1, result.Statistics.FilesTransformed);
    }

    [Fact]
    public void NB1_UnsignedHelperCoexistence_UncheckedCastShellsStripped()
    {
        var src = "public class C { public int M(uint a, uint b) => " +
                  "Cs2jUnsigned.UnsignedMath.AddUInt32(unchecked((int)a), unchecked((int)b)); }";
        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("AddUInt32((int)a,(int)b)", compact);
        Assert.DoesNotContain("unchecked", result.OutputCode);
        Assert.Equal(2, result.Statistics.UncheckedExpressionsStripped);
    }

    // ── NB-2: unchecked blocks ──

    [Fact]
    public void NB2_UncheckedBlock_InStatementList_StatementsSpliced()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    int r;
                    unchecked { r = a + b; r = r + 1; }
                    return r;
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("unchecked", result.OutputCode);

        var method = ParseMethod(result.OutputCode);
        // Method body is the only remaining block: the unchecked block was spliced.
        var blocks = method.DescendantNodes().OfType<BlockSyntax>().ToList();
        Assert.Single(blocks);
        Assert.Equal(4, blocks[0].Statements.Count);
        Assert.Equal(1, result.Statistics.UncheckedBlocksUnwrapped);
    }

    [Fact]
    public void NB2_UncheckedBlock_WithDirectLocalDeclaration_KeepsBraces()
    {
        var src = """
            public class C
            {
                public void M(int a)
                {
                    unchecked { int r = a + 1; System.Console.Write(r); }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("unchecked", result.OutputCode);

        var method = ParseMethod(result.OutputCode);
        var blocks = method.DescendantNodes().OfType<BlockSyntax>().ToList();
        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks[1].Statements, s => s is LocalDeclarationStatementSyntax);
    }

    [Fact]
    public void NB2_UncheckedBlock_AsEmbeddedStatement_KeepsBraces()
    {
        var src = """
            public class C
            {
                public void M(int a, int b)
                {
                    if (a > 0) unchecked { a = a + b; }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("unchecked", result.OutputCode);

        var root = CSharpSyntaxTree.ParseText(result.OutputCode).GetRoot();
        var ifStatement = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.IsType<BlockSyntax>(ifStatement.Statement);
    }

    [Fact]
    public void Nested_CheckedBlockKept_InnerUncheckedExprStripped()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    checked { int r = unchecked(a + b); return r; }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("checked", result.OutputCode);
        Assert.DoesNotContain("unchecked", result.OutputCode);
        Assert.Contains("(a + b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.UncheckedExpressionsStripped);
        Assert.Equal(1, result.Statistics.ResidualCheckedNodes);
    }

    [Fact]
    public void Nested_CheckedBlockAroundUncheckedBlock_InnerSplicedOuterKept()
    {
        var src = """
            public class C
            {
                public void M()
                {
                    checked { unchecked { System.Console.Write(1); } }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("checked", result.OutputCode);
        Assert.DoesNotContain("unchecked", result.OutputCode);

        var method = ParseMethod(result.OutputCode);
        // Method body + retained checked block.
        Assert.Equal(2, method.DescendantNodes().OfType<BlockSyntax>().Count());
    }

    // ── NB-6: constant materialization ──

    [Fact]
    public void NB6_UncheckedUIntToInt_ConstantFoldedToSignedLiteral()
    {
        var src = "public class C { public int M() => unchecked((int)4294967295u); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("=> -1;", result.OutputCode);
        Assert.DoesNotContain("unchecked", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConstantsMaterialized);
    }

    [Fact]
    public void NB6_UncheckedByteOverflow_ConstantFoldedToCastZero()
    {
        var src = "public class C { public byte M() => unchecked((byte)256); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("(byte)0", result.OutputCode);
    }

    [Fact]
    public void NB6_CheckedNonOverflowConstant_MaterializedAndShellRemoved()
    {
        var src = "public class C { public int M() => checked(1 + 2); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("=> 3;", result.OutputCode);
        Assert.DoesNotContain("checked", result.OutputCode);
        Assert.Equal(1, result.Statistics.ConstantsMaterialized);
    }

    [Fact]
    public void NB6_ULongMaxConstant_KeptWithUlSuffix()
    {
        var src = "public class C { public ulong M() => unchecked(18446744073709551615UL); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains("18446744073709551615UL", result.OutputCode);
    }

    // ── NB-9/NB-11: non-integer checked shells ──

    [Fact]
    public void NB9_CheckedDecimalArithmetic_ShellStrippedOperationUntouched()
    {
        var src = "public class C { public decimal M(decimal a, decimal b) => checked(a + b); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("checked", result.OutputCode);
        Assert.Contains("(a + b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.CheckedNonIntegerShellsStripped);
    }

    [Fact]
    public void NB11_CheckedEnumConversion_ShellStripped()
    {
        var src = "enum U : int { A, B } public class C { public U M(int x, int y) => checked((U)(x + y)); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("checked", result.OutputCode);
        Assert.Contains("(U)(x+y)", Compact(result.OutputCode));
        Assert.Equal(1, result.Statistics.CheckedNonIntegerShellsStripped);
    }

    [Fact]
    public void PlainIntegerArithmetic_NoKeywords_FileByteIdentical()
    {
        var src = "public class C { public int M(int a, int b) => a + b; }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void CleanFile_WithBom_ReturnedByteIdentical()
    {
        var src = "\uFEFFpublic class C { public int M() => 1; }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    // ── M1 retention boundary ──

    [Fact]
    public void M1_CheckedBuiltInIntegerBinary_ShellKeptAndNoHelpers()
    {
        var src = "public class C { public int M(int a, int b) => checked(a + b); }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains("checked(a + b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.ResidualCheckedNodes);
        Assert.Empty(result.Statistics.HelpersByDomain);
        Assert.Equal(1, result.Statistics.FilesDirtyNoRewrite);
    }

    [Fact]
    public void M1_CheckedIntegerDivisionByZero_ShellKept()
    {
        var src = "public class C { public int M(int x) => checked(x / 0); }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains("checked(x / 0)", result.OutputCode);
    }

    [Fact]
    public void M1_CheckedCompoundAssignment_ShellKeptForM3()
    {
        var src = "public class C { public int M(int a, int b) => checked(a += b); }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains("checked(a += b)", result.OutputCode);
    }

    // ── Decision 4: user-defined checked operators coexist with member-desugar ──

    [Fact]
    public void Decision4_CheckedUserOpVariant_ShellKept_DeferredVoucherCounted()
    {
        var src = WideUserOps + " public class C { public Wide M(Wide a, Wide b) => checked(a + b); }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Contains("checked(a + b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.CheckedUserOpShellsDeferred);
        // User-operator shells are exempt: no NB-13 residual error even in strict mode.
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == PreprocessDiagnosticCodes.NumericBoundaryResidualNode);
    }

    [Fact]
    public void Decision4_UncheckedUserOpCall_ShellStripped()
    {
        var src = WideUserOps + " public class C { public Wide M(Wide a, Wide b) => unchecked(a + b); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("unchecked", result.OutputCode);
        Assert.Contains("(a + b)", result.OutputCode);
    }

    // ── Idempotency ──

    [Fact]
    public void Rewrite_IsIdempotent_SecondPassZeroDiff()
    {
        var src = """
            public class C
            {
                public int M(uint a, uint b, int x)
                {
                    unchecked { x = x + 1; }
                    return unchecked((int)a) + unchecked((int)b) + x;
                }
            }
            """;
        var first = Run(src);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    private static BaseMethodDeclarationSyntax ParseMethod(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().First();
    }
}
