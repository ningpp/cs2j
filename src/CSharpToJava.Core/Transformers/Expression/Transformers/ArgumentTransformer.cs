using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Utilities;
using System.Collections.Immutable;
using System.Linq;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>Transforms C# argument lists; ref/out and coercion rules live in partials.</summary>
public partial class ArgumentTransformer
{
    private static readonly Lazy<ArgumentTransformer> _instance = new(() => new());
    public static ArgumentTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => ExpressionTransformerFacade.Instance.Transform(node, context);

    public static string TransformArgumentList(
        ArgumentListSyntax? argumentList,
        ConversionContext context,
        IExpressionTransformer transformer,
        int argStartIndex = 0,
        IMethodSymbol? methodSymbol = null,
        int maxArgCount = -1)
    {
        if (argumentList == null || argumentList.Arguments.Count <= argStartIndex)
        {
            return string.Empty;
        }

        var args = argumentList.Arguments;
        var effectiveEnd = maxArgCount >= 0 ? Math.Min(args.Count, maxArgCount) : args.Count;
        IReadOnlyList<ArgumentSyntax> relevantArgs = (argStartIndex > 0 || effectiveEnd < args.Count)
            ? args.Skip(argStartIndex).Take(effectiveEnd - argStartIndex).ToList()
            : args.ToList();

        var orderedArgs = ReorderNamedArguments(relevantArgs, argumentList, context);
        var parameters = methodSymbol?.Parameters;

        var transformed = orderedArgs
            .Select((arg, index) => TransformArgument(arg, context, transformer, parameters, index))
            .ToList();

        return string.Join(", ", transformed);
    }

    private static string TransformArgument(
        ArgumentSyntax arg,
        ConversionContext context,
        IExpressionTransformer transformer,
        ImmutableArray<IParameterSymbol>? parameters,
        int index)
    {
        var parameter = GetParameter(parameters, index);
        var result = TransformSingleArgument(arg, context, transformer, parameter);
        result = ApplyStructValueCopyIfNeeded(arg, result, parameter, context);

        if (!parameters.HasValue || context.SemanticModel == null)
            return CoerceEnumerableStreamFallback(arg, result, context);

        return parameter == null
            ? result
            : CoerceArgumentType(arg, result, parameter, context);
    }

    /// <summary>
    /// Full argument-list text for an invocation, honoring ref/out holder
    /// wrapping (including pending pre/post statements registered on the
    /// context). Used by the invocation IR path, which otherwise transforms
    /// <see cref="ArgumentSyntax.Expression"/> directly and would silently drop
    /// the ref/out semantics.
    /// </summary>
    public static string TransformInvocationArguments(
        InvocationExpressionSyntax invocation, ConversionContext context)
    {
        var method = (context.GetSymbolInfo(invocation).Symbol as IMethodSymbol)
                     ?? (context.GetSymbolInfo(invocation).CandidateSymbols.FirstOrDefault() as IMethodSymbol);
        return TransformArgumentList(invocation.ArgumentList, context,
            ExpressionTransformerFacade.Instance, methodSymbol: method);
    }

