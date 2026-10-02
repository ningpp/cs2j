using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Which emitted shape a spread (<c>..e</c>) was lowered to. Drives the
/// design-§6 spread statistics buckets.
/// </summary>
internal enum SpreadExpansion
{
    /// <summary><c>tmp.AddRange(e)</c> — the source expression appears once.</summary>
    AddRange,

    /// <summary><c>foreach (var v in span) tmp.Add(v);</c> for Span/ReadOnlySpan sources.</summary>
    SpanForeach,

    /// <summary>foreach-Add for string (char) and IEnumerable sources without AddRange.</summary>
    EnumerableForeach,
}

/// <summary>
/// CE-SPREAD (design §2.3): classifies a spread source by the semantic type of
/// <c>spread.Expression</c> itself (per M0 probe §6.1 the IOperation operand
/// carries the natural type with no conversion wrapper, so classification must
/// not read a conversion node) and emits the single-evaluation
/// AddRange / foreach-Add statement sequence. The source expression appears
/// exactly once (inlined) and source order is preserved by emission order;
/// Concat/Array.Resize/LINQ shapes are forbidden; string sources always use
/// foreach-Add (List&lt;char&gt;.AddRange(string) does not compile — string is
/// not <c>IEnumerable&lt;char&gt;</c>).
/// </summary>
internal static class SpreadExpander
{
    /// <summary>
    /// Classifies a spread source. Order matters: array, Span/ReadOnlySpan,
    /// string (char elements; string is non-generic IEnumerable too), generic
    /// or non-generic IEnumerable — otherwise unknown (CS2J0576).
    /// </summary>
    public static SpreadSourceKind ClassifySource(ITypeSymbol? sourceType)
    {
        if (sourceType == null
            || sourceType.TypeKind == TypeKind.Error
            || sourceType is IErrorTypeSymbol)
        {
            return SpreadSourceKind.Unknown;
        }

        if (sourceType is IArrayTypeSymbol)
            return SpreadSourceKind.Array;

        if (TargetTypeResolver.IsSpanLike(sourceType))
            return SpreadSourceKind.Span;

        if (sourceType.SpecialType == SpecialType.System_String)
            return SpreadSourceKind.StringChars;

        if (ImplementsEnumerable(sourceType))
            return SpreadSourceKind.Enumerable;

        return SpreadSourceKind.Unknown;
    }

    /// <summary>
    /// Element type of a classified spread source: array element, Span type
    /// argument, char for string, first <c>IEnumerable&lt;T&gt;</c> argument
    /// for custom enumerable types. Null only for unknown/non-generic sources.
    /// </summary>
    public static ITypeSymbol? TryGetElementType(
        ITypeSymbol? sourceType, Compilation compilation)
    {
        if (sourceType is IArrayTypeSymbol array)
            return array.ElementType;

        if (sourceType != null && sourceType.SpecialType == SpecialType.System_String)
            return compilation.GetSpecialType(SpecialType.System_Char);

        if (sourceType is INamedTypeSymbol named
            && TargetTypeResolver.IsSpanLike(named)
            && named.TypeArguments is { Length: 1 } spanArgs)
        {
            return spanArgs[0];
        }

        if (sourceType != null)
        {
            foreach (var iface in sourceType.AllInterfaces
                         .Prepend(sourceType as INamedTypeSymbol))
            {
                if (iface is { IsGenericType: true }
                    && iface.OriginalDefinition.ToDisplayString()
                        == "System.Collections.Generic.IEnumerable<T>"
                    && iface.TypeArguments is { Length: 1 } enumArgs)
                {
                    return enumArgs[0];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="target"/> exposes an accessible
    /// single-parameter AddRange whose parameter type implicitly accepts
    /// <paramref name="sourceType"/> (e.g. List&lt;T&gt;.AddRange on int[] /
    /// IEnumerable&lt;T&gt; inputs). Symbol/Conversion based — never text.
    /// </summary>
    public static bool HasCompatibleAddRange(
        SemanticModel model,
        INamedTypeSymbol target,
        ITypeSymbol sourceType)
    {
        var compilation = (CSharpCompilation)model.Compilation;
        var currentAssembly = compilation.Assembly;

        foreach (var method in TargetTypeResolver.EnumerateMethods(target, "AddRange"))
        {
            if (method.IsStatic
                || method.IsGenericMethod
                || method.Parameters.Length != 1
                || !TargetTypeResolver.IsAccessible(method, currentAssembly))
            {
                continue;
            }

            if (compilation.ClassifyConversion(
                    sourceType, method.Parameters[0].Type).IsImplicit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Emits the single-evaluation expansion statements for one spread.
    /// </summary>
    /// <param name="kind">classified source kind.</param>
    /// <param name="source">visited (already-lowered) source expression; inlined once.</param>
    /// <param name="tempReceiver">the __ed_n collection temp.</param>
    /// <param name="loopVariable">
    /// Analyze-phase pre-allocated foreach variable (CE-8); used when
    /// <paramref name="addRangeAvailable"/> is false.
    /// </param>
    /// <param name="addRangeAvailable">
    /// AddRange decided at Analyze: List&lt;T&gt; temps (array/Span/interface/
    /// builder targets) or concrete temps with a symbol-compatible AddRange.
    /// </param>
    public static (IReadOnlyList<StatementSyntax> Statements, SpreadExpansion Expansion)
        BuildExpansion(
            SpreadSourceKind kind,
            ExpressionSyntax source,
            ExpressionSyntax tempReceiver,
            string loopVariable,
            bool addRangeAvailable)
    {
        // String and Span can never use AddRange: List<char>.AddRange(string)
        // does not compile (string is not IEnumerable<char>), and Span is a
        // ref struct that does not implement IEnumerable at all.
        var useAddRange = addRangeAvailable
                          && kind is SpreadSourceKind.Array or SpreadSourceKind.Enumerable;

        if (useAddRange)
        {
            var addRange = SyntaxFactory.ExpressionStatement(
                MemberInvoke(tempReceiver, "AddRange", source));
            return (new StatementSyntax[] { addRange }, SpreadExpansion.AddRange);
        }

        var loopAdd = SyntaxFactory.ExpressionStatement(
            MemberInvoke(
                tempReceiver, "Add",
                SyntaxFactory.IdentifierName(loopVariable)));
        var foreachStatement = SyntaxFactory.ForEachStatement(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(loopVariable),
            source,
            loopAdd);

        var expansion = kind == SpreadSourceKind.Span
            ? SpreadExpansion.SpanForeach
            : SpreadExpansion.EnumerableForeach;
        return (new StatementSyntax[] { foreachStatement }, expansion);
    }

    /// <summary><c>receiver.Name(arg)</c>.</summary>
    public static InvocationExpressionSyntax MemberInvoke(
        ExpressionSyntax receiver, string name, ExpressionSyntax arg)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.IdentifierName(name)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(arg))));

    private static bool ImplementsEnumerable(ITypeSymbol type)
        => type.SpecialType == SpecialType.System_Collections_IEnumerable
           || type.AllInterfaces.Any(i =>
               i.SpecialType == SpecialType.System_Collections_IEnumerable);
}
