using CSharpToJava.Core.EqualityConcat;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Locks the full M0 probe classification table through the single
/// <see cref="SemanticKindClassifier"/>: every category in the probe matrix
/// has an explicit assertion, including the edge cases (MulticastDelegate,
/// events, dynamic, expression trees, nullable tuples).
/// </summary>
public class EqualityConcatClassifierUnitTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException).Assembly.Location),
    };

    private static EqualityConcatClassification ClassifyFirst(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("ec-classifier",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);

        var node = tree.GetRoot().DescendantNodes().First(IsCandidate);
        return SemanticKindClassifier.Classify(node, model);
    }

    private static bool IsCandidate(SyntaxNode node)
        => node switch
        {
            BinaryExpressionSyntax binary => binary.OperatorToken.Kind()
                is SyntaxKind.EqualsEqualsToken
                    or SyntaxKind.ExclamationEqualsToken
                    or SyntaxKind.PlusToken,
            AssignmentExpressionSyntax assignment => assignment.OperatorToken
                .IsKind(SyntaxKind.PlusEqualsToken),
            _ => false,
        };

    [Fact]
    public void Classifies_StringEquality()
    {
        var c = ClassifyFirst("class C { bool M(string a, string b) => a == b; }");
        Assert.Equal(EqualityConcatKind.StringEquality, c.Kind);
        Assert.True(c.IsEquality);
        Assert.False(c.IsNotEquals);
        Assert.Equal(SpecialType.System_String, c.BoundMethod?.ContainingType.SpecialType);
    }

    [Fact]
    public void Classifies_StringNotEquals()
    {
        var c = ClassifyFirst("class C { bool M(string a, string b) => a != b; }");
        Assert.Equal(EqualityConcatKind.StringEquality, c.Kind);
        Assert.True(c.IsNotEquals);
    }

    [Fact]
    public void Classifies_ObjectIdentity()
    {
        var c = ClassifyFirst(
            "class C { bool M(string a, string b) => (object)a == b; }");
        Assert.Equal(EqualityConcatKind.ObjectIdentity, c.Kind);
        Assert.Equal(SpecialType.System_Object, c.BoundMethod?.ContainingType.SpecialType);
    }

    [Fact]
    public void Classifies_DelegateEquality()
    {
        var c = ClassifyFirst(
            "class C { bool M(System.Action a, System.Action b) => a == b; }");
        Assert.Equal(EqualityConcatKind.DelegateEquality, c.Kind);
    }

    [Fact]
    public void Classifies_SystemDelegate_Equality()
    {
        var c = ClassifyFirst(
            "class C { bool M(System.Delegate a, System.Delegate b) => a == b; }");
        Assert.Equal(EqualityConcatKind.DelegateEquality, c.Kind);
    }

    [Fact]
    public void Classifies_MulticastDelegate_As_ObjectIdentity()
    {
        var c = ClassifyFirst(
            "class C { bool M(System.MulticastDelegate a) => a == a; }");
        Assert.Equal(EqualityConcatKind.ObjectIdentity, c.Kind);
    }

    [Fact]
    public void Classifies_RecordClass_Equality()
    {
        var c = ClassifyFirst("""
            class C {
                record Rc(string Name);
                bool M(Rc a, Rc b) => a == b;
            }
            """);
        Assert.Equal(EqualityConcatKind.RecordValueEquality, c.Kind);
        Assert.True(c.BoundMethod?.IsImplicitlyDeclared);
        Assert.True(c.BoundMethod?.ContainingType.IsRecord);
    }

    [Fact]
    public void Classifies_RecordStruct_Equality()
    {
        var c = ClassifyFirst("""
            class C {
                record struct Rs(int X);
                bool M(Rs a, Rs b) => a == b;
            }
            """);
        Assert.Equal(EqualityConcatKind.RecordValueEquality, c.Kind);
    }

    [Fact]
    public void Classifies_StringConcat()
    {
        var c = ClassifyFirst(
            "class C { string M(string a, string b) => a + b; }");
        Assert.Equal(EqualityConcatKind.StringConcat, c.Kind);
    }

    [Fact]
    public void Classifies_StringConcatCompound()
    {
        var c = ClassifyFirst("""
            class C { void M(string a, string b) { a += b; } }
            """);
        Assert.Equal(EqualityConcatKind.StringConcatCompound, c.Kind);
    }

    [Fact]
    public void Classifies_LiftedNullableEquality()
    {
        var c = ClassifyFirst("class C { bool M(bool? x) => x == true; }");
        Assert.Equal(EqualityConcatKind.LiftedNullableEquality, c.Kind);
    }

    [Fact]
    public void Classifies_TupleResidual()
    {
        var c = ClassifyFirst(
            "class C { bool M((int A, string B) t) => t == t; }");
        Assert.Equal(EqualityConcatKind.TupleEqualityResidual, c.Kind);
    }

    [Fact]
    public void Classifies_NullableTupleNull_As_TupleResidual()
    {
        var c = ClassifyFirst(
            "class C { bool M((int, string)? nt) => nt == null; }");
        Assert.Equal(EqualityConcatKind.TupleEqualityResidual, c.Kind);
    }

    [Fact]
    public void Classifies_EventNullCheck()
    {
        var c = ClassifyFirst("""
            class C {
                event System.Action E;
                bool M() => E == null;
            }
            """);
        Assert.Equal(EqualityConcatKind.EventNullCheck, c.Kind);
    }

    [Fact]
    public void Classifies_UserDefinedOperator()
    {
        var c = ClassifyFirst("""
            struct SPoint {
                public static bool operator ==(SPoint a, SPoint b) => true;
                public static bool operator !=(SPoint a, SPoint b) => false;
                public override bool Equals(object? o) => true;
                public override int GetHashCode() => 0;
            }
            class C { bool M(SPoint a, SPoint b) => a == b; }
            """);
        Assert.Equal(EqualityConcatKind.UserDefinedOperator, c.Kind);
        Assert.False(c.BoundMethod?.IsImplicitlyDeclared);
    }

    [Fact]
    public void Classifies_PredefinedOther()
    {
        var c = ClassifyFirst("class C { bool M(int a, int b) => a == b; }");
        Assert.Equal(EqualityConcatKind.PredefinedOther, c.Kind);
    }

    [Fact]
    public void Classifies_ExpressionTree()
    {
        var c = ClassifyFirst("""
            using System;
            using System.Linq.Expressions;
            class C {
                Expression<Func<string, string, bool>> M() => (a, b) => a == b;
            }
            """);
        Assert.Equal(EqualityConcatKind.ExpressionTree, c.Kind);
    }

    [Fact]
    public void Classifies_Dynamic()
    {
        var c = ClassifyFirst("class C { bool M(dynamic z) => z == \"a\"; }");
        Assert.Equal(EqualityConcatKind.Dynamic, c.Kind);
    }
}
