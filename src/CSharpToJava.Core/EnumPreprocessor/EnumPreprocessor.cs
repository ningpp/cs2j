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
        var residuals = CountResidualEnumOps(code, semanticModel, root);
        stats.ResidualCount = residuals.Count;

        if (residuals.Count > 0)
        {
            var severity = options.Strict
                ? EnumPreprocessorSeverity.Error
                : EnumPreprocessorSeverity.Warning;
            var sites = string.Join("; ", residuals.Take(12));
            diagnostics.Add(new EnumPreprocessorDiagnostic(
                severity,
                $"{residuals.Count} residual enum bitwise/HasFlag node(s) after preprocessing. First site(s): {sites}"));
        }

        return new EnumPreprocessorResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Dirty check: syntax tree contains any bitwise operators, compound
    /// assignments, prefix ~, HasFlag calls, or enum relational/arithmetic
    /// candidate shapes (equality/relational/add/subtract binaries). The
    /// comparison/arithmetic shapes are a syntax-level superset (int sites
    /// match too) — the rewriter gates semantically and the file falls through
    /// to dirty-no-rewrite (byte-level preserved) when nothing enum-typed hits.
    /// This is intentionally a superset of enum operations.
    /// </summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(n =>
            (n is BinaryExpressionSyntax b &&
                (b.IsKind(SyntaxKind.BitwiseOrExpression)
                 || b.IsKind(SyntaxKind.BitwiseAndExpression)
                 || b.IsKind(SyntaxKind.ExclusiveOrExpression)
                 || b.IsKind(SyntaxKind.EqualsExpression)
                 || b.IsKind(SyntaxKind.NotEqualsExpression)
                 || b.IsKind(SyntaxKind.LessThanExpression)
                 || b.IsKind(SyntaxKind.LessThanOrEqualExpression)
                 || b.IsKind(SyntaxKind.GreaterThanExpression)
                 || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression)
                 || b.IsKind(SyntaxKind.AddExpression)
                 || b.IsKind(SyntaxKind.SubtractExpression)))
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
    /// Post-check: enumerate residual enum bitwise/HasFlag nodes in the output.
    /// Uses semantic analysis on the rewritten tree to distinguish enum
    /// operations from int operations. Each entry carries the output-line
    /// location and a trimmed snippet so strict-gate failures are actionable.
    /// </summary>
    private static List<string> CountResidualEnumOps(
        string code,
        SemanticModel? originalModel,
        CompilationUnitSyntax originalRoot)
    {
        var residuals = new List<string>();
        if (originalModel == null)
            return residuals;

        // Parse the output and create a new compilation to get a semantic model
        var newTree = CSharpSyntaxTree.ParseText(code, path: "enum-output.cs");
        var newCompilation = originalModel.Compilation.ReplaceSyntaxTree(
            originalModel.SyntaxTree, newTree);
        var newModel = newCompilation.GetSemanticModel(newTree);
        var newRoot = (CompilationUnitSyntax)newTree.GetRoot();

        static string Describe(SyntaxNode node, SemanticModel model, string kind)
        {
            var lineSpan = node.SyntaxTree.GetLineSpan(node.Span);
            var text = node.ToString().Replace('\n', ' ').Replace('\r', ' ');
            while (text.Contains("  "))
                text = text.Replace("  ", " ");
            if (text.Length > 100)
                text = text[..100] + "...";
            return $"(line {lineSpan.StartLinePosition.Line + 1}, {kind}) {text.Trim()}";
        }

        foreach (var node in newRoot.DescendantNodes())
        {
            if (node is BinaryExpressionSyntax b
                && (b.IsKind(SyntaxKind.BitwiseOrExpression)
                    || b.IsKind(SyntaxKind.BitwiseAndExpression)
                    || b.IsKind(SyntaxKind.ExclusiveOrExpression)))
            {
                // The node's OWN type identifies the chosen operator: inside
                // enum member initializers the operands look enum-typed while
                // the selected operator is the underlying integer one.
                if (IsEnumType(newModel.GetTypeInfo(b).Type))
                    residuals.Add(Describe(b, newModel, "binary"));
            }
            else if (node is AssignmentExpressionSyntax a
                && (a.IsKind(SyntaxKind.OrAssignmentExpression)
                    || a.IsKind(SyntaxKind.AndAssignmentExpression)
                    || a.IsKind(SyntaxKind.ExclusiveOrAssignmentExpression)))
            {
                var leftType = newModel.GetTypeInfo(a.Left).Type;
                if (IsEnumType(leftType))
                    residuals.Add(Describe(a, newModel, "compound"));
            }
            else if (node is PrefixUnaryExpressionSyntax p
                && p.IsKind(SyntaxKind.BitwiseNotExpression))
            {
                if (IsEnumType(newModel.GetTypeInfo(p).Type))
                    residuals.Add(Describe(p, newModel, "~"));
            }
            else if (node is InvocationExpressionSyntax i && IsHasFlagCall(i))
            {
                if (i.Expression is MemberAccessExpressionSyntax ma)
                {
                    var receiverType = newModel.GetTypeInfo(ma.Expression).Type;
                    if (IsEnumType(receiverType))
                        residuals.Add(Describe(i, newModel, "HasFlag"));
                }
            }
        }

        return residuals;
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
