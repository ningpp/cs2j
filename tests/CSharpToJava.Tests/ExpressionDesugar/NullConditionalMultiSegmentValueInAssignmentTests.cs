using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// Regression for the dotnet.text.json corpus
/// (json/src/System/Text/JsonSerialization/ReadStack.cs):
/// <code>
/// utf8PropertyName = frame.JsonPropertyInfo?.NameAsUtf8Bytes ??
///     frame.CtorArgumentState?.JsonParameterInfo?.NameAsUtf8Bytes;
/// </code>
///
/// <para>
/// The right-hand side of <c>??</c> is a <b>multi-segment value</b> null-conditional
/// chain (<c>a?.b?.c</c>, two <c>?.</c> hops) whose nearest enclosing statement
/// (after the null-coalesce preprocessor lowers <c>??</c>) is an
/// <b>assignment expression statement</b> (<see cref="ExpressionStatementSyntax"/>).
/// </para>
///
/// <para>
/// <b>Root cause (now fixed):</b> the C35 decision table in
/// <see cref="ChainSegmentAnalyzer.ApplyDecisions"/> only allowed multi-segment
/// value lowering for <see cref="LocalDeclarationStatementSyntax"/> and
/// <see cref="ReturnStatementSyntax"/> anchors. Any other statement anchor fell
/// through to <see cref="NullConditionalDiagnosticCodes.CS2J0557"/> and the strict
/// family-abort gate then aborted the file without generating any Java.
/// </para>
///
/// <para>
/// <b>Fix:</b> extend the anchor allow-list to include
/// <see cref="ExpressionStatementSyntax"/>, and teach
/// <see cref="NullConditionalRewriter.RebuildValueAnchorStatement"/> to replace
/// the chain root with the nested ternary inside the statement's expression
/// (preserving assignments <c>Result = a?.B?.C?.Name</c>).
/// </para>
///
/// <para>
/// These tests verify that the multi-segment value chain is correctly lowered
/// (not preserved with CS2J0557) for assignment-expression-statement anchors.
/// </para>
/// </summary>
public sealed class NullConditionalMultiSegmentValueInAssignmentTests
{
    /// <summary>Minimal shape: 3-segment chain RHS of an assignment statement (= ExpressionStatement anchor).</summary>
    private const string MultiSegmentAssignmentSource = """
        #nullable enable

        public sealed class A {
            public B? B;
        }
        public sealed class B {
            public C? C;
        }
        public sealed class C {
            public string Name = "";
        }
        public sealed class Test {
            public string? Result;
            public void Run(A? a) {
                Result = a?.B?.C?.Name;
            }
        }
        """;

    /// <summary>
    /// Variation: assignment statement inside a nested block (if-statement body).
    /// The chain root is still directly owned by the ExpressionStatement; the
    /// if-statement is its grand-parent. Verifies ExpressionStatement anchor
    /// resolution works at arbitrary nesting depth.
    /// </summary>
    private const string MultiSegmentAssignmentInBlockSource = """
        #nullable enable

        public sealed class A {
            public B? B;
        }
        public sealed class B {
            public C? C;
        }
        public sealed class C {
            public string Name = "";
        }
        public sealed class Test {
            public string? Result;
            public void Run(A? a, bool cond) {
                if (cond) {
                    Result = a?.B?.C?.Name;
                }
            }
        }
        """;

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Array).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static ExpressionDesugarResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c35-ms-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        var result = new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                NullConditional = new NullConditionalOptions { Strict = true },
            });
        return result;
    }

    [Fact]
    public void MultiSegmentValue_On_AssignmentStatement_Anchor_Is_Lowered()
    {
        var result = Run(MultiSegmentAssignmentSource);

        // The CS2J0557 error must NOT occur anymore: the ExpressionStatement
        // anchor is now in the allow-list and the chain is lowered.
        var cs2J0557 = result.Diagnostics
            .Where(d => d.Code == NullConditionalDiagnosticCodes.CS2J0557)
            .ToList();
        Assert.Empty(cs2J0557);

        // The rewriter must have transformed the file.
        Assert.True(result.Changed);

        // The output must no longer contain the unlifted multi-segment chain
        // (no more two consecutive ?. on the same expression path).
        Assert.DoesNotContain("?.", result.OutputCode);

        // The lowered form should contain guarded-access patterns: either an
        // if-statement wrapping the assignment, or a nested ternary. Both
        // signal that the chain was lowered (not preserved as-is).
        var hasIfGuard = result.OutputCode.Contains("if (") &&
            result.OutputCode.Contains("!= null)");
        var hasTernary = result.OutputCode.Contains("?")
            && result.OutputCode.Contains(":");
        Assert.True(hasIfGuard || hasTernary,
            $"Expected lowered guard or ternary in output.\nOutput:\n{result.OutputCode}");
    }

    [Fact]
    public void MultiSegmentValue_In_IfBlock_Assignment_Is_Lowered()
    {
        var result = Run(MultiSegmentAssignmentInBlockSource);

        // Same expectations as the top-level variant: the assignment anchor
        // inside an if-block must also be lowered without any CS2J0557 error.
        var cs2J0557 = result.Diagnostics
            .Where(d => d.Code == NullConditionalDiagnosticCodes.CS2J0557)
            .ToList();
        Assert.Empty(cs2J0557);
        Assert.True(result.Changed);
        Assert.DoesNotContain("?.", result.OutputCode);

        var hasIfGuard = result.OutputCode.Contains("if (") &&
            result.OutputCode.Contains("!= null)");
        var hasTernary = result.OutputCode.Contains("?")
            && result.OutputCode.Contains(":");
        Assert.True(hasIfGuard || hasTernary,
            $"Expected lowered guard or ternary in output.\nOutput:\n{result.OutputCode}");
    }
}
