using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests.Analysis;

/// <summary>
/// R1a (upgrade plan §6.4-R1 "五件套"): the shared
/// <see cref="SyntheticTypeParameterResolver"/> is the single semantic owner of
/// the type-parameter set a synthesized member must redeclare. These tests pin
/// the five outputs — ordered carry set with constraint closure, dedup against
/// host scope, three-state expressibility, explicit site type arguments and
/// deterministic shadowing — plus the closure-lift M2/M3 migration.
/// </summary>
public class SyntheticTypeParameterResolverTests
{
    // ── ① signature + body → carry, and ⑤ signature-visible → infer ──

    [Fact]
    public void SignatureAndBody_Reference_CarriesTheParameter()
    {
        var (model, root) = Compile("""
            using System;
            class C
            {
                public Func<T, int> Make<T>(T value)
                {
                    return x => { T copy = x; return copy.GetHashCode(); };
                }
            }
            """);

        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();
        var plan = SyntheticTypeParameterResolver.Resolve(model, lambda, body: lambda);

        var parameter = Assert.Single(plan.Parameters);
        Assert.Equal("T", parameter.Name);
        Assert.Equal(SyntheticTypeParameterOrigin.Signature, parameter.Origin);
        Assert.Equal(SyntheticTypeParameterState.Unconstrained, parameter.State);
        Assert.Null(parameter.StableName);
        // T appears in the converted delegate signature: method-group inference
        // can supply it, so no explicit site type arguments.
        Assert.Empty(plan.TypeArguments);
        Assert.DoesNotContain(plan.Diagnostics,
            d => d.Code == SyntheticTypeParameterResolver.NotExpressibleCode);
    }

    // ── ② `where T : IFoo<U>` → U carried too (S-08) ──

    [Fact]
    public void ConstraintClosure_CarriesReferencedParameter_AndListsExplicitTypeArguments()
    {
        var (model, root) = Compile("""
            using System;
            interface IFoo<T> { }
            class C
            {
                public Func<T> Make<T, U>(T value) where T : IFoo<U>
                {
                    return () => value;
                }
            }
            """);

        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();
        var plan = SyntheticTypeParameterResolver.Resolve(model, lambda, body: lambda);

        Assert.Collection(plan.Parameters,
            first =>
            {
                Assert.Equal("T", first.Name);
                Assert.NotNull(first.Constraint);
                Assert.Equal(SyntheticTypeParameterState.Expressible, first.State);
            },
            second =>
            {
                Assert.Equal("U", second.Name);
                Assert.Equal(SyntheticTypeParameterOrigin.ConstraintClosure, second.Origin);
                Assert.Null(second.Constraint);
            });

        // U is not visible in `Func<T>`: the delegate site must spell both.
        Assert.Equal(new[] { "T", "U" }, plan.TypeArguments);
    }

    // ── ③ host-visible type parameters are never redeclared ──

    [Fact]
    public void HostTypeParameter_IsNotRedeclared()
    {
        var (model, root) = Compile("""
            using System;
            class C<T>
            {
                public Func<T, int> Make(T value) => x => x.GetHashCode();
            }
            """);

        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();
        var plan = SyntheticTypeParameterResolver.Resolve(model, lambda, body: lambda);

        Assert.Empty(plan.Parameters);
        Assert.Empty(plan.TypeArguments);
        Assert.Empty(plan.Diagnostics);
    }

    // ── ④ unconstrained vs NotExpressible (S-03) ──

    [Fact]
    public void UnconstrainedParameter_IsReportedAsUnconstrained()
    {
        var (model, root) = Compile("""
            using System;
            class C
            {
                public Func<T> Make<T>(T value) { return () => value; }
            }
            """);

        var lambda = root.DescendantNodes().OfType<LambdaExpressionSyntax>().Single();
        var plan = SyntheticTypeParameterResolver.Resolve(model, lambda, body: lambda);

        var parameter = Assert.Single(plan.Parameters);
        Assert.Equal(SyntheticTypeParameterState.Unconstrained, parameter.State);
        Assert.Null(parameter.Reason);
    }

