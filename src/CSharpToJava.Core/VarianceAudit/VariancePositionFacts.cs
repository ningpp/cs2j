using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>
/// Position-direction facts for the variance-audit capability (design D3①/D7/D12):
/// maps a syntactic type position to its flow (Producer/Consumer/Both), composes
/// nesting directions and detects baseline compile errors.
/// </summary>
public static class VariancePositionFacts
{
    /// <summary>
    /// Flow of a member-signature type position: return/out-parameter/get-only = Producer,
    /// value/in-parameter/set-only = Consumer, ref-parameter/get+set property = Both (D3①).
    /// Unresolvable or data-flow-free positions (fields, locals, base lists, casts, ...)
    /// are conservatively Both; batch 3 refines them with scope member-access summaries.
    /// </summary>
    public static UseFlow FlowForPosition(TypeSyntax type)
    {
        switch (type.Parent)
        {
            case ParameterSyntax parameter:
                return FlowForParameter(parameter);
            case MethodDeclarationSyntax method when method.ReturnType == type:
            case LocalFunctionStatementSyntax local when local.ReturnType == type:
            case DelegateDeclarationSyntax del when del.ReturnType == type:
                return UseFlow.Producer;
            case PropertyDeclarationSyntax property when property.Type == type:
                return FlowForAccessors(property.AccessorList, property.ExpressionBody != null);
            case IndexerDeclarationSyntax indexer when indexer.Type == type:
                return FlowForAccessors(indexer.AccessorList, indexer.ExpressionBody != null);
            default:
                return UseFlow.Both;
        }
    }

    private static UseFlow FlowForParameter(ParameterSyntax parameter)
    {
        foreach (var modifier in parameter.Modifiers)
        {
            if (modifier.IsKind(SyntaxKind.OutKeyword))
            {
                return UseFlow.Producer;
            }

            if (modifier.IsKind(SyntaxKind.RefKeyword))
            {
                return UseFlow.Both;
            }

            if (modifier.IsKind(SyntaxKind.InKeyword))
            {
                return UseFlow.Consumer;
            }
        }

        return UseFlow.Consumer;
    }

    private static UseFlow FlowForAccessors(AccessorListSyntax? accessors, bool expressionBody)
    {
        var hasGet = expressionBody;
        var hasSet = false;
        if (accessors != null)
        {
            foreach (var accessor in accessors.Accessors)
            {
                if (accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                {
                    hasGet = true;
                }
                else if (accessor.Keyword.IsKind(SyntaxKind.SetKeyword) || accessor.Keyword.IsKind(SyntaxKind.InitKeyword))
                {
                    hasSet = true;
                }
            }
        }

        if (hasGet && hasSet)
        {
            return UseFlow.Both;
        }

        if (hasGet)
        {
            return UseFlow.Producer;
        }

        return hasSet ? UseFlow.Consumer : UseFlow.Both;
    }

    /// <summary>
    /// Composes an outer position flow with a declaration-site variance (D7):
    /// co∘co=co, contra∘contra=co, co∘contra=contra; any Both stays Both.
    /// </summary>
    public static UseFlow Compose(UseFlow position, Microsoft.CodeAnalysis.VarianceKind declaration)
        => Compose(position, ToFlow(declaration));

    public static UseFlow Compose(UseFlow outer, UseFlow inner)
    {
        if (outer == UseFlow.Both || inner == UseFlow.Both)
        {
            return UseFlow.Both;
        }

        return outer == inner ? UseFlow.Producer : UseFlow.Consumer;
    }

    /// <summary>Combines two flows from sibling branches (multi-parameter variant types).</summary>
    public static UseFlow Combine(UseFlow left, UseFlow right)
    {
        if (left == UseFlow.Both || right == UseFlow.Both)
        {
            return UseFlow.Both;
        }

        return left == right ? left : UseFlow.Both;
    }

    public static UseFlow ToFlow(Microsoft.CodeAnalysis.VarianceKind variance) => variance switch
    {
        Microsoft.CodeAnalysis.VarianceKind.Out => UseFlow.Producer,
        Microsoft.CodeAnalysis.VarianceKind.In => UseFlow.Consumer,
        _ => UseFlow.Both,
    };

    public static VarianceKind ToKind(Microsoft.CodeAnalysis.VarianceKind variance) => variance switch
    {
        Microsoft.CodeAnalysis.VarianceKind.In => VarianceKind.In,
        _ => VarianceKind.Out,
    };

    public static string FlowWord(UseFlow flow) => flow switch
    {
        UseFlow.Producer => "Producer",
        UseFlow.Consumer => "Consumer",
        _ => "Both",
    };

    public static string FlowSymbol(UseFlow flow) => flow switch
    {
        UseFlow.Producer => "co",
        UseFlow.Consumer => "contra",
        _ => "both",
    };

    public static string VarianceAnnotation(Microsoft.CodeAnalysis.VarianceKind variance) => variance switch
    {
        Microsoft.CodeAnalysis.VarianceKind.Out => "out",
        Microsoft.CodeAnalysis.VarianceKind.In => "in",
        _ => "invariant",
    };

    /// <summary>
    /// D12: a file whose baseline tree already has compile errors (class-level out/in,
    /// illegal variance positions) must not accumulate safety diagnostics. Uses both the
    /// syntax-tree diagnostics and the semantic-model diagnostics.
    /// </summary>
    public static bool HasBaselineErrors(SemanticModel model)
    {
        foreach (var diagnostic in model.SyntaxTree.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                return true;
            }
        }

        foreach (var diagnostic in model.GetDiagnostics())
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                return true;
            }
        }

        return false;
    }
}