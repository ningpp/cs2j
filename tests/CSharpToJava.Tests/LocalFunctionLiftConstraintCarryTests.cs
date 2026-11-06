using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// S-05 (upgrade plan §6.4-R0): local-function-lift copies the enclosing method's
/// constraint clauses verbatim, so a lifted method carried `where` clauses for
/// parameters it does not declare (dangling clauses), and it had no transitive
/// closure over constraint references (`where T : IFoo&lt;U&gt;` named an
/// undeclared U). Carried clauses must be exactly those of the parameters the
/// lifted method declares, closed over clause references.
/// </summary>
public class LocalFunctionLiftConstraintCarryTests
{
    private static LocalFunctionLiftResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "local-fn-constraint-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IComparable<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new LocalFunctionLiftPreprocessor().Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static MethodDeclarationSyntax LiftedMethod(string code, string name)
        => CSharpSyntaxTree.ParseText(code)
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => string.Equals(m.Identifier.Text, name, StringComparison.OrdinalIgnoreCase));

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "local-fn-constraint-verify",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(IComparable<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Lifted local function failed to compile:\n" + string.Join("\n", errors) + "\n--- code ---\n" + code);
    }

    [Fact]
    public void UnusedEnclosingConstraint_IsNotCopiedOntoLiftedMethod()
    {
        var result = Run("""
            using System;
            class C
            {
                public int Outer<T, U>(T t, U u) where T : class where U : struct
                {
                    int Local(T value) { return value.ToString() == null ? 0 : 1; }
                    return Local(t);
                }
            }
            """);
        var lifted = LiftedMethod(result.OutputCode, "Local");
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where T : class", StringComparison.Ordinal));
        Assert.DoesNotContain(clauses, c => c.Contains("where U", StringComparison.Ordinal));
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ConstraintReferencedParameter_IsCarriedTransitively()
    {
        // The body only mentions T; U is reachable solely through T's clause.
        var result = Run("""
            using System;
            class C
            {
                public bool Outer<T, U>(T t) where T : IComparable<U> where U : struct
                {
                    bool Local(T value) { return value.ToString() != null; }
                    return Local(t);
                }
            }
            """);
        var lifted = LiftedMethod(result.OutputCode, "Local");
        Assert.Equal(2, lifted.TypeParameterList!.Parameters.Count);
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where T : IComparable<U>", StringComparison.Ordinal));
        Assert.Contains(clauses, c => c.Contains("where U : struct", StringComparison.Ordinal));
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void LocalClausesReferencingEnclosingParameter_CarryIt()
    {
        // The local function's OWN `where U : IComparable<T>` names the enclosing T,
        // so T must be redeclared even though the body never mentions it.
        var result = Run("""
            using System;
            class C
            {
                public int Outer<T>(T t) where T : class
                {
                    int Local<U>(U u) where U : IComparable<T> { return 0; }
                    return 0;
                }
            }
            """);
        var lifted = LiftedMethod(result.OutputCode, "Local");
        Assert.Equal(2, lifted.TypeParameterList!.Parameters.Count);
        var clauses = lifted.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("where U : IComparable<T>", StringComparison.Ordinal));
        Assert.Contains(clauses, c => c.Contains("where T : class", StringComparison.Ordinal));
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ExplicitCallSiteTypeArguments_AreExtendedToTheNewArity()
    {
        // The call site spells its own type argument (`Local<int>(0)`); the lifted
        // method also declares the carried enclosing T, so the explicit list must be
        // extended to <int, T> or the call fails with CS0305.
        var result = Run("""
            using System;
            class C
            {
                public int Outer<T>(T t) where T : class
                {
                    int Local<U>(U u, T value) { return u.ToString() == value.ToString() ? 1 : 0; }
                    return Local<int>(0, t);
                }
            }
            """);
        var lifted = LiftedMethod(result.OutputCode, "Local");
        Assert.Equal(2, lifted.TypeParameterList!.Parameters.Count);

        var invocation = CSharpSyntaxTree.ParseText(result.OutputCode)
            .GetRoot()
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is GenericNameSyntax gn
                && string.Equals(gn.Identifier.Text, "Local", StringComparison.OrdinalIgnoreCase));
        var typeArguments = ((GenericNameSyntax)invocation.Expression).TypeArgumentList.Arguments;
        Assert.Equal(2, typeArguments.Count);
        AssertCompiles(result.OutputCode);
    }
}