    [Fact]
    public void UnresolvedConstraint_IsNotExpressible_WithReason_AndNeverThrows()
    {
        var (model, root) = Compile("""
            using System;
            class C
            {
                public Func<T> Make<T>() where T : MissingType { return () => default; }
            }
            """);

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var plan = SyntheticTypeParameterResolver.Resolve(model, method, body: method);

        var parameter = Assert.Single(plan.Parameters);
        Assert.Equal("T", parameter.Name);
        Assert.Equal(SyntheticTypeParameterState.NotExpressible, parameter.State);
        Assert.False(string.IsNullOrWhiteSpace(parameter.Reason));
        Assert.Contains(plan.Diagnostics,
            d => d.Code == SyntheticTypeParameterResolver.NotExpressibleCode && d.ParameterName == "T");
    }

    // ── ⑤ shadowing → deterministic StableName ──

    [Fact]
    public void ShadowedParameter_GetsDeterministicStableName()
    {
        var (model, root) = Compile("""
            using System;
            class C
            {
                public void Outer<T>(T outer)
                {
                    void Local<T>(T inner) { }
                }
            }
            """);

        var local = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        var outerTypeParameter = model.GetDeclaredSymbol(
            root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single())!.TypeParameters[0];
        var signatureTypes = new ITypeSymbol?[] { outerTypeParameter };

        var first = SyntheticTypeParameterResolver.Resolve(
            model, local, body: local, signatureTypes: signatureTypes);
        var second = SyntheticTypeParameterResolver.Resolve(
            model, local, body: local, signatureTypes: signatureTypes);

        Assert.Equal(2, first.Parameters.Count);
        Assert.Equal("T", first.Parameters[0].Name);
        Assert.Null(first.Parameters[0].StableName);
        Assert.Equal("T", first.Parameters[1].Name);
        Assert.NotNull(first.Parameters[1].StableName);
        Assert.NotEqual("T", first.Parameters[1].StableName);

        Assert.Equal(
            first.Parameters.Select(p => p.RenderName),
            second.Parameters.Select(p => p.RenderName));
        Assert.Contains(first.Diagnostics,
            d => d.Code == SyntheticTypeParameterResolver.ShadowedCode);
    }

    // ── closure-lift M2/M3 integration (2 fixtures) ──

    [Fact]
    public void ClosureLift_ExpressionBodiedLambda_StaysCompilable()
    {
        var result = new ClosureLiftPreprocessor().Preprocess("""
            using System;
            class C
            {
                public Func<int, int> Make(int seed)
                {
                    return x => x + seed;
                }
            }
            """);

        Assert.True(result.Changed);
        Assert.Contains("Cs2jDisplayClass_Make_0", result.OutputCode, StringComparison.Ordinal);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void ClosureLift_DisplayClass_CarriesConstraintClosure()
    {
        var result = new ClosureLiftPreprocessor().Preprocess("""
            using System;
            interface IFoo<T> { }
            class C
            {
                public Func<T> Make<T, U>(T value) where T : IFoo<U>
                {
                    return () => value;
                }
            }
            """);

        Assert.True(result.Changed);

        var synthesized = CSharpSyntaxTree.ParseText(result.OutputCode)
            .GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text.StartsWith("Cs2jDisplayClass_", StringComparison.Ordinal));

        Assert.Equal(
            new[] { "T", "U" },
            synthesized.TypeParameterList!.Parameters.Select(p => p.Identifier.Text));
        Assert.Contains(
            synthesized.ConstraintClauses,
            clause => clause.ToString().Contains("IFoo<U>", StringComparison.Ordinal));
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    private static (SemanticModel Model, CompilationUnitSyntax Root) Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "synthetic-type-parameters",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return (compilation.GetSemanticModel(tree), (CompilationUnitSyntax)tree.GetRoot());
    }

    private static IReadOnlyList<string> CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "closure-lift-r1a-verify",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
    }
}
