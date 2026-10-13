using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.Nullable;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C22 expression-lambda linearization (DebuggerAttributes.cs:73/83 shapes):
/// <code>
/// x => c0 &amp;&amp; GetState(x) != Never
/// → x => { if (!(c0)) return false; return &lt;guarded lifted tail&gt;; }
/// </code>
/// The conjunction prefix becomes early-false guards (identical short-circuit
/// order/frequency); the lifted site gains a return anchor inside the block
/// scope and normal HasValue guard hoisting applies.
/// </summary>
public class NullableLambdaLinearizationTests
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
            "nl-lam-" + Guid.NewGuid().ToString("N"),
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
        var result = new NullableLiftRule().Execute(context);
        return (result.Root.ToFullString(), diagnostics, result.MadeChanges);
    }

    private static string Invoke(string source)
    {
        var compilation = CSharpCompilation.Create(
            "nl-lam-emit-" + Guid.NewGuid().ToString("N"),
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

    private const string PredicateSource = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Text;
        public enum State { A, Never }
        public sealed class Item
        {
            public bool Public;
            public int StateCalls;
            public int NameCalls;
            public State? GetState() { StateCalls++; return Public ? State.A : State.Never; }
            public string? GetName() { NameCalls++; return Public ? "n" : null; }
        }
        public static class Demo
        {
            public static string Run()
            {
                var items = new[]
                {
                    new Item { Public = false },
                    new Item { Public = true },
                };

                var byPublic = items
                    .Where(x => x.Public && x.GetState() != State.Never)
                    .ToList();
                var byName = items
                    .Where(x => x.GetName() != null && x.GetState() != State.Never)
                    .ToList();

                var sb = new StringBuilder();
                sb.Append(byPublic.Count).Append(byPublic[0].StateCalls).Append(';');
                sb.Append(byName.Count).Append(items[0].NameCalls).Append(items[0].StateCalls)
                  .Append(items[1].StateCalls);
                return sb.ToString();
            }
        }
        """;


    [Fact]
    public void Conjunction_Predicate_Lambda_Linearizes_And_Behaves_Identically()
    {
        var expected = Invoke(PredicateSource);
        var (output, diagnostics, changed) = RunRule(PredicateSource);

        Assert.True(changed, string.Join("\n", diagnostics) + "\n---\n" + output);
        Assert.DoesNotContain(diagnostics, d => d.Severity == NumericBoundarySeverity.Error);

        var compact = Compact(output);
        // Both lambdas are block lambdas now, with an early-false guard and a
        // hoisted HasValue guard around the lifted comparison.
        Assert.Contains("=>{", compact);
        Assert.Contains("if(!(x.Public))returnfalse;", compact);
        Assert.Contains("if(!(x.GetName()!=null))returnfalse;", compact);
        Assert.Contains("__cs2j_n_", compact);
        Assert.Contains("HasValue", compact);
        // Both lists materialize before the StringBuilder reads: item1's
        // GetState fires once per predicate (2 total), item0's never fires.
        Assert.Equal("12;1102", Invoke(output));
        Assert.Equal(expected, Invoke(output));
    }

    [Fact]
    public void Site_Not_In_Leftmost_Site_Component_Still_Reports_CS2J5355()
    {
        // Lifted sites in c0 AND c2: after splitting at c0, the c2 site is
        // still on a short-circuit right arm — no exact linearization exists,
        // the rule rolls back with CS2J5355 as before.
        const string src = """
            using System;
            using System.Linq;
            public enum State { A, Never }
            public sealed class Item
            {
                public bool Public;
                public State? GetState() => Public ? State.A : State.Never;
            }
            public static class Demo
            {
                public static string Run()
                {
                    var items = new[] { new Item() };
                    return items
                        .Where(x => x.GetState() != State.Never
                                    && x.Public
                                    && x.GetState() == State.A)
                        .Count()
                        .ToString();
                }
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }

    [Fact]
    public void Expression_Tree_Lambda_Is_Not_Linearized()
    {
        const string src = """
            using System;
            using System.Linq.Expressions;
            public enum State { A, Never }
            public sealed class Item
            {
                public bool Public;
                public State? GetState() => State.A;
            }
            public static class Demo
            {
                public static string Run()
                {
                    Expression<Func<Item, bool>> predicate =
                        x => x.Public && x.GetState() != State.Never;
                    return predicate.ToString() ?? "";
                }
            }
            """;
        var (output, diagnostics, changed) = RunRule(src);

        // Statement lambdas cannot bind Expression<TDelegate>: the candidate
        // is skipped and the site keeps its CS2J5354 expression-tree rollback.
        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && (d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget
                    || d.Code == PreprocessDiagnosticCodes.NullableLiftExpressionTree));
        Assert.DoesNotContain("return false", output);
    }

    [Fact]
    public void Non_Conjunction_Lambda_Body_Is_Not_Linearized()
    {
        const string src = """
            using System;
            public enum State { A, Never }
            public sealed class Item
            {
                public State? GetState() => State.A;
            }
            public static class Demo
            {
                static Func<Item, bool> Predicate()
                    => x => x.GetState() != State.Never;
                public static string Run() => Predicate().ToString() ?? "";
            }
            """;
        var (_, diagnostics, changed) = RunRule(src);

        Assert.False(changed);
        Assert.Contains(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error
                && d.Code == PreprocessDiagnosticCodes.NullableLiftComplexTarget);
    }
}
