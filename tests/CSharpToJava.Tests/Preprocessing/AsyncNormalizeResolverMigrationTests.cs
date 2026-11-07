using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.AsyncNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R1b (upgrade plan §6.4-R1): async-normalize consumes the shared
/// <see cref="SyntheticTypeParameterResolver"/> instead of its private
/// name-matching carry logic. These tests pin the migrated behaviour: constraint
/// closure (S-08), own + enclosing + class-level scoping, the three-state
/// fallback (S-03) and the explicit-type-argument contract (④).
/// </summary>
public sealed class AsyncNormalizeResolverMigrationTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static AsyncNormalizeFileResult Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-resolver-migration",
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
    public void AsyncLambda_ConstraintClosure_CarriesReferencedParameter()
    {
        var result = Run("""
            using System;
            using System.Threading.Tasks;
            interface IFoo<T> { }
            class C
            {
                public async Task Outer<T, U>(T value) where T : IFoo<U> where U : struct
                {
                    Func<Task> f = async () => { await Task.Delay(0); Console.WriteLine(default(T)); };
                    await f();
                }
            }
            """);

        Assert.True(result.Changed);
        var lifted = LiftedMethod(result.OutputCode);
        Assert.Equal(
            new[] { "T", "U" },
            lifted.TypeParameterList!.Parameters.Select(p => p.Identifier.Text));
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where T : IFoo<U>", StringComparison.Ordinal));
        Assert.Contains(clauses, c => c.Contains("where U : struct", StringComparison.Ordinal));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void AsyncLocalFunction_CarriesOwnAndEnclosing_ButNotClassLevel()
    {
        var result = Run("""
            using System;
            using System.Threading.Tasks;
            interface IFoo<T> { }
            class Impl<T> : IFoo<T> { }
            class C<TClass>
            {
                public async Task Outer<U>(U value) where U : class
                {
                    async Task<int> Local<V>(V v, U u) where V : IFoo<U>
                    {
                        await Task.Delay(0);
                        Console.WriteLine(default(TClass));
                        return 0;
                    }

                    await Local(new Impl<U>(), value);
                }
            }
            """);

        Assert.True(result.Changed);
        var lifted = LiftedMethod(result.OutputCode);
        Assert.Equal(
            new[] { "V", "U" },
            lifted.TypeParameterList!.Parameters.Select(p => p.Identifier.Text));
        Assert.DoesNotContain(
            lifted.TypeParameterList.Parameters,
            p => p.Identifier.Text == "TClass");
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where V : IFoo<U>", StringComparison.Ordinal));
        Assert.Contains(clauses, c => c.Contains("where U : class", StringComparison.Ordinal));
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void AsyncLambda_NotExpressibleConstraint_DefersWithoutLifting()
    {
        var result = Run("""
            using System;
            using System.Threading.Tasks;
            class C
            {
                public async Task Outer<T>(T value) where T : MissingType
                {
                    Func<Task> f = async () => { await Task.Delay(0); Console.WriteLine(default(T)); };
                    await f();
                }
            }
            """);

        Assert.False(result.Changed);
        Assert.DoesNotContain("Cs2jAsync_", result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1807
            && d.Message.Contains("cannot be reproduced", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncLocalFunction_NotExpressibleConstraint_DefersWithoutLifting()
    {
        var result = Run("""
            using System;
            using System.Threading.Tasks;
            interface IFoo<T> { }
            class C
            {
                public async Task Outer<T>(T value) where T : MissingType
                {
                    async Task Local<U>(U u) where U : IFoo<T>
                    {
                        await Task.Delay(0);
                    }
                }
            }
            """);

        Assert.False(result.Changed);
        Assert.DoesNotContain("Cs2jAsync_", result.OutputCode);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == AsyncNormalizeDiagnosticCode.CS2J1804
            && d.Message.Contains("cannot be reproduced", StringComparison.Ordinal));
    }

    // ── ④ explicit-site contract: the resolver is the single owner ──

    [Fact]
    public void ResolverContract_SignatureVisibleNeedsNoExplicitArguments_ConstraintOnlyDoes()
    {
        const string src = """
            using System;
            using System.Threading.Tasks;
            interface IFoo<T> { }
            class C
            {
                public void SignatureVisible<T>(T value)
                {
                    Func<Task<T>> visible = async () => { await Task.Delay(0); return value; };
                }

                public void ConstraintOnly<T, U>(T value) where T : IFoo<U>
                {
                    Func<Task> closure = async () => { await Task.Delay(0); Console.WriteLine(default(T)); };
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-resolver-contract", new[] { tree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var lambdas = root.DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
        LambdaExpressionSyntax LambdaFor(string variable)
            => lambdas.Single(l =>
                l.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax vd }
                && vd.Identifier.Text == variable);

        var visiblePlan = SyntheticTypeParameterResolver.Resolve(
            model, LambdaFor("visible"), body: LambdaFor("visible"));
        Assert.Equal(new[] { "T" }, visiblePlan.Parameters.Select(p => p.RenderName));
        // T is visible in the converted delegate `Func<Task<T>>`: inference can
        // supply it, so the call site needs no explicit type arguments.
        Assert.Empty(visiblePlan.TypeArguments);

        var closurePlan = SyntheticTypeParameterResolver.Resolve(
            model, LambdaFor("closure"), body: LambdaFor("closure"));
        Assert.Equal(new[] { "T", "U" }, closurePlan.Parameters.Select(p => p.RenderName));
        // U is only reachable through the carried `where T : IFoo<U>` clause, so
        // the site must list the whole ordered set explicitly.
        Assert.Equal(new[] { "T", "U" }, closurePlan.TypeArguments);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "async-resolver-verify", new[] { tree }, GetReferences(),
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
