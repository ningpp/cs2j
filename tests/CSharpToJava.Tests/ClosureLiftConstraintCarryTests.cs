using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// S-03 (upgrade plan §6.4-R0): closure-lift re-declares the enclosing method's
/// type parameters on the synthesized display class / holder, but used to drop
/// their `where` clauses. Any member access that depends on a constraint then
/// stopped compiling inside the synthesized type (CS1061), because the
/// redeclared parameter lost the interface bound. The clauses must be carried —
/// filtered to the parameters actually redeclared, so no dangling `where`
/// appears for a parameter the synthesized type does not declare.
/// </summary>
public class ClosureLiftConstraintCarryTests
{
    private const string GenericConstrainedSource = """
        using System;

        class Sample
        {
            public string Describe<T>(T value, T other) where T : IComparable<T>
            {
                Func<T, string> f = v => v.CompareTo(other) > 0 ? "gt" : "le";
                return f(value);
            }
        }
        """;

    private static ClosureLiftResult Run(string source, ClosureLiftOptions? options = null)
        => new ClosureLiftPreprocessor().Preprocess(source, model: null, options);

    [Fact]
    public void DisplayClass_CarriesTheEnclosingMethodConstraint()
    {
        var result = Run(GenericConstrainedSource);
        Assert.True(result.Changed, "closure-lift must rewrite the capturing lambda");

        var synthesized = SynthesizedClass(result.OutputCode);
        Assert.Contains("T", synthesized.TypeParameterList!.Parameters.Select(p => p.Identifier.Text));
        var clauses = synthesized.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("IComparable<T>", StringComparison.Ordinal));
    }

    [Fact]
    public void DisplayClass_WithCarriedConstraint_Compiles()
    {
        var result = Run(GenericConstrainedSource);
        var errors = CompileErrors(result.OutputCode);
        Assert.Empty(errors);
    }

    [Fact]
    public void MutableCaptureHolder_CarriesTheEnclosingMethodConstraint()
    {
        // C32 (the mutated-capture path) emits the holder class itself; the display
        // class stage may later evolve the same group, so both emitters must carry
        // the clause.
        var result = Run("""
            using System;
            class Sample
            {
                public string Describe<T>(T value) where T : IComparable<T>
                {
                    T result = value;
                    Action a = () => result = value;
                    a();
                    return result.ToString();
                }
            }
            """);
        Assert.True(result.Changed, "the mutated capture must lift into a holder");
        var synthesized = SynthesizedClass(result.OutputCode);
        Assert.Contains("T", synthesized.TypeParameterList!.Parameters.Select(p => p.Identifier.Text));
        var clauses = synthesized.ConstraintClauses.Select(c => c.ToString()).ToList();
        Assert.Contains(clauses, c => c.Contains("IComparable<T>", StringComparison.Ordinal));
    }

    [Fact]
    public void NonGenericMethod_HasNoConstraintClause()
    {
        var result = Run("""
            using System;
            class S
            {
                public Func<int> F(int seed)
                {
                    return () => seed;
                }
            }
            """);
        Assert.True(result.Changed);
        Assert.DoesNotContain("where ", result.OutputCode, StringComparison.Ordinal);
    }

    private static ClassDeclarationSyntax SynthesizedClass(string code)
        => CSharpSyntaxTree.ParseText(code)
            .GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.Text.StartsWith("Cs2jDisplayClass_", StringComparison.Ordinal));

    private static IReadOnlyList<string> CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "closure-lift-verify",
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
