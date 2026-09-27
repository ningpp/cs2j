using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.EnumPreprocessor;

/// <summary>
/// C# enum bitwise preprocessor public entry point. Rewrites enum bitwise
/// operations (|, &amp;, ^, ~), compound assignments (|=, &amp;=, ^=), and
/// HasFlag calls into explicit underlying-type integer operations + (E) casts.
/// Clean files are byte-level preserved. Rewritten output is valid C#.
/// </summary>
public sealed class EnumPreprocessor
{
    public EnumPreprocessorResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        EnumPreprocessorOptions? options = null)
    {
        options ??= new EnumPreprocessorOptions();
        var stats = new EnumPreprocessorStatistics { FilesScanned = 1 };
        var diagnostics = new List<EnumPreprocessorDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        // Dirty check: no bitwise/HasFlag nodes → byte-level copy
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new EnumPreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        // Pass: rewrite expressions
        var rewriter = new EnumSyntaxRewriter(
            semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (rewriter.Rewrites == 0)
        {
            // Dirty syntax but no semantic enum bitwise ops (e.g. int bitwise)
            stats.FilesDirtyNoRewrite = 1;
            return new EnumPreprocessorResult(sourceCode, false, diagnostics, stats);
        }

        // Format
        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        // Post-check: semantic-level residual enum ops
        var residualCount = CountResidualEnumOps(code, semanticModel, root);
        stats.ResidualCount = residualCount;

        if (residualCount > 0)
        {
            var severity = options.Strict
                ? EnumPreprocessorSeverity.Error
                : EnumPreprocessorSeverity.Warning;
            diagnostics.Add(new EnumPreprocessorDiagnostic(
                severity,
                $"{residualCount} residual enum bitwise/HasFlag node(s) after preprocessing."));
        }

        return new EnumPreprocessorResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Dirty check: syntax tree contains any bitwise operators, compound
    /// assignments, prefix ~, or HasFlag calls. This is intentionally a
    /// superset of enum operations (int bitwise also matches).
    /// </summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            (n is BinaryExpressionSyntax b &&
                (b.IsKind(SyntaxKind.BitwiseOrExpression)
                 || b.IsKind(SyntaxKind.BitwiseAndExpression)
                 || b.IsKind(SyntaxKind.ExclusiveOrExpression)))
            || (n is AssignmentExpressionSyntax a &&
                (a.IsKind(SyntaxKind.OrAssignmentExpression)
                 || a.IsKind(SyntaxKind.AndAssignmentExpression)
                 || a.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression)))
            || (n is PrefixUnaryExpressionSyntax p
                && p.IsKind(SyntaxKind.BitwiseNotExpression))
            || (n is InvocationExpressionSyntax i && IsHasFlagCall(i)));

    private static bool IsHasFlagCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.ValueText == "HasFlag"
            && invocation.ArgumentList.Arguments.Count == 1)
            return true;
        return false;
    }

    /// <summary>
    /// Post-check: count residual enum bitwise/HasFlag nodes in the output.
    /// Uses semantic analysis on the rewritten tree to distinguish enum
    /// operations from int operations.
    /// </summary>
    private static int CountResidualEnumOps(
        string code,
        SemanticModel? originalModel,
        CompilationUnitSyntax originalRoot)
    {
        if (originalModel == null)
            return 0;

        // Parse the output and create a new compilation to get a semantic model
        var newTree = CSharpSyntaxTree.ParseText(code, path: "enum-output.cs");
        var newCompilation = originalModel.Compilation.ReplaceSyntaxTree(
            originalModel.SyntaxTree, newTree);
        var newModel = newCompilation.GetSemanticModel(newTree);
        var newRoot = (CompilationUnitSyntax)newTree.GetRoot();

        int count = 0;

        foreach (var node in newRoot.DescendantNodes())
        {
            if (node is BinaryExpressionSyntax b
                && (b.IsKind(SyntaxKind.BitwiseOrExpression)
                    || b.IsKind(SyntaxKind.BitwiseAndExpression)
                    || b.IsKind(SyntaxKind.ExclusiveOrExpression)))
            {
                var leftType = newModel.GetTypeInfo(b.Left).Type;
                var rightType = newModel.GetTypeInfo(b.Right).Type;
                if (IsEnumType(leftType) || IsEnumType(rightType))
                    count++;
            }
            else if (node is AssignmentExpressionSyntax a
                && (a.IsKind(SyntaxKind.OrAssignmentExpression)
                    || a.IsKind(SyntaxKind.AndAssignmentExpression)
                    || a.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression)))
            {
                var leftType = newModel.GetTypeInfo(a.Left).Type;
                if (IsEnumType(leftType))
                    count++;
            }
            else if (node is PrefixUnaryExpressionSyntax p
                && p.IsKind(SyntaxKind.BitwiseNotExpression))
            {
                var operandType = newModel.GetTypeInfo(p.Operand).Type;
                if (IsEnumType(operandType))
                    count++;
            }
            else if (node is InvocationExpressionSyntax i && IsHasFlagCall(i))
            {
                if (i.Expression is MemberAccessExpressionSyntax ma)
                {
                    var receiverType = newModel.GetTypeInfo(ma.Expression).Type;
                    if (IsEnumType(receiverType))
                        count++;
                }
            }
        }

        return count;
    }

    private static bool IsEnumType(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type is INamedTypeSymbol nts)
        {
            if (nts.TypeKind == TypeKind.Enum) return true;
            if (nts.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
                && nts.TypeArguments.Length == 1
                && nts.TypeArguments[0] is INamedTypeSymbol inner
                && inner.TypeKind == TypeKind.Enum)
                return true;
        }
        return false;
    }
}
