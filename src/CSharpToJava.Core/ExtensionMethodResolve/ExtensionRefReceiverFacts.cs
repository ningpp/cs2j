using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// EM-REF defensiveness (CS2J1204): a <c>this ref</c> / <c>this scoped ref</c>
/// receiver can only be rewritten as <c>Host.M(ref r, ...)</c> when the source
/// receiver expression is a provably writable lvalue the compiler itself
/// accepted as a ref argument. <c>this in</c> accepts any expression (rvalues
/// included), so it is always provable. Selection is still driven by the bound
/// symbol facts (ReducedFrom first parameter RefKind/ScopedKind); this is the
/// argument-side proof only — syntax shape never selects the site.
/// </summary>
internal static class ExtensionRefReceiverFacts
{
    /// <summary>
    /// True when <paramref name="receiver"/> can be proven from syntax +
    /// symbols to be a legal <c>ref</c> argument (writable variable).
    /// </summary>
    public static bool IsProvableWritableRefReceiver(ExpressionSyntax receiver, SemanticModel model)
    {
        switch (receiver)
        {
            // locals/parameters are always writable variables (unless the
            // parameter itself is a ref-readonly/in parameter).
            case IdentifierNameSyntax identifier:
            {
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                return symbol switch
                {
                    ILocalSymbol local => local.RefKind != RefKind.In,
                    IParameterSymbol parameter => parameter.RefKind is RefKind.None or RefKind.Ref or RefKind.Out,
                    IFieldSymbol field => IsWritableField(field),
                    _ => false,
                };
            }

            // this.field: writable when the field itself is writable.
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } memberAccess:
                return model.GetSymbolInfo(memberAccess).Symbol is IFieldSymbol field2
                       && IsWritableField(field2);

            // instance fields: obj.field — the receiver must be a variable too,
            // but the bound source call proves it compiled; accept the same
            // provable receiver recursively.
            case MemberAccessExpressionSyntax memberAccess2:
                return model.GetSymbolInfo(memberAccess2).Symbol is IFieldSymbol field3
                       && IsWritableField(field3)
                       && IsReceiverOfMutableLocatable(memberAccess2.Expression, model);

            // array[index] is always a ref-writable variable.
            case ElementAccessExpressionSyntax elementAccess:
            {
                var type = model.GetTypeInfo(elementAccess.Expression).Type;
                if (type is IArrayTypeSymbol)
                {
                    return true;
                }

                // indexer: legal only when it returns by ref (writable).
                if (model.GetSymbolInfo(elementAccess).Symbol is IPropertySymbol { ReturnsByRef: true })
                {
                    return true;
                }

                return false;
            }

            // out var declarations embedded as the receiver are variables.
            case DeclarationExpressionSyntax:
                return true;

            default:
                return false;
        }
    }

    private static bool IsWritableField(IFieldSymbol field)
        => !field.IsReadOnly;

    /// <summary>
    /// Recursive receiver proof for <c>a.b.field</c> chains: each object
    /// expression must itself be an lvalue the source bound as legal.
    /// </summary>
    private static bool IsReceiverOfMutableLocatable(ExpressionSyntax expression, SemanticModel model)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                var symbol = model.GetSymbolInfo(identifier).Symbol;
                return symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol;
            case ThisExpressionSyntax:
                return true;
            case MemberAccessExpressionSyntax memberAccess:
                return model.GetSymbolInfo(memberAccess).Symbol is IFieldSymbol
                       && IsReceiverOfMutableLocatable(memberAccess.Expression, model);
            case ElementAccessExpressionSyntax elementAccess:
                return model.GetTypeInfo(elementAccess.Expression).Type is IArrayTypeSymbol
                       || IsReceiverOfMutableLocatable(elementAccess.Expression, model);
            default:
                return false;
        }
    }
}
