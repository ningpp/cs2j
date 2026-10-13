using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C17 statement-linearization lanes that eradicate the field CS2J5339
/// residuals (Uri.cs:2471 / UriExt.cs:504 while-condition postfixes,
/// UriHelper.cs same-anchor double postfixes, Compiler.cs:804 enum
/// for-incrementor, XsdDateTime/Infer/XPath* zero-statement prefixes):
/// <list type="bullet">
/// <item>while-condition postfix → <c>while(true){ plans; if(!(C'))break; body }</c>;</item>
/// <item>discarded for-incrementor → incrementor removed, write appended at body end;</item>
/// <item>multiple postfixes in one anchor → source-order read/write materialization;</item>
/// <item>statement-free prefix → inline <c>(target = NEW)</c> in any expression slot.</item>
/// Every behavioral case is verified by emitting BOTH the original and the
/// rewritten tree and comparing their actual runtime output.
/// </list>
/// </summary>
public class IncrementLinearizationTests
{
    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static (string Output, List<NumericBoundaryDiagnostic> Diagnostics, bool Changed) RunRule(string src)
    {
        var tree = (SyntaxTree)CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "inc-lin-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);

        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0,
            "test input failed to compile:\n" + string.Join("\n", compileErrors));

        var diagnostics = new List<NumericBoundaryDiagnostic>();
        var context = new NumericBoundaryRuleContext(
            (CompilationUnitSyntax)tree.GetRoot(),
            model,
            projectDefaultChecked: false,
            new NumericBoundaryOptions(),
            new NumericBoundaryStatistics(),
            diagnostics);
        var result = new IncrementTargetRule().Execute(context);
        return (result.Root.ToFullString(), diagnostics, result.MadeChanges);
    }

    private static string Invoke(string source)
    {
        var compilation = CSharpCompilation.Create(
            "inc-lin-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var demo = asm.GetType("Demo")!;
        return (string)demo.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
    }

    private static string Compact(string code) =>
        new(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private const string WhileConditionSource = """
        using System.Text;
        public static class Demo
        {
            static string Scan(char[] a)
            {
                ushort idx = 0;
                var sb = new StringBuilder();
                while (a[idx++] != '|')
                {
                    if (idx == 2) { sb.Append("C"); continue; }
                    if (idx == 4) break;
                    for (int k = 0; k < 2; k++)
                    {
                        if (k == 0) continue;
                        sb.Append(idx);
                    }
                }
                sb.Append('/').Append(idx);
                return sb.ToString();
            }

            static int FindPipe(char[] a)
            {
                ushort j = 0;
                while (a[j++] != '|') ;
                return j;
            }

            public static string Run()
            {
                var sb = new StringBuilder();
                sb.Append(Scan(new[] { 'a', 'b', 'c', 'd', '|', 'z' })).Append(';');
                sb.Append(FindPipe(new[] { 'x', '|', 'y' }));
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void While_Condition_Postfix_Linearizes_And_Preserves_Continue_Break_EmptyBody()
    {
        var expected = Invoke(WhileConditionSource);
        var (output, diagnostics, changed) = RunRule(WhileConditionSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        Assert.Contains("while(true)", compact);
        Assert.Contains("__cs2j_inc_o", compact);
        Assert.Contains("if(!(", compact);
        // Both postfix sites became old-value reads.
        Assert.DoesNotContain("idx++", compact);
        Assert.DoesNotContain("j++", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("1C3/4;2", actual);
    }

    [Theory]
    [InlineData("""
        public static class Demo
        {
            public static string Run()
            {
                char[] a = { 'a', '|' };
                ushort idx = 0;
                bool ready = true;
                while (ready && a[idx++] != '|') { }
                return idx.ToString();
            }
        }
        """)]
    [InlineData("""
        public static class Demo
        {
            public static string Run()
            {
                char[] a = { 'a', '|' };
                char[] b = { 'x' };
                ushort idx = 0;
                bool ready = true;
                while (ready ? a[idx++] != '|' : b[0] != '|') { }
                return idx.ToString();
            }
        }
        """)]
    public void While_Condition_Postfix_On_Blocked_Path_Still_Reports_CS2J5339(string src)
    {
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    private const string ForIncrementorSource = """
        using System.Text;
        enum Kind { A, B, C, D }
        public static class Demo
        {
            public static string Run()
            {
                var sb = new StringBuilder();
                for (Kind k = Kind.A; k < Kind.D; k++)
                    sb.Append((int)k);
                sb.Append(';');
                for (Kind k = Kind.A; k < Kind.D; k++)
                {
                    sb.Append((int)k);
                    if (k == Kind.B) sb.Append('!');
                }
                return sb.ToString();
            }
        }
        """;

    [Fact]
    public void For_Incrementor_Enum_Moves_Write_To_Body_End()
    {
        var expected = Invoke(ForIncrementorSource);
        var (output, diagnostics, changed) = RunRule(ForIncrementorSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        // The incrementor list is empty; the enum write-back is a body-end
        // statement (embedded body got wrapped in a block).
        Assert.Contains("for(Kindk=Kind.A;k<Kind.D;)", compact);
        Assert.Contains("k=(global::Kind)(int)((int)k+1)", compact);
        Assert.DoesNotContain("k++", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("012;01!2", actual);
    }

    [Fact]
    public void For_Incrementor_With_Direct_Continue_Reports_CS2J5339()
    {
        const string src = """
            using System.Text;
            enum Kind { A, B, C, D }
            public static class Demo
            {
                public static string Run()
                {
                    var sb = new StringBuilder();
                    for (Kind k = Kind.A; k < Kind.D; k++)
                    {
                        if (k == Kind.B) continue;
                        sb.Append((int)k);
                    }
                    return sb.ToString();
                }
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    [Fact]
    public void For_Incrementor_With_Effectful_Sibling_Reports_CS2J5339()
    {
        const string src = """
            using System.Text;
            enum Kind { A, B, C, D }
            public static class Demo
            {
                public static string Run()
                {
                    var sb = new StringBuilder();
                    int j = 0;
                    for (Kind k = Kind.A; k < Kind.D; k++, j++)
                    {
                        sb.Append((int)k).Append(j);
                    }
                    return sb.ToString();
                }
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    private const string SameAnchorDoublePostfixSource = """
        public sealed class Box
        {
            public int Value { get; set; }
        }
        public static class Demo
        {
            static string Copy(char[] src)
            {
                var dest = new char[4];
                var box = new Box();
                ushort srcPos = 0;
                while (srcPos < 3)
                {
                    dest[box.Value++] = src[srcPos++];
                }
                return new string(dest, 0, 3) + box.Value + srcPos;
            }

            public static string Run() => Copy(new[] { 'a', 'b', 'c' });
        }
        """;

    [Fact]
    public void Same_Anchor_Double_Postfix_Materializes_In_Source_Order()
    {
        var expected = Invoke(SameAnchorDoublePostfixSource);
        var (output, diagnostics, changed) = RunRule(SameAnchorDoublePostfixSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        Assert.Contains("dest[__cs2j_inc_o1]=src[__cs2j_inc_o2]", compact);
        Assert.DoesNotContain("Value++", compact);
        Assert.DoesNotContain("srcPos++", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("abc33", actual);
    }

    [Fact]
    public void Owned_Postfix_Followed_By_DirectMap_Postfix_Same_Statement_Is_Allowed()
    {
        // UriHelper.cs copy-loop shape: the owned ByRef.Value++ is the LHS
        // index; a converter-direct int++ follows it on the RHS. Both effects
        // already run in that order, so materializing only the owned read/write
        // before the statement preserves evaluation order.
        const string src = """
            public sealed class Box
            {
                public int Value { get; set; }
            }
            public static class Demo
            {
                static string Copy(int[] src)
                {
                    var dest = new int[3];
                    var box = new Box();
                    int srcPos = 0;
                    while (srcPos < 3)
                    {
                        dest[box.Value++] = src[srcPos++];
                    }
                    return dest[0] + "," + dest[1] + "," + dest[2] + "/" + box.Value;
                }

                public static string Run() => Copy(new[] { 7, 8, 9 });
            }
            """;
        var expected = Invoke(src);
        var (output, diagnostics, changed) = RunRule(src);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        // The direct-map srcPos++ stays inside the statement; only the owned
        // property site materializes ahead of it.
        var compact = Compact(output);
        Assert.Contains("srcPos++", compact);
        Assert.DoesNotContain("Value++", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("7,8,9/3", actual);
    }

    [Fact]
    public void Same_Anchor_Materialization_Rejects_Foreign_Effect_Site_Follows()
    {
        const string src = """
            public sealed class Box
            {
                public int Value { get; set; }
            }
            public static class Demo
            {
                static int Bump() => 1;
                static int Combine(int a, int b) => a + b;
                public static string Run()
                {
                    var dest = new int[1];
                    var src = new[] { 1, 2, 3 };
                    var box = new Box();
                    ushort srcPos = 0;
                    dest[box.Value++] = Combine(Bump(), src[srcPos++]);
                    return dest[0].ToString();
                }
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.IncrementTargetNoAnchor);
    }

    private const string StatementFreePrefixSource = """
        using System;
        using System.Text;
        public sealed class Counter
        {
            public int Value { get; set; }
        }
        public static class Demo
        {
            static string Arg(int x) => "[" + x + "]";

            static string Shape(Counter c, int limit, Func<int> f)
            {
                var sb = new StringBuilder();
                while (++c.Value < limit) sb.Append('.');
                sb.Append(c.Value);
                sb.Append(Arg(++c.Value));
                if (++c.Value == 5) sb.Append('!');
                sb.Append(f());
                return sb.ToString();
            }

            public static string Run()
            {
                var c = new Counter();
                return Shape(c, 3, () => ++c.Value);
            }
        }
        """;

    [Fact]
    public void Statement_Free_Prefix_Inlines_In_Condition_Argument_Lambda()
    {
        var expected = Invoke(StatementFreePrefixSource);
        var (output, diagnostics, changed) = RunRule(StatementFreePrefixSource);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        Assert.Contains("(c.Value=c.Value+1)", compact);
        // Zero spliced statements: no temps anywhere, including inside the lambda.
        Assert.DoesNotContain("__cs2j_inc_", output);
        Assert.DoesNotContain("++c.Value", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("..3[4]!6", actual);
    }

    [Fact]
    public void Nested_Anchor_Standalone_Postfixes_Both_Splice_Without_Dangling_Placeholder()
    {
        // RegexNode.ReduceConcatenationWithAdjacentLoops shape (CS0103/CS0201
        // regression): two standalone property-postfix sites whose anchor
        // statements live in NESTED blocks (a sibling if-body inside the same
        // outer if-body). A container-rebuilding Pass2 discarded the inner
        // block's splice, leaving `__cs2j_inc_dN;` as an expression statement.
        const string src = """
            public sealed class Node
            {
                public int M { get; set; }
                public int N { get; set; }
            }
            public static class Demo
            {
                static string Combine(Node node, bool canCombine, bool cap)
                {
                    if (canCombine)
                    {
                        node.M++;
                        if (cap)
                        {
                            node.N++;
                        }
                    }
                    return node.M + "," + node.N;
                }

                public static string Run()
                    => Combine(new Node(), true, true) + "|" + Combine(new Node(), true, false);
            }
            """;
        var expected = Invoke(src);
        var (output, diagnostics, changed) = RunRule(src);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        Assert.Contains("node.M=node.M+1", compact);
        Assert.Contains("node.N=node.N+1", compact);
        // No placeholder identifier survives as a dangling statement.
        Assert.DoesNotContain("__cs2j_inc_d", compact);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("1,1|1,0", actual);
    }

    [Fact]
    public void Prefix_Indexer_Inside_Local_Decl_Keeps_Declared_Name_In_Scope()
    {
        // ContentValidator.cs regression (CS2J5361 -> CS2J5367 strict gate):
        // `decimal count = ++counters[arr[i]];` is a complex-target prefix site
        // anchored on a LOCAL DECLARATION. The C17 splice must insert the
        // receiver/index temps as SIBLING STATEMENTS of the declaration;
        // wrapping the declaration in a bare block scoped `count` out of the
        // following statements, so the C34 decimal sweep saw operand type '?'
        // and refused to rewrite `count == max` / `count < min`.
        const string src = """
            public static class Demo
            {
                static string Decide(decimal[] counters, int[] arr, int i, decimal max, decimal min)
                {
                    decimal count = ++counters[arr[i]];
                    if (count == max)
                        return "max";
                    else if (count < min)
                        return "min";
                    return "mid";
                }

                public static string Run()
                {
                    return Decide(new decimal[2], new[] { 0 }, 0, 1m, 5m)
                        + "|" + Decide(new decimal[2], new[] { 0 }, 0, 9m, 5m)
                        + "|" + Decide(new decimal[2], new[] { 0 }, 0, 9m, 1m);
                }
            }
            """;
        var expected = Invoke(src);
        var (output, diagnostics, changed) = RunRule(src);

        Assert.True(changed);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);
        Assert.Contains("__cs2j_inc_", output);

        var actual = Invoke(output);
        Assert.Equal(expected, actual);
        Assert.Equal("max|min|mid", actual);
    }
}
