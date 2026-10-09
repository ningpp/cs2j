using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>Result of <see cref="HandlerBindingAnalyzer.Classify"/>.</summary>
internal sealed record HandlerBinding(
    InterpolationBindingKind Kind,
    ITypeSymbol? ConvertedType,
    string? ConstantValue,
    string? HandlerTypeName)
{
    public static HandlerBinding Unknown(ITypeSymbol? convertedType)
        => new(InterpolationBindingKind.Unknown, convertedType, null, null);
}

/// <summary>
/// IE-1 hard gate G0 (design §4.3): semantic five-way classification of an
/// interpolation site. Never guesses from <c>$</c> text — every decision uses
/// SemanticModel/IOperation. Roslyn 5.0 shapes were locked by probe tests:
/// <list type="bullet">
/// <item>default string: <c>GetOperation</c> is IInterpolatedStringOperation
/// with text/interpolation parts and NO handler-creation ancestor,
/// ConvertedType is System.String;</item>
/// <item>custom handler (incl. an explicit DefaultInterpolatedStringHandler
/// parameter): the immediate parent operation is
/// IInterpolatedStringHandlerCreationOperation and/or ConvertedType carries
/// [System.Runtime.CompilerServices.InterpolatedStringHandler];</item>
/// <item>FormattableString: ConvertedType is System.FormattableString (both
/// variable and FormattableString.Invariant argument), parent conversion;</item>
/// <item>GetConstantValue(node).HasValue is ALSO true for all-text sites
/// bound to a handler or FormattableString — therefore the handler and
/// FormattableString gates must run BEFORE the constant short-circuit
/// (folding there would emit a string literal into a handler/FormattableString
/// slot and break compilation/observable behavior).</item>
/// </list>
/// </summary>
internal static class HandlerBindingAnalyzer
{
    public const string HandlerAttributeFullName =
        "System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute";

    private const string FormattableStringMetadataName = "System.FormattableString";
    private const string FormattableMetadataName = "System.IFormattable";

    public static HandlerBinding Classify(
        SemanticModel model,
        InterpolatedStringExpressionSyntax node)
    {
        var typeInfo = model.GetTypeInfo(node);
        var convertedType = typeInfo.ConvertedType;
        var operation = model.GetOperation(node);

        // ---- G0-a: handler creation directly owning THIS interpolation ----
        // Probe-locked: default/FormattableString bindings never have a
        // creation op between the site and its enclosing interpolation. The
        // walk stops at the first enclosing IInterpolatedStringOperation so
        // nested inner interpolations and handler-constructor-argument
        // interpolations are not mistaken for the handler-bound site itself.
        if (convertedType != null && IsOwnedByHandlerCreation(operation))
        {
            return new HandlerBinding(
                InterpolationBindingKind.CustomHandler,
                convertedType,
                null,
                convertedType.ToDisplayString());
        }

        // ---- G0-b: converted type itself is an attributed handler ----
        if (convertedType != null
            && convertedType.SpecialType != SpecialType.System_String
            && HasHandlerAttribute(convertedType))
        {
            return new HandlerBinding(
                InterpolationBindingKind.CustomHandler,
                convertedType,
                null,
                convertedType.ToDisplayString());
        }

        // ---- FormattableString / IFormattable explicit runtime shape ----
        if (IsFormattableStringShape(convertedType))
        {
            return new HandlerBinding(
                InterpolationBindingKind.FormattableString,
                convertedType,
                null,
                null);
        }

        // ---- IE-FOLD constant (safe: non-string targets already handled) ----
        var constant = model.GetConstantValue(node);
        if (constant.HasValue && constant.Value is string constantText)
        {
            return new HandlerBinding(
                InterpolationBindingKind.Constant,
                convertedType,
                constantText,
                null);
        }

        // ---- Default string binding ----
        if (convertedType != null
            && convertedType.SpecialType == SpecialType.System_String
            && !IsOwnedByHandlerCreation(operation))
        {
            return new HandlerBinding(
                InterpolationBindingKind.DefaultString,
                convertedType,
                null,
                null);
        }

        // ---- Boxed string binding (natural string -> System.Object) ----
        // An interpolation in an object slot (object local/field, object[]
        // element, params object[] argument, SR.Format(resource, $"..."))
        // undergoes the ordinary interpolation->string conversion followed by
        // an implicit boxing conversion; handlers bind by parameter TYPE and
        // can never reach an object slot, so expansion semantics are identical
        // to DefaultString — the boxing stays on the enclosing conversion.
        // dynamic is excluded: its natural Type is dynamic, never string.
        if (convertedType is { SpecialType: SpecialType.System_Object, TypeKind: not TypeKind.Dynamic }
            && typeInfo.Type is { SpecialType: SpecialType.System_String })
        {
            return new HandlerBinding(
                InterpolationBindingKind.DefaultString,
                convertedType,
                null,
                null);
        }

        // ---- Undecidable (error type / missing metadata) ----
        return HandlerBinding.Unknown(convertedType);
    }

    /// <summary>
    /// True when an <see cref="IInterpolatedStringHandlerCreationOperation"/>
    /// owns the site: walking upward from the site's own interpolated-string
    /// operation, a creation op must be reached BEFORE any enclosing
    /// interpolated-string operation. (A nested inner interpolation whose
    /// nearest enclosing interpolation binds to a handler is part of the
    /// outer kept subtree and is dispositioned by the rule's enclosure logic
    /// instead.)
    /// </summary>
    private static bool IsOwnedByHandlerCreation(IOperation? operation)
    {
        for (var current = operation?.Parent;
             current != null;
             current = current.Parent)
        {
            if (current is IInterpolatedStringHandlerCreationOperation)
                return true;
            if (current is IInterpolatedStringOperation)
                return false;
        }

        return false;
    }

    private static bool HasHandlerAttribute(ITypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (string.Equals(
                    attribute.AttributeClass?.ToDisplayString(),
                    HandlerAttributeFullName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFormattableStringShape(ITypeSymbol? type)
    {
        if (type == null)
            return false;

        if (type is { TypeKind: TypeKind.Error })
            return false;

        foreach (var metadataName in new[] { FormattableStringMetadataName, FormattableMetadataName })
        {
            if (string.Equals(
                    type.OriginalDefinition.ToDisplayString(),
                    metadataName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
