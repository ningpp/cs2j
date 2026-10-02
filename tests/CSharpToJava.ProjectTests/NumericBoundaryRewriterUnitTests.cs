using System.Text.RegularExpressions;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Unit tests for the numeric-boundary pass (C15): NB-1 (unchecked expression
/// stripping), NB-2 (unchecked block unwrapping), NB-3/4 (checked binary/unary
/// helpers over the four integer domains), NB-5 (compound assignments), NB-6
/// (constant materialization), NB-7 (checked narrowing conversions), NB-8
/// (postfix hoisting), NB-9/11 (non-integer shell stripping), NB-12 (error
/// rollback), NB-13 (residual gate + Q6 voucher exemption), project-wide
/// /checked, lambda/local-function boundaries and idempotency.
/// </summary>
public class NumericBoundaryRewriterUnitTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(
        string src,
        bool fullFrameworkReferences = false)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var references = new List<MetadataReference>();
        if (fullFrameworkReferences)
        {
            var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            if (!string.IsNullOrEmpty(trustedAssemblies))
            {
                foreach (var assembly in trustedAssemblies
                             .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (File.Exists(assembly))
                    {
                        references.Add(MetadataReference.CreateFromFile(assembly));
                    }
                }
            }
        }

        if (references.Count == 0)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        var compilation = CSharpCompilation.Create("numeric-boundary-test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    private static NumericBoundaryFileResult Run(
        string src,
        NumericBoundaryOptions? options = null,
        bool fullFrameworkReferences = false)
    {
        var (tree, model) = ParseWithSemantics(src, fullFrameworkReferences);
        return new NumericBoundaryPreprocessor().Rewrite(src, model, options);
    }

    /// <summary>Whitespace-insensitive containment (the formatter owns layout).</summary>
    private static string Compact(string text) => Regex.Replace(text, @"\s+", "");

    private const string HelperPrefix = "Cs2jUnsigned.UnsignedMath.";

    private static void AssertContainsHelper(NumericBoundaryFileResult result, string call)
        => Assert.Contains(Compact(HelperPrefix + call), Compact(result.OutputCode));

    private static NumericBoundaryOptions ProjectCheckedOptions(string projectDirectory)
        => new()
        {
            SourceFilePath = Path.Combine(projectDirectory, "Sample.cs"),
            CheckForOverflowUnderflowByProjectDirectory =
                new Dictionary<string, bool> { [Path.GetFullPath(projectDirectory)] = true },
        };

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

    private static void AssertNoCheckedOrUncheckedKeywords(NumericBoundaryFileResult result)
    {
        Assert.DoesNotContain("checked", result.OutputCode);
        Assert.DoesNotContain("unchecked", result.OutputCode);
        Assert.Equal(0, result.Statistics.ResidualCheckedNodes);
        Assert.Equal(0, result.Statistics.ResidualUncheckedNodes);
    }

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

    // ── NB-2/NB-3: checked blocks are explicitized, never retained ──

    [Fact]
    public void CheckedBlock_BecomesBareBlock_InnerUncheckedExprStripped()
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
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.Contains("(a + b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.UncheckedExpressionsStripped);
        Assert.DoesNotContain("AddChecked", result.OutputCode);
    }

    [Fact]
    public void CheckedBlock_AroundUncheckedBlock_AllKeywordNodesGone()
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
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.Contains("System.Console.Write(1)", result.OutputCode);
    }

    [Fact]
    public void CheckedBlock_BuiltInArithmetic_BecomesHelperAndBareBlock()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    checked { int r = a + b; return r; }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.Contains("AddCheckedInt32(a, b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.CheckedBinarySites);
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

    [Fact]
    public void ProjectChecked_ImplicitConstantOverflow_MaterializedToWrappedLiteral()
    {
        // /checked project: `byte b = 200 + 100;` must compile in the intermediate
        // tree even though the implicit conversion is checked by default.
        var src = """
            public class C
            {
                public byte M()
                {
                    byte b = 200 + 100;
                    return b;
                }
            }
            """;
        var result = Run(src, ProjectCheckedOptions(Path.GetTempPath()));

        Assert.True(result.Changed);
        // The folded 300 is represented as the in-range wrapped byte value 44
        // (the intermediate csproj no longer carries /checked, so unchecked wrap
        // semantics make the tree compile).
        Assert.Equal(1, result.Statistics.ConstantsMaterialized);
        Assert.DoesNotContain("200 + 100", result.OutputCode);
        Assert.Contains("(byte)44", result.OutputCode);
    }

    // ── NB-9/NB-11: non-integer checked shells ──

    [Fact]
    public void NB9_CheckedDecimalArithmetic_ShellStrippedOperationUntouched()
    {
        var src = "public class C { public decimal M(decimal a, decimal b) => checked(a + b); }";
        // Family isolation: this asserts the C15 non-integer shell strip only;
        // C34 (default on) would reduce the inner decimal site, so pin it off.
        var result = Run(src, new NumericBoundaryOptions { EnableDecimalOps = false });

        Assert.True(result.Changed);
        Assert.DoesNotContain("checked", result.OutputCode);
        Assert.Contains("(a + b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.CheckedNonIntegerShellsStripped);
    }

    [Fact]
    public void NB11_CheckedEnumConversion_ShellStripped_InnerIntegerArithmeticChecked()
    {
        var src = "enum U : int { A, B } public class C { public U M(int x, int y) => checked((U)(x + y)); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain("checked", result.OutputCode);
        // The enum cast itself stays bare; the nested integer add is checked.
        Assert.Contains($"(U)({HelperPrefix}AddCheckedInt32(x,y))", Compact(result.OutputCode));
        Assert.Equal(1, result.Statistics.CheckedNonIntegerShellsStripped);
        Assert.Equal(1, result.Statistics.CheckedBinarySites);
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

    // ── NB-3: checked binary arithmetic over the four domains ──

    [Fact]
    public void NB3_CheckedInt32Binary_AddSubtractMultiply_UseHelpers()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    int x;
                    int y;
                    int z;
                    checked
                    {
                        x = a + b;
                        y = a - b;
                        z = a * b;
                    }
                    return x + y + z;
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        AssertContainsHelper(result, "AddCheckedInt32(a,b)");
        AssertContainsHelper(result, "SubtractCheckedInt32(a,b)");
        AssertContainsHelper(result, "MultiplyCheckedInt32(a,b)");
        // The return expression is outside the checked block: plain operators stay.
        Assert.Contains("return x + y + z;", result.OutputCode);
        Assert.Equal(3, result.Statistics.CheckedBinarySites);
        Assert.Equal(3, result.Statistics.HelpersByDomain["Int32"]);
    }

    [Fact]
    public void NB3_CheckedInt64Binary_UsesInt64Helpers()
    {
        var src = "public class C { public long M(long a, long b) => checked(a + b - a * b); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "AddCheckedInt64");
        AssertContainsHelper(result, "SubtractCheckedInt64");
        AssertContainsHelper(result, "MultiplyCheckedInt64");
    }

    [Fact]
    public void NB3_CheckedUInt32Binary_UsesRealUIntSignatures()
    {
        var src = "public class C { public uint M(uint a, uint b) => checked(a + b - a * b); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "AddCheckedUInt32(a,b)");
        AssertContainsHelper(result, "SubtractCheckedUInt32");
        AssertContainsHelper(result, "MultiplyCheckedUInt32(a,b)");
    }

    [Fact]
    public void NB3_CheckedUInt64Binary_UsesUInt64Helpers()
    {
        var src = "public class C { public ulong M(ulong a, ulong b) => checked(a + b - a * b); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "AddCheckedUInt64(a,b)");
        AssertContainsHelper(result, "SubtractCheckedUInt64");
        AssertContainsHelper(result, "MultiplyCheckedUInt64(a,b)");
    }

    [Fact]
    public void NB3_DivisionAndRemainder_ShellStripped_NoHelper()
    {
        var src = """
            public class C
            {
                public int M(int x, int y)
                {
                    checked { int d = x / y; int m = x % y; return d + m; }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.Contains("x / y", result.OutputCode);
        Assert.Contains("x % y", result.OutputCode);
        Assert.DoesNotContain("DivideChecked", result.OutputCode);
        Assert.DoesNotContain("RemainderChecked", result.OutputCode);
        // The outer `d + m` is inside the checked block: exactly one add helper.
        Assert.Equal(1, result.Statistics.CheckedBinarySites);
    }

    [Fact]
    public void NB3_DivideByZeroLiteral_ShellStripped_ExpressionKept()
    {
        var src = "public class C { public int M(int x) => checked(x / 0); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.Contains("x / 0", result.OutputCode);
    }

    [Fact]
    public void NB3_BitwiseOperators_ShellStripped_NoHelper()
    {
        var src = "public class C { public int M(int a, int b) => checked(a & b | a ^ b); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.DoesNotContain("Checked", result.OutputCode);
    }

    // ── NB-4: checked unary minus ──

    [Fact]
    public void NB4_CheckedInt32UnaryMinus_UsesNegateHelper()
    {
        var src = "public class C { public int M(int a) => checked(-a); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "NegateCheckedInt32(a)");
        Assert.Equal(1, result.Statistics.CheckedUnarySites);
    }

    [Fact]
    public void NB4_CheckedInt64UnaryMinus_UsesNegateHelper()
    {
        var src = "public class C { public long M(long a) => checked(-a); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "NegateCheckedInt64(a)");
    }

    [Fact]
    public void NB4_NegatedUInt_PromotesToLong_NoHelper()
    {
        // Probe: -uint naturally promotes to long and cannot overflow domain helpers.
        var src = "public class C { public long M(uint a) => checked(-a); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.DoesNotContain("NegateChecked", result.OutputCode);
        Assert.Contains("=>-a;", Compact(result.OutputCode));
    }

    // ── NB-5: checked compound assignments ──

    [Fact]
    public void NB5_CheckedCompound_AddSubtractMultiply_AreHelperAssignments()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    checked
                    {
                        a += b;
                        a -= b;
                        a *= b;
                        return a;
                    }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        var compact = Compact(result.OutputCode);
        Assert.Contains($"a={HelperPrefix}AddCheckedInt32(a,b)", compact);
        Assert.Contains($"a={HelperPrefix}SubtractCheckedInt32(a,b)", compact);
        Assert.Contains($"a={HelperPrefix}MultiplyCheckedInt32(a,b)", compact);
        Assert.Equal(3, result.Statistics.CompoundAssignments);
    }

    [Fact]
    public void NB5_CheckedCompound_DivideAndBitwise_StayAsIs()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    checked
                    {
                        a /= b;
                        a %= b;
                        a &= b;
                        return a;
                    }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.Contains("a /= b", result.OutputCode);
        Assert.Contains("a %= b", result.OutputCode);
        Assert.Contains("a &= b", result.OutputCode);
        Assert.Equal(0, result.Statistics.CompoundAssignments);
    }

    [Fact]
    public void NB5_NarrowTargetCompound_AddAndNarrowConversion_BothChecked()
    {
        // Probe: `byte b = 200; checked { b += (byte)100; }` is legal C# and
        // throws OverflowException: the arithmetic AND the out-conversion check.
        var src = """
            public class C
            {
                public byte M()
                {
                    byte b = 200;
                    checked { b += (byte)100; }
                    return b;
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // The constant (byte)100 is materialized to 100 inside the arithmetic.
        Assert.Contains(
            $"b={HelperPrefix}ToByteCheckedInt32({HelperPrefix}AddCheckedInt32(b,100))",
            compact);
        Assert.Equal(1, result.Statistics.CompoundAssignments);
        Assert.Equal(1, result.Statistics.CheckedConversionSites);
    }

    // ── NB-7: checked narrowing conversions ──

    [Theory]
    [InlineData("(byte)a", "ToByteCheckedInt32(a)")]
    [InlineData("(sbyte)a", "ToSByteCheckedInt32(a)")]
    [InlineData("(short)a", "ToInt16CheckedInt32(a)")]
    [InlineData("(ushort)a", "ToUInt16CheckedInt32(a)")]
    [InlineData("(char)a", "ToCharCheckedInt32(a)")]
    [InlineData("(uint)a", "ToUInt32CheckedInt32(a)")]
    public void NB7_CheckedNarrowing_FromInt32_UsesConversionHelper(string cast, string helper)
    {
        var src = $"public class C {{ public object M(int a) => checked({cast}); }}";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, helper);
        Assert.Equal(1, result.Statistics.CheckedConversionSites);
    }

    [Fact]
    public void NB7_CheckedNarrowing_FromUInt32_UsesUInt32Helper()
    {
        var src = "public class C { public int M(uint a) => checked((int)a); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "ToInt32CheckedUInt32(a)");
    }

    [Fact]
    public void NB7_CheckedNarrowing_FromInt64_UsesInt64Helpers()
    {
        var src = """
            public class C
            {
                public void M(long l)
                {
                    checked
                    {
                        int a = (int)l;
                        uint b = (uint)l;
                        byte c = (byte)l;
                        ulong d = (ulong)l;
                    }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "ToInt32CheckedInt64(l)");
        AssertContainsHelper(result, "ToUInt32CheckedInt64(l)");
        AssertContainsHelper(result, "ToByteCheckedInt64(l)");
        AssertContainsHelper(result, "ToUInt64CheckedInt64(l)");
    }

    [Fact]
    public void NB7_CheckedNarrowing_FromUInt64_UsesUInt64Helpers()
    {
        var src = """
            public class C
            {
                public void M(ulong u)
                {
                    checked
                    {
                        int a = (int)u;
                        uint b = (uint)u;
                        long c = (long)u;
                        byte d = (byte)u;
                    }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "ToInt32CheckedUInt64(u)");
        AssertContainsHelper(result, "ToUInt32CheckedUInt64(u)");
        AssertContainsHelper(result, "ToInt64CheckedUInt64(u)");
        AssertContainsHelper(result, "ToByteCheckedUInt64(u)");
    }

    [Fact]
    public void NB7_WideningConversion_BareCastKept_NoHelper()
    {
        var src = "public class C { public long M(int x) => checked((long)x); }";
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.Contains("(long)x", result.OutputCode);
        Assert.Equal(0, result.Statistics.CheckedConversionSites);
    }

    // ── NB-8: ++/── prefix and postfix ──

    [Fact]
    public void NB8_PrefixIncrement_BecomesHelperAssignment()
    {
        var src = "public class C { public int M(int a) => checked(++a); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains($"(a={HelperPrefix}AddCheckedInt32(a,1))", Compact(result.OutputCode));
        Assert.Equal(1, result.Statistics.CheckedIncrementSites);
    }

    [Fact]
    public void NB8_PrefixDecrement_UsesSubtractHelper()
    {
        var src = "public class C { public int M(int a) => checked(--a); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.Contains($"(a={HelperPrefix}SubtractCheckedInt32(a,1))", Compact(result.OutputCode));
    }

    [Fact]
    public void NB8_StandalonePostfix_BecomesDirectAssignment()
    {
        var src = """
            public class C
            {
                public void M(int a)
                {
                    checked { a++; }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // The checked statement block survives as a bare block after shell stripping.
        Assert.Contains($"{{a={HelperPrefix}AddCheckedInt32(a,1);}}", compact);
        Assert.DoesNotContain(NumericBoundarySyntaxFacts.TempNamePrefix, result.OutputCode);
    }

    [Fact]
    public void NB8_ForIncrementorPostfix_StaysInForIncrementors()
    {
        var src = """
            public class C
            {
                public int M()
                {
                    int sum = 0;
                    checked
                    {
                        for (int i = 0; i < 10; i++) { sum += i; }
                    }
                    return sum;
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        var root = CSharpSyntaxTree.ParseText(result.OutputCode).GetRoot();
        var forStatement = root.DescendantNodes().OfType<ForStatementSyntax>().Single();
        Assert.Single(forStatement.Incrementors);
        Assert.Contains("AddCheckedInt32(i, 1)", forStatement.Incrementors[0].ToString());
        Assert.DoesNotContain(NumericBoundarySyntaxFacts.TempNamePrefix, result.OutputCode);
    }

    [Fact]
    public void NB8_ValueConsumedPostfix_HoistsTempBeforeStatement_OldValueUsedOnce()
    {
        var src = """
            public class C
            {
                public int M(int a)
                {
                    checked { return a++; }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        // var __cs2j_nb_0 = a; a = AddCheckedInt32(a, 1); return __cs2j_nb_0;
        Assert.Contains("var__cs2j_nb_0=a;", compact);
        Assert.Contains($"a={HelperPrefix}AddCheckedInt32(a,1);", compact);
        Assert.Contains("return__cs2j_nb_0;", compact);

        // Hoist expands inside the (now bare) former checked block.
        var method = ParseMethod(result.OutputCode);
        var body = (BlockSyntax)method.Body!;
        var inner = Assert.IsType<BlockSyntax>(Assert.Single(body.Statements));
        Assert.Equal(3, inner.Statements.Count);
    }

    [Fact]
    public void NB8_TwoPostfixSitesInOneStatement_InterleaveInEvaluationOrder()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    checked
                    {
                        int r = a++ + b++;
                        return r;
                    }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains("var__cs2j_nb_0=a;", compact);
        Assert.Contains("var__cs2j_nb_1=b;", compact);
        Assert.Contains($"a={HelperPrefix}AddCheckedInt32(a,1)", compact);
        Assert.Contains($"b={HelperPrefix}AddCheckedInt32(b,1)", compact);
        // The outer add is inside the checked block, so it is also a helper call.
        AssertContainsHelper(result, "AddCheckedInt32(__cs2j_nb_0,__cs2j_nb_1)");
    }

    [Fact]
    public void NB8_EnumIncrement_UsesUnderlyingDomainAndCastBack()
    {
        var src = """
            public enum E { A, B, C }
            public class C
            {
                public void M(E e)
                {
                    checked { e++; }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains($"e=(global::E){HelperPrefix}AddCheckedInt32((int)e,1)", compact);
    }

    [Fact]
    public void NB8_ByteIncrement_ArithmeticAndNarrowConversionBothChecked()
    {
        var src = """
            public class C
            {
                public byte M()
                {
                    byte b = 200;
                    checked { ++b; }
                    return b;
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        var compact = Compact(result.OutputCode);
        Assert.Contains(
            $"b={HelperPrefix}ToByteCheckedInt32({HelperPrefix}AddCheckedInt32(b,1))",
            compact);
    }

    // ── error codes (NB-12: every error rolls the whole file back) ──

    [Fact]
    public void CS2J5320_NIntArithmetic_ErrorRollsFileBack()
    {
        var src = "public class C { public nint M(nint a) => checked(a + 1); }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryUnsupportedType);
        Assert.True(result.Statistics.ErrorsByReason
            .ContainsKey(PreprocessDiagnosticCodes.NumericBoundaryUnsupportedType));
    }

    [Fact]
    public void CS2J5321_FloatingToIntegerConversion_Error()
    {
        var src = "public class C { public int M(double d) => checked((int)d); }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryFloatingToInteger);
    }

    [Fact]
    public void CS2J5322_DynamicArithmetic_Error()
    {
        var src = """
            public class C
            {
                public int M(dynamic d) => checked(d + 1);
            }
            """;
        var result = Run(src, fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryDynamic);
    }

    [Fact]
    public void CS2J5323_ExpressionTreeCheckedSite_Error()
    {
        var src = """
            using System;
            using System.Linq.Expressions;
            public class C
            {
                public Expression<Func<int, int, int>> M() => (a, b) => checked(a + b);
            }
            """;
        var result = Run(src, fullFrameworkReferences: true);

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryExpressionTree);
    }

    // C17 M1 (decision 17): complex targets in checked regions are now expanded
    // by the increment-targets rule (Order=10) BEFORE this C15 sweep runs, so
    // the postfix never reaches RewriteIncrement as a complex target. The bare
    // int arithmetic of the expanded assignment is still helperized here.
    [Fact]
    public void CS2J5324_PropertyPostfix_C17ExpandsFirst_C15HelperizesArithmetic()
    {
        var src = """
            public class C
            {
                public int P { get; set; }
                public void M() { checked { P++; } }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
        // The postfix on a standalone statement needs no temps; M1 rewrites it
        // as an assignment and the C15 sweep helperizes the bare int arithmetic.
        Assert.DoesNotContain("P++", result.OutputCode.Replace(" ", "").Replace("\r", "").Replace("\n", ""));
        AssertContainsHelper(result, "AddCheckedInt32");
    }

    [Fact]
    public void CS2J5324_FieldInitializerPostfix_InCheckedProject_Error()
    {
        var src = """
            public class C
            {
                private static int _counter;
                private int _value = _counter++;
            }
            """;
        var result = Run(src, ProjectCheckedOptions(Path.GetTempPath()));

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
    }

    [Fact]
    public void CS2J5325_NullableLiftedArithmetic_C22GuardFirst_CheckedHelperInLiveBranch()
    {
        // C22 M2 (decision 18): with the nullable-lift rule enabled (default),
        // the lifted site is explicitized BEFORE this sweep; the checked sweep
        // then inserts AddCheckedInt32 inside the guard's live branch instead
        // of rejecting the site with CS2J5325.
        var src = "public class C { public int? M(int? a, int? b) => checked(a + b); }";
        var result = Run(src);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryNullableLifted);
        var compact = Compact(result.OutputCode);
        Assert.Contains(
            "a.HasValue&&b.HasValue?(int?)(Cs2jUnsigned.UnsignedMath.AddCheckedInt32(a.Value,b.Value)):(int?)null",
            compact);
        // The null branch carries no helper call:
        Assert.Equal(1, compact.Split("UnsignedMath").Length - 1);
    }

    [Fact]
    public void CS2J5325_NullableLiftedArithmetic_Error_WhenNullableRuleDisabled()
    {
        var src = "public class C { public int? M(int? a, int? b) => checked(a + b); }";
        var result = Run(src, new NumericBoundaryOptions { EnableNullable = false });

        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryNullableLifted);
    }

    // ── lambda vs local-function checked-context boundaries ──

    [Fact]
    public void Lambda_DoesNotInheritCheckedBlock_NoHelperInside()
    {
        var src = """
            using System;
            public class C
            {
                public int M(int a, int b)
                {
                    checked
                    {
                        Func<int> f = () => a + b;
                        return f();
                    }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.DoesNotContain("AddChecked", result.OutputCode);
        Assert.Contains("a + b", result.OutputCode);
    }

    [Fact]
    public void LocalFunction_InheritsCheckedBlock_HelperInside()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    checked
                    {
                        int Local() => a + b;
                        return Local();
                    }
                }
            }
            """;
        var result = Run(src);

        Assert.True(result.Changed);
        AssertContainsHelper(result, "AddCheckedInt32(a,b)");
    }

    // ── project-wide /checked ──

    [Fact]
    public void ProjectChecked_KeywordFreeArithmetic_IsRewrittenWithoutKeywords()
    {
        var src = "public class C { public int M(int a, int b) => a + b; }";
        var result = Run(src, ProjectCheckedOptions(Path.GetTempPath()));

        Assert.True(result.Changed);
        Assert.Contains("AddCheckedInt32(a, b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.CheckedBinarySites);
    }

    [Fact]
    public void ProjectChecked_ExplicitUncheckedBlock_Wins_PlainArithmeticNoHelper()
    {
        var src = """
            public class C
            {
                public int M(int a, int b)
                {
                    unchecked { return a + b; }
                }
            }
            """;
        var result = Run(src, ProjectCheckedOptions(Path.GetTempPath()));

        Assert.True(result.Changed);
        AssertNoCheckedOrUncheckedKeywords(result);
        Assert.DoesNotContain("AddChecked", result.OutputCode);
        Assert.Contains("a + b", result.OutputCode);
    }

    // ── NB-13 / Q6: user-defined checked operator vouchers ──

    [Fact]
    public void Q6_CheckedUserOpVoucher_WithoutMemberDesugar_IsCS2J5327Error()
    {
        var src = WideUserOps + " public class C { public Wide M(Wide a, Wide b) => checked(a + b); }";
        var result = Run(src);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.CheckedUserOpShellsDeferred);
        Assert.Contains(result.Diagnostics, d =>
            d.Severity == NumericBoundarySeverity.Error
            && d.Code == PreprocessDiagnosticCodes.NumericBoundaryResidualNode);
        Assert.Equal(1, result.Statistics.ResidualCheckedNodes);
    }

    [Fact]
    public void Q6_CheckedUserOpVoucher_WithMemberDesugarCoRun_IsExempt()
    {
        var src = WideUserOps + " public class C { public Wide M(Wide a, Wide b) => checked(a + b); }";
        var options = new NumericBoundaryOptions
        {
            EnabledPassIds = new HashSet<string>(StringComparer.Ordinal)
            {
                "numeric-boundary",
                NumericBoundaryOptions.MemberDesugarPassId,
            },
        };
        var result = Run(src, options);

        // Shell is intentionally preserved for member-desugar; the file itself
        // needs no other rewrite, so it copies through with no errors.
        Assert.False(result.Changed);
        Assert.Contains("checked(a + b)", result.OutputCode);
        Assert.Equal(1, result.Statistics.CheckedUserOpShellsDeferred);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Equal(1, result.Statistics.ResidualCheckedNodes);
    }

    [Fact]
    public void Q6_UncheckedUserOpCall_ShellStripped()
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

    [Fact]
    public void HelperizedOutput_IsIdempotent_SecondPassByteIdentical()
    {
        var src = "public class C { public int M(int a, int b) => checked(a + b); }";
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
