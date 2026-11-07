using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// R1b (upgrade plan §6.4-R1): local-function-lift consumes the shared
/// <see cref="CSharpToJava.Core.Analysis.SyntheticTypeParameterResolver"/> plan
/// instead of its own name-based carry logic. These tests pin constraint closure
/// (S-08), class-level scoping (④), shadowing dedup (S-05) and the conservative
/// NotExpressible fallback (S-03) during the migration.
/// </summary>
public class LocalFunctionLiftResolverMigrationTests
{
    private static LocalFunctionLiftResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = Compile(tree);
        return new LocalFunctionLiftPreprocessor().Preprocess(
            source, compilation.GetSemanticModel(tree));
    }

    private static MethodDeclarationSyntax LiftedMethod(string code, string name)
        => CSharpSyntaxTree.ParseText(code)
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => string.Equals(m.Identifier.Text, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EnclosingConstraintClosure_IsCarriedThroughResolverPlan()
    {
        var result = Run("""
            using System;
            interface IFoo<T> { }
            class C
            {
                public void Outer<T, U>(T t) where T : IFoo<U> where U : struct
                {
                    void Local(T value) { value.ToString(); }
                    Local(t);
                }
            }
            """);

        Assert.True(result.Changed);
        var lifted = LiftedMethod(result.OutputCode, "Local");
        Assert.Equal(
            new[] { "T", "U" },
            lifted.TypeParameterList!.Parameters.Select(p => p.Identifier.Text));
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where T : IFoo<U>", StringComparison.Ordinal));
        Assert.Contains(clauses, c => c.Contains("where U : struct", StringComparison.Ordinal));
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ClassLevelTypeParameter_IsNotRedeclared()
    {
        var result = Run("""
            using System;
            class C<T>
            {
                public void Outer(T t)
                {
                    void Local(T value) { value.ToString(); }
                    Local(t);
                }
            }
            """);

        Assert.True(result.Changed);
        var lifted = LiftedMethod(result.OutputCode, "Local");
        Assert.Null(lifted.TypeParameterList);
        Assert.Empty(lifted.ConstraintClauses);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ShadowedTypeParameter_IsDeclaredOnce_AndStillLifts()
    {
        var result = Run("""
            using System;
            class C
            {
                public void Outer<T>(T t)
                {
                    void Local<T>(T inner) { inner.ToString(); }
                    Local(t);
                }
            }
            """);

        Assert.True(result.Changed);
        var lifted = LiftedMethod(result.OutputCode, "Local");
        Assert.Equal(
            new[] { "T" },
            lifted.TypeParameterList!.Parameters.Select(p => p.Identifier.Text));
        Assert.DoesNotContain(
            lifted.TypeParameterList.Parameters,
            p => p.Identifier.Text.StartsWith("T_", StringComparison.Ordinal));
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void NotExpressibleEnclosingConstraint_SkipsWithoutDanglingWhere()
    {
        var result = Run("""
            using System;
            class C
            {
                public void Outer<T>(T t) where T : MissingType
                {
                    void Local(T value) { value.ToString(); }
                    Local(t);
                }
            }
            """);

        Assert.False(result.Changed);
        Assert.DoesNotContain("private void local", result.OutputCode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.EnclosingTypeParameterConflict
            && d.Message.Contains("cannot be reproduced", StringComparison.Ordinal));
    }

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = Compile(tree).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Lifted local function failed to compile:\n"
            + string.Join("\n", errors) + "\n--- code ---\n" + code);
    }

    private static CSharpCompilation Compile(SyntaxTree tree)
        => CSharpCompilation.Create(
            "local-fn-resolver-migration",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IComparable<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
