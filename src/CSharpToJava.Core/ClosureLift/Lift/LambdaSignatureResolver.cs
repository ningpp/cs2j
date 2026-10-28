using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>One rendered named-method parameter (design D12/D23).</summary>
internal sealed record ResolvedLambdaParameter(
    string TypeDisplay,
    string Name);

/// <summary>
/// Symbol-frozen signature of one lambda (design D12): the target delegate's
/// InvokeMethod supplies the explicit parameter types and the return type —
/// never inferred from the body. <see cref="ReturnTypeDisplay"/> is null for
/// void delegates.
/// </summary>
internal sealed record ResolvedLambdaSignature(
    string DelegateTypeDisplay,
    IReadOnlyList<ResolvedLambdaParameter> Parameters,
    string? ReturnTypeDisplay);

/// <summary>
/// C45 M1 signature resolution (design §4: Lift/LambdaSignatureResolver).
/// Resolves <c>SemanticModel.GetTypeInfo(lambda).ConvertedType</c> to a
/// delegate's InvokeMethod (D8) and renders the named-method signature.
/// Failure modes freeze the lambda (and thereby the file, per stage policy):
///   - ConvertedType missing / ErrorType / not a delegate → CS2J4509 (D12);
///   - ref/out/in parameter or ref return → CS2J4501 (D6②/D23).
/// </summary>
internal static class LambdaSignatureResolver
{
    public static ResolvedLambdaSignature? Resolve(
        SemanticModel model,
        LambdaExpressionSyntax lambda,
        ClosureLiftOptions options,
        string? memberName,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        var convertedType = model.GetTypeInfo(lambda).ConvertedType;
        if (convertedType is null)
        {
            // Generic-inference argument positions (D20/E16 territory, e.g.
            // `list.ConvertAll(x => …)`) report no conversion target for valid
            // code. Nothing can be synthesized — the lambda stays in place and
            // the residual counter owns it; no row, no freeze.
            return null;
        }

        if (convertedType is not INamedTypeSymbol delegateType
            || delegateType.TypeKind != TypeKind.Delegate
            || delegateType.DelegateInvokeMethod is not { } invoke)
        {
            diagnostics.Add(new ClosureLiftDiagnostic(
                ClosureLiftSeverity.Error,
                ClosureLiftCodes.UnresolvableDelegateType,
                $"Lambda target type '{convertedType.ToDisplayString()}' is not a resolvable delegate; " +
                "the named-method signature cannot be synthesized (D8/D12).",
                null,
                lambda.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                memberName));
            return null;
        }

        if (ContainsErrorType(delegateType))
        {
            // A missing reference leaves the inferred delegate with error type
            // arguments (`Func<?, bool>`, rendered as `Func<, bool>`). Nothing
            // compilable can be synthesized — same residual discipline as the
            // missing-ConvertedType case (no row, no freeze).
            return null;
        }

        var parameters = new List<ResolvedLambdaParameter>();
        foreach (var parameter in invoke.Parameters)
        {
            if (parameter.RefKind != RefKind.None || parameter.Type is IPointerTypeSymbol)
            {
                diagnostics.Add(new ClosureLiftDiagnostic(
                    options.Strict ? ClosureLiftSeverity.Error : ClosureLiftSeverity.Warning,
                    ClosureLiftCodes.RefLambdaParameter,
                    $"Delegate parameter '{parameter.Name}' has ref kind {parameter.RefKind}; ref lambda " +
                    "parameters are a C01 ByRef<T> follow-up (D6②).",
                    null,
                    lambda.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    memberName));
                return null;
            }

            parameters.Add(new ResolvedLambdaParameter(
                parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                parameter.Name));
        }

        if (invoke.ReturnsByRef || invoke.ReturnsByRefReadonly)
        {
            diagnostics.Add(new ClosureLiftDiagnostic(
                options.Strict ? ClosureLiftSeverity.Error : ClosureLiftSeverity.Warning,
                ClosureLiftCodes.RefLambdaParameter,
                "Delegate returns by ref; ref-returning lambda targets are a C01 ByRef<T> follow-up (D23).",
                null,
                lambda.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                memberName));
            return null;
        }

        var returnType = invoke.ReturnsVoid
            ? null
            : invoke.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new ResolvedLambdaSignature(
            delegateType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            parameters,
            returnType);
    }

    /// <summary>
    /// True when the type or any of its type arguments is an error type: the
    /// semantic model is missing a reference, so the symbol renders an empty or
    /// `?` type that would be emitted as invalid syntax (CS1031/CS1001).
    /// </summary>
    private static bool ContainsErrorType(ITypeSymbol? type) => type switch
    {
        null => false,
        _ when type.TypeKind == TypeKind.Error => true,
        IArrayTypeSymbol array => ContainsErrorType(array.ElementType),
        IPointerTypeSymbol pointer => ContainsErrorType(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsErrorType),
        _ => false,
    };
}