    /// <summary>
    /// True when the invocation passes (or, per the resolved symbol, should
    /// pass) any argument by ref/out — the IR path must then route through
    /// <see cref="TransformInvocationArguments"/> instead of bare expression
    /// transforms.
    /// </summary>
    public static bool HasByRefArguments(InvocationExpressionSyntax invocation, IMethodSymbol? method)
    {
        if (invocation.ArgumentList.Arguments.Any(a =>
                a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                || a.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)))
            return true;
        return method?.Parameters.Any(p => p.RefKind is RefKind.Out or RefKind.Ref) == true;
    }

    private static IParameterSymbol? GetParameter(ImmutableArray<IParameterSymbol>? parameters, int index)
    {
        if (!parameters.HasValue)
            return null;

        var p = parameters.Value;
        if (index >= p.Length && p.Length > 0 && p[^1].IsParams)
            index = p.Length - 1;
        return index < p.Length ? p[index] : null;
    }

    private static IReadOnlyList<ArgumentSyntax> ReorderNamedArguments(
        IReadOnlyList<ArgumentSyntax> args,
        ArgumentListSyntax argumentList,
        ConversionContext context)
    {
        if (!args.Any(a => a.NameColon != null))
            return args.ToList();
        if (context.SemanticModel == null || argumentList.Parent == null)
            return args.ToList();

        var symbolInfo = context.GetSymbolInfo(argumentList.Parent);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return args.ToList();

        var parameters = methodSymbol.Parameters;
        var result = new ArgumentSyntax?[parameters.Length];

        foreach (var arg in args)
        {
            if (arg.NameColon == null)
                continue;

            var paramName = arg.NameColon.Name.Identifier.Text;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == paramName)
                {
                    result[i] = arg;
                    break;
                }
            }
        }

        var positional = args.Where(a => a.NameColon == null).ToList();
        int positionalIndex = 0;
        for (int i = 0; i < result.Length && positionalIndex < positional.Count; i++)
        {
            if (result[i] == null)
                result[i] = positional[positionalIndex++];
        }

        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == null && parameters[i].IsOptional)
                result[i] = CreateDefaultArgumentSyntax(parameters[i]);
        }

        return result.Where(a => a != null).Cast<ArgumentSyntax>().ToList();
    }

    private static ArgumentSyntax CreateDefaultArgumentSyntax(IParameterSymbol parameter)
    {
        string defaultText;
        if (!parameter.HasExplicitDefaultValue)
        {
            defaultText = "null";
        }
        else
        {
            var value = parameter.ExplicitDefaultValue;
            if (value == null)
            {
                defaultText = "null";
            }
            else if (value is bool b)
            {
                defaultText = b ? "true" : "false";
            }
            else if (value is string s)
            {
                defaultText = SyntaxFactory.Literal(s).ToFullString();
            }
            else if (value is char c)
            {
                defaultText = SyntaxFactory.Literal(c).ToFullString();
            }
            else if (parameter.Type.TypeKind == TypeKind.Enum && parameter.Type is INamedTypeSymbol enumType)
            {
                var member = enumType.GetMembers()
                    .OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
                defaultText = member != null
                    ? $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{member.Name}"
                    : value.ToString()!;
            }
            else
            {
                defaultText = value.ToString()!;
            }
        }

        return SyntaxFactory.Argument(SyntaxFactory.ParseExpression(defaultText));
    }

    private static string TransformSingleArgument(
        ArgumentSyntax arg,
        ConversionContext context,
        IExpressionTransformer transformer,
        IParameterSymbol? parameterSymbol = null)
    {
        var refKind = arg.RefKindKeyword.Kind();
        if (refKind == SyntaxKind.None && parameterSymbol != null)
        {
            refKind = parameterSymbol.RefKind switch
            {
                RefKind.Out => SyntaxKind.OutKeyword,
                RefKind.Ref => SyntaxKind.RefKeyword,
                RefKind.In => SyntaxKind.InKeyword,
                _ => SyntaxKind.None
            };
        }

        var argExpr = arg.Expression is PostfixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.SuppressNullableWarningExpression
        } nullForgiving
            ? nullForgiving.Operand
            : arg.Expression;

        return refKind switch
        {
            SyntaxKind.OutKeyword => TransformOutArgument(arg, argExpr, context, transformer, parameterSymbol),
            SyntaxKind.RefKeyword => TransformRefArgument(arg, argExpr, context, transformer, parameterSymbol),
            _ => transformer.Transform(arg.Expression, context)
        };
    }

    private static string ApplyStructValueCopyIfNeeded(
        ArgumentSyntax arg,
        string transformedExpr,
        IParameterSymbol? targetParam,
        ConversionContext context)
    {
        if (context.SemanticModel == null || targetParam == null)
            return transformedExpr;
        if (arg.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
            return transformedExpr;

        var argType = context.GetTypeInfo(arg.Expression).Type;
        if (!StructCloneHelper.IsUserDefinedStruct(argType)
            || !StructCloneHelper.IsUserDefinedStruct(targetParam.Type))
        {
            return transformedExpr;
        }
        if (StructCloneHelper.IsValueParamEffectivelyReadOnly(targetParam, context.ProjectCompilation))
            return transformedExpr;
        if (StructCloneHelper.IsCloneableTemporary(arg.Expression) || arg.Expression is ElementAccessExpressionSyntax)
            return transformedExpr;

        return StructCloneHelper.BuildCloneExpression(arg.Expression, transformedExpr);
    }
}
