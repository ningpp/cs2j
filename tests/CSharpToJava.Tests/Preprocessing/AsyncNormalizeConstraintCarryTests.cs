using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// S-08 (upgrade plan §6.4-R0): async-normalize carries enclosing method type
/// parameters into the lifted method by NAME matching and copies only the clauses
/// of directly-used parameters. A clause like <c>where T : IFoo&lt;U&gt;</c> therefore
/// named an undeclared U (CS0246), and own-clause references to enclosing
/// parameters were not carried at all. The carry set is now closed over clause
/// references, and clauses are emitted exactly for the carried parameters.
/// </summary>
public sealed class AsyncNormalizeConstraintCarryTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static AsyncNormalizeFileResult Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-constraint-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return new AsyncNormalizePreprocessor().Preprocess(
            src, compilation.GetSemanticModel(tree), new AsyncNormalizeOptions());
    }

    private static MethodDeclarationSyntax LiftedMethod(string code)
        => CSharpSyntaxTree.ParseText(code, ParseOptions)
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text.StartsWith("Cs2jAsync_", StringComparison.Ordinal));

    [Fact]
    public void ConstraintReferencedParameter_IsCarriedTransitively()
    {
        var result = Run("""
            using System;
            using System.Threading.Tasks;
            class C
            {
                public async Task Outer<T, U>(T value)
                    where T : IComparable<U> where U : struct
                {
                    Func<Task> f = async () => { await Task.Delay(0); Console.WriteLine(default(T)); };
                    await f();
                }
            }
            """);
        Assert.True(result.Changed);

        var lifted = LiftedMethod(result.OutputCode);
        Assert.Equal(2, lifted.TypeParameterList!.Parameters.Count);
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where T : IComparable<U>", StringComparison.Ordinal));
        Assert.Contains(clauses, c => c.Contains("where U : struct", StringComparison.Ordinal));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void UnusedEnclosingConstraint_IsNotCarried()
    {
        var result = Run("""
            using System;
            using System.Threading.Tasks;
            class C
            {
                public async Task Outer<T, U>(T value) where T : class where U : struct
                {
                    Func<Task> f = async () => { await Task.Delay(0); Console.WriteLine(default(T)); };
                    await f();
                }
            }
            """);
        var lifted = LiftedMethod(result.OutputCode);
        var typeParameters = lifted.TypeParameterList!.Parameters.Select(p => p.Identifier.Text).ToList();
        Assert.Equal(new[] { "T" }, typeParameters);
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where T : class", StringComparison.Ordinal));
        Assert.DoesNotContain(clauses, c => c.Contains("where U", StringComparison.Ordinal));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void OwnClauseReferencingEnclosingParameter_CarriesIt()
    {
        var result = Run("""
            using System;
            using System.Threading.Tasks;
            class C
            {
                public async Task Outer<T>(T value) where T : class
                {
                    async Task<int> Local<V>(V v) where V : IComparable<T>
                    {
                        await Task.Delay(0);
                        return 0;
                    }

                    await Task.Delay(0);
                }
            }
            """);
        var lifted = LiftedMethod(result.OutputCode);
        var typeParameters = lifted.TypeParameterList!.Parameters.Select(p => p.Identifier.Text).ToList();
        Assert.Equal(new[] { "V", "T" }, typeParameters);
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where V : IComparable<T>", StringComparison.Ordinal));
        Assert.Contains(clauses, c => c.Contains("where T : class", StringComparison.Ordinal));
        AssertOutputCompiles(result.OutputCode);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-constraint-verify",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "lifted async method failed to compile:\n" + string.Join("\n", errors) + "\n--- code ---\n" + code);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
