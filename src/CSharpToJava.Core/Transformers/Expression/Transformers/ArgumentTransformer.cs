using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;
using System.Linq;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Utility class for argument list transformation.
/// Not dispatched through ExpressionTransformerRegistry; used directly by other transformers.
/// </summary>
public class ArgumentTransformer
{
    // ArgumentTransformer is a special case - it doesn't handle specific SyntaxKinds directly
    // but is used by other transformers for argument list transformation.
    // Touched by ExpressionTransformerRegistry to allow unit testing and explicit invocation.
    static ArgumentTransformer() { }

    private static readonly Lazy<ArgumentTransformer> _instance = new(() => new());
    public static ArgumentTransformer Instance => _instance.Value;

    /// <summary>
    /// Transforms an argument expression directly via the expression facade.
    /// ArgumentTransformer is a utility class; use TransformArgumentList for full argument lists.
    /// </summary>
    public string Transform(ExpressionSyntax node, ConversionContext context)
        => ExpressionTransformerFacade.Instance.Transform(node, context);

    /// <summary>
    /// Transforms an argument list to Java code.
    /// Named arguments are reordered to positional order using the semantic model when available.
    /// out/ref/in arguments are handled with the holder-object pattern or pass-by-value respectively.
    /// </summary>
    /// <param name="argStartIndex">
    /// Issue 5: index of the first argument to include. Pass 1 when in the static-extension-receiver
    /// call path to skip the receiver that has already been prepended to the argument list.
    /// </param>
    public static string TransformArgumentList(ArgumentListSyntax? argumentList, ConversionContext context, IExpressionTransformer transformer, int argStartIndex = 0)
    {
        if (argumentList == null) return "";

        var args = argumentList.Arguments;
        if (args.Count <= argStartIndex) return "";

        // Issue 5: slice from argStartIndex when in the static-extension-receiver path
        IReadOnlyList<ArgumentSyntax> relevantArgs = argStartIndex > 0
            ? args.Skip(argStartIndex).ToList()
            : args.ToList();

        var orderedArgs = ReorderNamedArguments(relevantArgs, argumentList, context);
        var transformed = orderedArgs.Select(arg => TransformSingleArgument(arg, context, transformer));
        return string.Join(", ", transformed);
    }

    /// <summary>
    /// Reorders named arguments to match the target method's positional parameter order.
    /// Returns the original order when the semantic model is unavailable or the symbol cannot be resolved.
    /// </summary>
    private static IReadOnlyList<ArgumentSyntax> ReorderNamedArguments(
        IReadOnlyList<ArgumentSyntax> args,
        ArgumentListSyntax argumentList,
        ConversionContext context)
    {
        if (!args.Any(a => a.NameColon != null))
            return args.ToList();

        if (context.SemanticModel == null || argumentList.Parent == null)
            return args.ToList();

        var symbolInfo = context.SemanticModel.GetSymbolInfo(argumentList.Parent);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return args.ToList();

        var parameters = methodSymbol.Parameters;
        var result = new ArgumentSyntax?[parameters.Length];

        // Place named arguments at their declared parameter positions
        foreach (var arg in args)
        {
            if (arg.NameColon == null) continue;
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

        // Fill remaining slots with positional arguments in order
        var positional = args.Where(a => a.NameColon == null).ToList();
        int pi = 0;
        for (int i = 0; i < result.Length && pi < positional.Count; i++)
        {
            if (result[i] == null)
                result[i] = positional[pi++];
        }

        return result.Where(a => a != null).Cast<ArgumentSyntax>().ToList();
    }

    /// <summary>
    /// Transforms a single argument to its Java representation.
    /// Handles ref/out/in modifiers; out var uses the holder-object pattern via pre-statements.
    /// </summary>
    private static string TransformSingleArgument(ArgumentSyntax arg, ConversionContext context, IExpressionTransformer transformer)
    {
        var refKind = arg.RefKindKeyword.Kind();

        if (refKind == SyntaxKind.OutKeyword)
        {
            // out _ (discard) — pass a scratch one-element array; the result is intentionally unused
            if (arg.Expression is DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax })
                return "new Object[1]";

            // Bare _ identifier used as a discard
            if (arg.Expression is IdentifierNameSyntax { Identifier.Text: "_" })
                return "new Object[1]";

            // out var result — holder-object pattern: declare Type[] _resultHolder = new Type[1]; before the call
            if (arg.Expression is DeclarationExpressionSyntax outDecl &&
                outDecl.Designation is SingleVariableDesignationSyntax svd)
            {
                var varName = svd.Identifier.Text;
                var holderName = $"_{varName}Holder";
                var javaType = ResolveOutVarType(outDecl, context);
                context.AddPreStatement($"{javaType}[] {holderName} = new {javaType}[1]");
                return holderName;
            }

            // out existingVar — wrap in a typed holder array
            if (arg.Expression is IdentifierNameSyntax ident)
            {
                var varName = ident.Identifier.Text;
                var holderName = $"_{varName}Holder";
                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.SemanticModel.GetTypeInfo(ident);
                    if (typeInfo.Type != null)
                        javaType = context.MapType(typeInfo.Type);
                }
                context.AddPreStatement($"{javaType}[] {holderName} = new {javaType}[1]");
                return holderName;
            }

            // Fallback for other out expressions
            context.Diagnostics.Warning("out parameter has no direct Java equivalent", arg.GetLocation());
            return $"/* out */ {transformer.Transform(arg.Expression, context)}";
        }

        if (refKind == SyntaxKind.RefKeyword || refKind == SyntaxKind.InKeyword)
        {
            // Java has no ref/in semantics — pass the value directly
            context.Diagnostics.Warning("ref/in parameter has no direct Java equivalent; passing by value", arg.GetLocation());
            return transformer.Transform(arg.Expression, context);
        }

        return transformer.Transform(arg.Expression, context);
    }

    /// <summary>
    /// Resolves the Java type name for an out var declaration.
    /// Uses the semantic model when available; falls back to syntax-based mapping.
    /// </summary>
    private static string ResolveOutVarType(DeclarationExpressionSyntax decl, ConversionContext context)
    {
        if (context.SemanticModel != null)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(decl.Type);
            if (typeInfo.Type != null)
                return context.MapType(typeInfo.Type);
        }
        return context.MapTypeFromSyntax(decl.Type);
    }
}
